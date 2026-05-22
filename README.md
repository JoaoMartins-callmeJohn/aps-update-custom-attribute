# aps-folder-permissions

## Introduction

Assigning folder permissions through the APS API requires juggling several different identifiers that are easy to confuse. This sample is a .NET 10 CLI that walks through the full flow interactively:

1. Authenticates the user via **3-legged OAuth with PKCE** (no client secret needed)
2. Asks for the target project and folder
3. Fetches all project members from the ACC Admin API
4. Lets you assign permissions to a specific **user** or **role**
5. Presents the documented permission levels by name and posts the correct action set to the BIM360 Document Management API

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An [APS application](https://aps.autodesk.com/myapps) with `http://localhost:8080/` added as a callback URL
- The signed-in user must have admin rights to the target ACC project

## Setup & Run

Clone this repository:

```bash
git clone https://github.com/autodesk-platform-services/aps-folder-permissions
cd aps-folder-permissions
```

Run the app:

```bash
dotnet run
```

The CLI will prompt for everything it needs — no environment variables or config files required.

## How It Works

```
Client ID: <paste your APS app client ID>

Opening browser for sign-in...
Waiting for callback on http://localhost:8080/...
Signed in.

Project ID (with b. prefix): b.xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
Folder URN: urn:adsk.wipprod:fs.folder:co.XXXXXXXXXXXXXXXXXXXX

Apply permission to:
  1. User
  2. Role
Choice: 1

Select user:
  1. Alice Smith <alice@company.com>
  2. Bob Jones <bob@company.com>
Choice: 1

Select permission level:
  1. View Only
  2. View/Download
  3. View/Download + Publish Markups
  4. View/Download + Publish Markups + Upload
  5. View/Download + Publish Markups + Upload + Edit
  6. Full Control
Choice: 3

Permissions applied successfully.
```

## IDs Reference

The BIM360/ACC APIs use several distinct identifiers that refer to the same user or resource in different ways. Getting the wrong one is the most common source of `400`/`403` errors.

### Project ID

Used in BIM360 API paths (`/bim360/docs/v1/projects/{projectId}/...`).

This is the project's hub ID **with the `b.` prefix** (e.g. `b.xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`). The prefix is literal — do not strip it for these endpoints.

```
b.c0337487-5b66-422b-a284-c273b424af54
└─ required prefix for BIM360 API paths
```

### ACC Project ID

Used in ACC Admin API paths (`/construction/admin/v1/projects/{projectId}/...`).

Same UUID as the Project ID above, but **without the `b.` prefix**. The sample strips it automatically when calling the project users endpoint.

```
c0337487-5b66-422b-a284-c273b424af54
└─ no prefix for ACC Admin API paths
```

### Folder URN

Used in BIM360 API paths as the `{folderId}` segment.

The URN is placed **verbatim** in the URL — do not URL-encode it. Encoding the colons (`:` → `%3A`) causes the API to reject the request.

```
urn:adsk.wipprod:fs.folder:co.9g7HeA2wRqOxLlgLJ40UGQ
```

### User `id` → `subjectId`

Used as `subjectId` in the folder permissions request body when `subjectType` is `"USER"`.

This is the **UUID-formatted member ID** returned as `id` in the `GET /construction/admin/v1/projects/{projectId}/users` response. The API validates that this field matches the UUID format and will reject non-UUID values.

```json
{ "subjectId": "684c4e47-7720-4961-b0e9-ff5966d82edb", ... }
```

### User `autodeskId`

Sent as a separate `autodeskId` field in the request body alongside `subjectId`.

This is the **non-UUID Autodesk account identifier** (e.g. `45GPJ4KAX789`) also returned by the users endpoint. It is not the `subjectId` — it is an additional field the API requires for user-based permissions.

```json
{ "subjectId": "684c4e47-...", "autodeskId": "45GPJ4KAX789", ... }
```

### Role ID → `subjectId`

Used as `subjectId` in the folder permissions request body when `subjectType` is `"ROLE"`.

Role IDs are UUIDs returned in the `roles` array of each user in the project users response. The sample deduplicates them across all users to build the role list.

```json
{ "subjectId": "09dd30da-6562-4a92-a383-49c9888ee49c", "subjectType": "ROLE", ... }
```

## License

This sample is licensed under the terms of the [MIT License](./LICENSE).

## Written by

Joao Martins, Developer Advocate [@autodesk-platform-services](https://github.com/autodesk-platform-services)
