# ZMemoLibrary

ZMemoLibrary is a small, read-only ASP.NET Core web application for browsing, searching, inspecting, selecting, and downloading curated Markdown knowledge files used with chat-based LLMs.

It keeps the Markdown files as the source of truth. The application scans them into an in-memory catalogue and never edits, renames, moves, or deletes the source files.

## Features

- Recursively scans a configured folder for `.md` knowledge files.
- Parses a strict plain-text knowledge header followed by a Markdown body.
- Groups physical versions by their internal GUID.
- Resolves the current version using:
  1. internal `UPDATED` date;
  2. file modified time in UTC;
  3. file creation time in UTC.
- Detects byte-identical duplicate files and unresolved version ties.
- Shows the complete current catalogue immediately on page load.
- Searches current titles, keywords, summaries, Markdown bodies, and GUIDs.
- Supports click, Ctrl/Command+Click, Shift+Click, and Ctrl/Command+A selection.
- Copies selected catalogue summaries with Ctrl/Command+C.
- Highlights visible knowledge entries connected to selected entries.
- Renders Markdown in the built-in version viewer.
- Exposes retained historical versions for explicit viewing and downloading.
- Downloads each selected Markdown file separately without creating a ZIP.
- Provides day and night themes, with the initial theme chosen from local time.
- Displays scan errors without preventing valid files from loading.

## Technology

- .NET 8
- ASP.NET Core minimal APIs
- Plain HTML, CSS, and JavaScript
- No database
- No frontend framework or build pipeline
- No LLM, RAG, embedding, or vector-search dependency

## Project Structure

```text
ZMemoLibrary/
├── Endpoints/
│   ├── DownloadEndpoints.cs
│   └── KnowledgeEndpoints.cs
├── Knowledge/
│   ├── KnowledgeFileParser.cs
│   ├── KnowledgeLibrary.cs
│   └── KnowledgeModels.cs
├── Properties/
│   └── launchSettings.json
├── wwwroot/
│   ├── app.js
│   └── index.html
├── appsettings.Development.json
├── appsettings.json
├── Program.cs
├── ZMemoLibrary.csproj
├── ZMemoLibrary.http
└── ZMemoLibrary.slnx
```

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

Set the knowledge-library root in `appsettings.json`:

```json
{
  "KnowledgeLibrary": {
    "RootPath": "KnowledgeFiles"
  }
}
```

`RootPath` may be relative to the application content directory or an absolute path.

For a machine-specific path, prefer an untracked `appsettings.Local.json` or an environment variable rather than committing a personal filesystem path.

ASP.NET Core environment-variable form:

```text
KnowledgeLibrary__RootPath=/path/to/knowledge/files
```

The repository `.gitignore` excludes the default `KnowledgeFiles/` directory so private knowledge content is not accidentally published with the application source.

## Run Locally

Requirements:

- .NET 8 SDK

From the repository root:

```powershell
dotnet restore .\ZMemoLibrary.csproj
dotnet run --project .\ZMemoLibrary.csproj
```

Alternatively, open `ZMemoLibrary.slnx` or `ZMemoLibrary.csproj` in Visual Studio and run the HTTPS profile.

The development launch settings currently use:

```text
https://localhost:7266
http://localhost:5127
```

## Usage

1. Configure `KnowledgeLibrary:RootPath`.
2. Start the application.
3. Open the site to view the full current catalogue.
4. Search to shortlist entries.
5. Select entries with normal desktop selection controls.
6. Press Ctrl/Command+C to copy their displayed catalogue information, or choose **Download Selected** to download the represented Markdown files individually.
7. Double-click a catalogue row to view its rendered Markdown.
8. Expand the version count to inspect retained historical versions.
9. Choose **Rescan** after adding or changing source files.

A browser may request permission when several selected files are downloaded separately.

## Version Resolution

Files with the same header `ID` belong to one knowledge series, regardless of filename or path.

The current version is selected by:

1. latest internal `UPDATED` date;
2. latest filesystem modified time in UTC;
3. latest filesystem creation time in UTC.

If multiple distinct files remain tied, the series is marked ambiguous instead of selecting one arbitrarily.

Each physical version also receives a SHA-256 key. Before download, ZMemoLibrary verifies that the file still matches the scanned key. If it changed, rescan before downloading it.

## Read-Only Guarantee

ZMemoLibrary reads source files and returns downloaded copies. It does not:

- edit knowledge files;
- write metadata into them;
- rename, move, or delete them;
- generate a database or persisted catalogue in the knowledge folder.

## Privacy Before Publishing

Before pushing to a public GitHub repository:

- Do not commit private knowledge files.
- Review `appsettings.json` for personal or machine-specific paths.
- Do not commit certificates, keys, secrets, publish profiles, logs, or build output.
- Check the staged files before the first commit:

```powershell
git status
git diff --cached
```

## Current Scope

ZMemoLibrary intentionally does not include:

- knowledge editing or upload;
- LLM calls;
- RAG or vector search;
- automatic filesystem watching;
- multiple knowledge roots;
- authentication or user accounts;
- deployment configuration.