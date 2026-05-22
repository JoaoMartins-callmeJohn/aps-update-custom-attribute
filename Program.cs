using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string RedirectUri  = "http://localhost:8080/";
const string AuthorizeUrl = "https://developer.api.autodesk.com/authentication/v2/authorize";
const string TokenUrl     = "https://developer.api.autodesk.com/authentication/v2/token";
const string Scopes       = "data:read data:write account:read";

// ── Helpers ───────────────────────────────────────────────────────────────────

static string Ask(string prompt)
{
    Console.Write(prompt);
    return Console.ReadLine()?.Trim() ?? "";
}

static int AskChoice(string prompt, string[] options)
{
    Console.WriteLine(prompt);
    for (int i = 0; i < options.Length; i++)
        Console.WriteLine($"  {i + 1}. {options[i]}");
    while (true)
    {
        Console.Write("Choice: ");
        if (int.TryParse(Console.ReadLine(), out int n) && n >= 1 && n <= options.Length)
            return n - 1;
        Console.WriteLine("  Invalid, try again.");
    }
}

static string GenerateCodeVerifier()
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
    return new string(RandomNumberGenerator.GetBytes(64).Select(b => chars[b % chars.Length]).ToArray());
}

static string GenerateCodeChallenge(string verifier)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
    return Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

// ── 1. Authenticate via PKCE ──────────────────────────────────────────────────

var clientId  = Ask("Client ID: ");
var verifier  = GenerateCodeVerifier();
var challenge = GenerateCodeChallenge(verifier);

var loginUrl = $"{AuthorizeUrl}?response_type=code&client_id={clientId}" +
               $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
               $"&scope={Uri.EscapeDataString(Scopes)}" +
               $"&code_challenge={challenge}&code_challenge_method=S256&prompt=login";

Console.WriteLine("\nOpening browser for sign-in...");
Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });

var listener = new HttpListener();
listener.Prefixes.Add(RedirectUri);
listener.Start();
Console.WriteLine($"Waiting for callback on {RedirectUri}...");

var ctx      = await listener.GetContextAsync();
var authCode = ctx.Request.QueryString["code"]!;
var html     = Encoding.UTF8.GetBytes("<html><body>Authentication complete. You can close this tab.</body></html>");
ctx.Response.ContentLength64 = html.Length;
await ctx.Response.OutputStream.WriteAsync(html);
ctx.Response.OutputStream.Close();
listener.Stop();

using var http = new HttpClient();

var tokenRes = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"]    = "authorization_code",
    ["client_id"]     = clientId,
    ["code"]          = authCode,
    ["code_verifier"] = verifier,
    ["redirect_uri"]  = RedirectUri,
}));
tokenRes.EnsureSuccessStatusCode();

var tokenJson   = JsonNode.Parse(await tokenRes.Content.ReadAsStringAsync())!;
var accessToken = tokenJson["access_token"]!.GetValue<string>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
Console.WriteLine("Signed in.\n");

// ── 2. Project & folder ───────────────────────────────────────────────────────

var projectId = Ask("Project ID (with b. prefix): ");
var folderId  = Ask("Folder URN: ");
Console.WriteLine();

// ── 3. Fetch project users ────────────────────────────────────────────────────

// ACC admin API uses project ID without the "b." prefix
var accProjectId = projectId.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? projectId[2..] : projectId;

var usersRes = await http.GetAsync(
    $"https://developer.api.autodesk.com/construction/admin/v1/projects/{accProjectId}/users?limit=200");
usersRes.EnsureSuccessStatusCode();

var usersArray = JsonNode.Parse(await usersRes.Content.ReadAsStringAsync())!["results"]!.AsArray();

// ── 4. Role-based or user-based ───────────────────────────────────────────────

var targetTypeIdx = AskChoice("Apply permission to:", ["User", "Role"]);

string subjectId;
string subjectType;
string autodeskId = "";

if (targetTypeIdx == 0)
{
    var users = usersArray
        .Where(u => u?["id"]?.GetValue<string>() is not null)
        .Select(u => new
        {
            Id         = u!["id"]!.GetValue<string>(),
            AutodeskId = u["autodeskId"]?.GetValue<string>() ?? "",
            Label      = $"{u["name"]?.GetValue<string>()} <{u["email"]?.GetValue<string>()}>",
        }).ToList();

    var idx = AskChoice("\nSelect user:", users.Select(u => u.Label).ToArray());
    subjectId   = users[idx].Id;
    autodeskId  = users[idx].AutodeskId;
    subjectType = "USER";
}
else
{
    var roles = usersArray
        .SelectMany(u => u!["roles"]?.AsArray() ?? new JsonArray())
        .Where(r => r != null)
        .GroupBy(r => r!["id"]!.GetValue<string>())
        .Select(g => new { Id = g.Key, Name = g.First()!["name"]!.GetValue<string>() })
        .OrderBy(r => r.Name)
        .ToList();

    var idx = AskChoice("\nSelect role:", roles.Select(r => r.Name).ToArray());
    subjectId   = roles[idx].Id;
    subjectType = "ROLE";
}

// ── 5. Permission level ───────────────────────────────────────────────────────

var levels = new (string Label, string[] Actions)[]
{
    ("View Only",                                       ["VIEW", "COLLABORATE"]),
    ("View/Download",                                   ["VIEW", "DOWNLOAD", "COLLABORATE"]),
    ("View/Download + Publish Markups",                 ["VIEW", "DOWNLOAD", "COLLABORATE", "PUBLISH_MARKUP"]),
    ("View/Download + Publish Markups + Upload",        ["PUBLISH", "VIEW", "DOWNLOAD", "COLLABORATE", "PUBLISH_MARKUP"]),
    ("View/Download + Publish Markups + Upload + Edit", ["PUBLISH", "VIEW", "DOWNLOAD", "COLLABORATE", "PUBLISH_MARKUP", "EDIT"]),
    ("Full Control",                                    ["PUBLISH", "VIEW", "DOWNLOAD", "COLLABORATE", "PUBLISH_MARKUP", "EDIT", "CONTROL"]),
};

var levelIdx = AskChoice("\nSelect permission level:", levels.Select(l => l.Label).ToArray());
var actions  = levels[levelIdx].Actions;

// ── 6. Apply folder permissions ───────────────────────────────────────────────

// autodeskId (non-UUID) is sent alongside subjectId (UUID) for USER type
object entry = subjectType == "USER"
    ? new { subjectId, autodeskId, subjectType, actions, inheritActions = actions }
    : new { subjectId, subjectType, actions, inheritActions = actions };

// The folder URN must not be URL-encoded — the API expects it verbatim in the path
var permUrl = $"https://developer.api.autodesk.com/bim360/docs/v1/projects/{projectId}" +
              $"/folders/{folderId}/permissions:batch-create";

var permRes = await http.PostAsync(permUrl,
    new StringContent(JsonSerializer.Serialize(new[] { entry }), Encoding.UTF8, "application/json"));

var permBody = await permRes.Content.ReadAsStringAsync();
if (!permRes.IsSuccessStatusCode)
{
    Console.WriteLine($"\nError {(int)permRes.StatusCode}: {permBody}");
    return;
}

Console.WriteLine("\nPermissions applied successfully.");
