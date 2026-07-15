# ZMemoLibrary

ZMemoLibrary is a small ASP.NET Core web application for securely browsing, searching, inspecting, uploading, downloading, and deleting curated Markdown knowledge files used with chat-based LLMs.

The Markdown files remain the source of truth. The application scans them into an in-memory catalogue and uses a shared passcode to protect the webpage and every API endpoint.

## Features

- First-run web setup for one shared passcode.
- Secure passcode login with an encrypted authentication cookie.
- Settings and logout controls in the webpage footer.
- Login attempt rate limiting.
- Recursively scans a configured folder for `.md` knowledge files.
- Parses a strict plain-text knowledge header followed by a Markdown body.
- Groups physical versions by their internal GUID.
- Resolves the current version using:
  - internal `UPDATED` date;
  - file modified time in UTC;
  - file creation time in UTC.
- Detects byte-identical duplicate files and unresolved version ties.
- Shows the complete current catalogue immediately after login.
- Searches current titles, keywords, summaries, Markdown bodies, and GUIDs.
- Supports click, Ctrl/Command+Click, Shift+Click, and Ctrl/Command+A selection.
- Copies selected catalogue summaries with Ctrl/Command+C.
- Highlights visible knowledge entries connected to selected entries.
- Renders Markdown in the built-in version viewer.
- Exposes retained historical versions for explicit viewing and downloading.
- Downloads each selected Markdown file separately without creating a ZIP.
- Uploads Markdown knowledge files through the webpage or drag and drop.
- Deletes selected knowledge series by moving their files to the server's `Deleted` folder for manual inspection.
- Provides embedded prompts for creating, updating, and connecting knowledge files.
- Provides day and night themes, with the initial theme chosen from local time.
- Displays scan errors without preventing valid files from loading.

## Technology

- .NET 8
- ASP.NET Core minimal APIs
- ASP.NET Core cookie authentication
- PBKDF2-SHA256 passcode hashing
- Plain HTML, CSS, and JavaScript
- No database
- No frontend framework or build pipeline
- No LLM, RAG, embedding, or vector-search dependency

## Project Structure

```text
ZMemoLibrary/
├── AppData/                         # Generated locally; never commit
│   ├── access.json                  # Passcode hash and session version
│   └── settings.json                # Knowledge directory and HTTP port
├── Endpoints/
│   ├── DownloadEndpoints.cs
│   ├── KnowledgeEndpoints.cs
│   ├── LibraryManagementEndpoints.cs
│   └── PromptEndpoints.cs
├── Knowledge/
│   ├── KnowledgeFileParser.cs
│   ├── KnowledgeLibrary.cs
│   └── KnowledgeModels.cs
├── Prompts/
│   ├── create-knowledge.md
│   ├── find-connections.md
│   └── update-knowledge.md
├── Properties/
│   └── launchSettings.json
├── wwwroot/
│   ├── app.js
│   └── index.html
├── Program.cs
├── ZMemoLibrary.csproj
├── ZMemoLibrary.http
└── ZMemoLibrary.slnx
```

`AppData/access.json` is created after passcode setup. `AppData/settings.json` is created after application setup. Both are local runtime files excluded by `.gitignore`; never commit or package them.

## Access Protection

### First-run setup

When no access configuration exists, opening the application displays **Set up ZMemoLibrary access**.

1. Enter a shared passcode of at least 12 characters.
2. Confirm the passcode.
3. Select **Set passcode**.
4. The application stores a salted PBKDF2-SHA256 hash and signs the browser in.

The original passcode is never stored. The local access file contains only the salt, derived hash, iteration count, and a random session version:

```text
AppData/access.json
```

Complete first-run setup before exposing a new deployment to an untrusted network. While no passcode exists, the first visitor who reaches the setup page can create it.

### Normal login

After setup, unauthenticated visitors are redirected to `/access`. A correct passcode creates an encrypted, HTTP-only authentication cookie. The cookie uses `SameSite=Strict`, lasts for up to 30 days, and renews while actively used.

All application pages and API endpoints require authentication. API requests with an expired or invalid session receive `401 Unauthorized`, and the browser returns to the access page.

Login and passcode-change submissions are limited to five attempts per minute by the application.

### Application settings

Select **Settings** beside **Z-Memo-Library 2026** in the bottom row. The protected settings page manages:

- the knowledge library directory containing the Markdown files;
- the HTTP listening port;
- the shared passcode.

Every settings save requires the current passcode.

The knowledge library directory must already exist. It is applied immediately and triggers a rescan. The port must be between `1024` and `65535`; a port change is saved but takes effect only after restarting ZMemoLibrary.

Leave the new-passcode fields empty to preserve the current passcode. Changing the passcode creates a new session version and immediately invalidates every existing authentication cookie, including the browser that performed the change. Sign in again with the new passcode.

Runtime settings are stored in:

```text
AppData/settings.json
```

The Settings page is the only source of application-specific runtime settings.

### Logout

Select **Logout** beside **Z-Memo-Library 2026** in the bottom row. This removes the current browser's authentication cookie but does not affect other signed-in devices.

### Reset after losing the passcode

There is intentionally no public recovery endpoint. To reset access:

1. Stop the application.
2. Delete `AppData/access.json` from the application content root.
3. Start the application.
4. Open the setup page and create a new passcode immediately.

Deleting the file returns the application to first-run setup and invalidates all previous sessions.

### Deployment requirements

- Serve the application through HTTPS when it is reachable outside a trusted local network.
- Keep `AppData/access.json` outside source control and inaccessible from the public web root.
- Preserve the `AppData` directory across application updates if the application is redeployed by replacing its publish directory. This retains both access protection and runtime settings.
- Restrict filesystem access to the operating-system account that runs ZMemoLibrary.
- Back up the `AppData` directory if preserving the current passcode and runtime settings across server recovery matters.

## Knowledge File Format

Every valid knowledge file must begin with this exact header structure:

```text
BEGIN_KNOWLEDGE_HEADER
ID: 00000000-0000-0000-0000-000000000000
TITLE: Short human-readable title
CREATED: YYYY-MM-DD
UPDATED: YYYY-MM-DD
SUMMARY: One to three sentences explaining when this knowledge is useful.
KEYWORDS: keyword one; keyword two; keyword three
CONNECTIONS: 11111111-1111-1111-1111-111111111111 | Relationship reason
END_KNOWLEDGE_HEADER

# Markdown Body
The normal Markdown content begins here.
```

When there are no known connections, use:

```text
CONNECTIONS: none | No known related knowledge file.
```

Header requirements:

- Fields must appear exactly once and in the displayed order.
- Each field must remain on one physical line.
- `ID` must be a valid GUID.
- `CREATED` and `UPDATED` must use `YYYY-MM-DD`.
- Keywords and connections are separated with semicolons.
- A connection GUID and its reason are separated with `|`.
- Files must be valid UTF-8.

## Configuration

ZMemoLibrary keeps its application-specific runtime settings in:

```text
AppData/settings.json
```

If `AppData/settings.json` is missing or invalid, ZMemoLibrary starts temporarily with:

```text
HTTP port: 9000
Application state: setup required
```

After creating the passcode, the authenticated browser is redirected to `/setup`. Enter an existing absolute server path for the knowledge library and choose an HTTP port from `1024` to `65535`. Saving setup creates `AppData/settings.json`, scans the selected directory, and opens the library. The knowledge directory takes effect immediately; a changed port takes effect after restarting the application.

The repository `.gitignore` excludes both the default `KnowledgeFiles/` directory and the generated `AppData/` directory.

## Run Locally

Requirements:

- .NET 8 SDK

From the repository root:

```powershell
dotnet restore .\ZMemoLibrary.csproj
dotnet run --project .\ZMemoLibrary.csproj
```

Alternatively, open `ZMemoLibrary.slnx` or `ZMemoLibrary.csproj` in Visual Studio and start debugging. Visual Studio does not open a fixed URL because the runtime port is stored locally; use `http://localhost:9000` while setup is incomplete.

ZMemoLibrary listens on its configured HTTP port. On a new installation, open:

```text
http://localhost:9000
```

On first launch, create the shared passcode in the browser. Later launches show the normal unlock page. For an internet-facing deployment, terminate HTTPS at a reverse proxy or Cloudflare Tunnel and use the local HTTP endpoint as its origin.

## Usage

1. Start the application.
2. Open the configured HTTP address; a new installation uses `http://localhost:9000`.
3. Create the shared passcode on first launch, or enter the existing passcode.
4. Open **Settings** and choose the knowledge library directory and HTTP port.
5. Browse or search the current catalogue.
6. Select entries with normal desktop selection controls.
7. Press Ctrl/Command+C to copy their displayed catalogue information.
8. Choose **Download Selected** to download the represented Markdown files individually.
9. Double-click a catalogue row to view its rendered Markdown.
10. Expand the version count to inspect retained historical versions.
11. Choose **Upload Markdown** or drag `.md` files onto the page to import them.
12. Choose **Delete Selected** to move all versions of selected series to the server's `Deleted` folder.
13. Choose **Rescan** after changing source files outside the application.

A browser may request permission when several selected files are downloaded separately.

## Version Resolution

Files with the same header `ID` belong to one knowledge series, regardless of filename or path.

The current version is selected by:

1. latest internal `UPDATED` date;
2. latest filesystem modified time in UTC;
3. latest filesystem creation time in UTC.

If multiple distinct files remain tied, the series is marked ambiguous instead of selecting one arbitrarily.

Each physical version receives a SHA-256 key. Before download or mutation, ZMemoLibrary verifies that the physical file still matches the scanned version. If it changed, rescan before continuing.

## File Mutation Behaviour

ZMemoLibrary does not rewrite the content of existing knowledge files.

It can:

- import new Markdown files into the configured library;
- download unchanged copies;
- move all physical versions of a selected knowledge series into the server's `Deleted` folder.

Deletion is deliberately recoverable at the filesystem level: files are moved rather than permanently erased.

## Deployment

ZMemoLibrary is a web application hosted by ASP.NET Core Kestrel. A self-contained publish includes the web server, application assemblies, browser assets, and the required .NET runtime. Users run the platform executable and access the interface through a browser.

### Supported release targets

The recommended release targets are:

- `win-x64` — 64-bit Windows;
- `linux-x64` — 64-bit Intel/AMD Linux;
- `linux-arm64` — 64-bit ARM Linux;
- `osx-arm64` — Apple Silicon macOS.

A separate self-contained publish is required for each target. The target machine does not need a separately installed .NET runtime.

### Clean release contents

Each release archive should contain the complete publish output, including:

```text
ZMemoLibrary or ZMemoLibrary.exe
ZMemoLibrary.dll
supporting runtime files
wwwroot/
README.md
```

Never include:

```text
AppData/
KnowledgeFiles/
Deleted/
bin/
obj/
publish output from another runtime
```

`AppData` is created locally during first-run setup. A clean distribution must not contain a passcode verifier, session version, knowledge-library path, or saved port.

### Manual self-contained publishing

Run these commands from the repository root.

#### Windows x64

```powershell
dotnet publish .\ZMemoLibrary.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o .\publish\win-x64
```

Start it with:

```powershell
Set-Location .\publish\win-x64
.\ZMemoLibrary.exe
```

#### Linux x64

```powershell
dotnet publish .\ZMemoLibrary.csproj `
    -c Release `
    -r linux-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o .\publish\linux-x64
```

#### Linux ARM64

```powershell
dotnet publish .\ZMemoLibrary.csproj `
    -c Release `
    -r linux-arm64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o .\publish\linux-arm64
```

#### macOS ARM64

```powershell
dotnet publish .\ZMemoLibrary.csproj `
    -c Release `
    -r osx-arm64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o .\publish\osx-arm64
```

On Linux or macOS, make the executable runnable and start it from the publish directory:

```bash
chmod +x ZMemoLibrary
./ZMemoLibrary
```

### First run after deployment

A clean deployment has no `AppData` directory.

1. Start the platform executable from its publish directory.
2. Open `http://localhost:9000` or replace `localhost` with the server address.
3. Create the shared passcode.
4. Enter an existing absolute knowledge-library directory on the server.
5. Confirm the HTTP port and save setup.
6. If the selected port differs from `9000`, restart ZMemoLibrary and browse to the new port.

Path examples:

```text
Windows: B:\KnowledgeFiles
Linux:   /srv/zmemolibrary/knowledge
macOS:   /Users/zach/KnowledgeFiles
```

The directory field always refers to the filesystem of the machine running ZMemoLibrary, not the browser device.

### Reverse proxy

ZMemoLibrary listens on plain HTTP. HTTPS, domain routing, and external exposure should be handled separately by a reverse proxy such as Caddy.

Example upstream:

```text
http://127.0.0.1:9000
```

Replace `9000` with the saved application port.

### Packaging a release

Windows publishes can be packaged with PowerShell:

```powershell
Compress-Archive `
    -Path .\publish\win-x64\* `
    -DestinationPath .\publish\ZMemoLibrary-win-x64.zip `
    -Force
```

Linux and macOS releases are preferably distributed as `.tar.gz` archives so Unix executable permissions can be preserved. If a ZIP is used, the user may need to run `chmod +x ZMemoLibrary` after extraction.

### Upgrading an existing deployment

Preserve the deployed `AppData` directory when replacing application files. It contains the local passcode verifier and runtime settings.

Recommended upgrade sequence:

1. Stop ZMemoLibrary.
2. Back up `AppData`.
3. Replace the executable, assemblies, runtime files, and `wwwroot` with the new publish output.
4. Restore or retain the existing `AppData` directory.
5. Start ZMemoLibrary and verify the catalogue.

Do not copy a development or release-build `AppData` directory over an existing deployment.

## Privacy Before Publishing

Before pushing to a public Git repository:

- Do not commit private knowledge files.
- Do not commit or package any file under `AppData/`; it contains the passcode verifier, session state, machine-specific knowledge directory, and HTTP port.
- Do not commit certificates, keys, secrets, publish profiles, logs, or build output.
- Check staged files before the first commit:

```powershell
git status
git diff --cached
```

## Current Scope

ZMemoLibrary intentionally does not include:

- usernames or individual user accounts;
- roles or per-user permissions;
- self-registration or password recovery;
- a database;
- knowledge editing in the browser;
- LLM calls;
- RAG or vector search;
- automatic filesystem watching;
- multiple knowledge roots;
- deployment automation.

All authenticated visitors share the same passcode and receive the same application access.
