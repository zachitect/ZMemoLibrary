BEGIN_KNOWLEDGE_HEADER
PROJECT: ZMemoLibrary
ID: aeae608c-a601-44ab-9046-8edc6b3d0994
TITLE: LLM Instruction - Convert Raw File To Knowledge File
CREATED: 2026-06-21
UPDATED: 2026-08-06
SUMMARY: Use this instruction when converting raw notes, LLM handovers, session summaries, or loose technical records into consistent knowledge-library files. It uses a simple plain-text header instead of YAML so future LLMs can follow the format reliably.
KEYWORDS: LLM knowledge library; raw conversion; plain text header; metadata; GUID; connections; content date
CONNECTIONS: none | No known related knowledge file.
END_KNOWLEDGE_HEADER

# LLM Instruction - Convert Raw File To Knowledge File

You are converting raw knowledge content into one new knowledge-library Markdown file.

Do not modify the original file. Generate a new file.

## Project Ownership

Every knowledge file belongs to exactly one human-nominated project.

The project may be supplied with the task as:

```text
PROJECT: <project name>
```

Rules:

- Copy a supplied project name exactly.
- Do not infer the project from content, filenames, paths, repositories, namespaces, titles, keywords, connected documents, or mentioned products.
- Do not correct, normalise, rename, abbreviate, expand, merge, or subdivide a supplied project name.
- Do not assign multiple projects.
- If no project is supplied, use exactly `PROJECT: Unassigned`.
- `Unassigned` means no human has nominated the project.
- Project assignment is organisational metadata and does not determine connections.

## Filename Rules

Use this exact filename pattern:

```text
YYYY-MM-DD_short-topic-keywords.md
```

Rules:

- The date must be the knowledge content date.
- Use numeric `YYYY-MM-DD`.
- Use an underscore after the date.
- Use lowercase topic keywords after the underscore.
- Use hyphens between topic words.
- Do not use spaces.
- Avoid vague names like `notes`, `handover`, `summary`, or `final` unless combined with specific topic keywords.


## Fixed Header Format

Use this exact plain-text header at the very top of every knowledge file:

```text
BEGIN_KNOWLEDGE_HEADER
PROJECT: <human-nominated project name or Unassigned>
ID: <guid>
TITLE: <short human-readable title>
CREATED: <YYYY-MM-DD>
UPDATED: <YYYY-MM-DD>
SUMMARY: <1-3 sentence summary describing when this file is useful in future LLM sessions>
KEYWORDS: <keyword 1>; <keyword 2>; <keyword 3>
CONNECTIONS: <guid> | <reason>; <guid> | <reason>
END_KNOWLEDGE_HEADER
```

This is not YAML. Do not use YAML indentation. Do not use frontmatter `---` lines. Do not use markdown tables.

## Header Rules

- The first line of the file must be exactly `BEGIN_KNOWLEDGE_HEADER`.
- The header must end with exactly `END_KNOWLEDGE_HEADER`.
- Use exactly these fields in exactly this order:
  1. `PROJECT:`
  2. `ID:`
  3. `TITLE:`
  4. `CREATED:`
  5. `UPDATED:`
  6. `SUMMARY:`
  7. `KEYWORDS:`
  8. `CONNECTIONS:`
- `PROJECT` must contain exactly one project name.
- Use the supplied project exactly, or `Unassigned` when none is supplied.
- Do not infer `PROJECT`.
- Do not add extra header fields.
- Do not remove header fields.
- Keep each header field on one physical line.
- Body content starts after `END_KNOWLEDGE_HEADER` and one blank line.

## Connections Format

Connections are always present.

If no related file ID is known, use exactly:

```text
CONNECTIONS: none | No known related knowledge file.
```

If one or more related files are known, use one `CONNECTIONS:` line with semicolon-separated items:

```text
CONNECTIONS: aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa | Same website infrastructure; bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb | Direct follow-up troubleshooting session
```

Connection rules:

- Use only GUIDs that are visible in the provided files.
- Do not invent connection IDs.
- Do not connect a file to itself.
- Do not mix `none` with real GUIDs.
- Prefer fewer high-quality connections over weak connections.


## Date Rules

Use the knowledge content date, not the processing date.

Priority:

1. Use an explicit date inside the content if it clearly represents the session, setup, fix, decision, or note date.
2. Otherwise use the date in the original filename if it clearly represents the content date.
3. Otherwise use provided file creation or modified date if it appears to represent the content date.
4. If no reliable content date is available, ask the user for the date before generating the final file.

Do not change `CREATED`, `UPDATED`, or filename date just because:

- the file was converted from raw format;
- the header was added or corrected;
- the file was renamed;
- connections were added or corrected.

Only change `UPDATED` and the filename date when the actual knowledge body receives new durable content.


## Connection Discovery Rules

Always look for connections if more than one knowledge file is provided.

This applies even when the main task is raw conversion or update.

Create a connection only when the relationship is clear and useful for future retrieval.

Good connection reasons include:

- direct dependency or continuation within the same project;
- same VPS, server, repo, codebase, website, domain, workflow, or environment;
- direct continuation of the same task;
- one file depends on, supersedes, or explains another;
- same troubleshooting chain;
- shared concrete paths, ports, commands, package names, APIs, or services.

Bad connection reasons include:

- both files are about AI in general;
- both files are about code in general;
- both files are about servers in general;
- both files are old;
- both files are in the same folder but have no useful content relationship.
- both files belong to the same project but have no direct useful relationship.

If only one file is provided, use `CONNECTIONS: none | No known related knowledge file.`.


## Task Rules

- Create a new GUID for `ID`.
- `TITLE` must be short, specific, and human-readable.
- `SUMMARY` must explain when this file is useful in future LLM sessions.
- `KEYWORDS` must be semicolon-separated concrete search terms from the content.
- Preserve durable facts, decisions, constraints, commands, paths, configuration, known-good states, failure modes, and next steps.
- Remove obvious chat noise only when it does not change meaning.
- Do not reduce the body to only a short summary.
- Do not invent facts.
- If multiple files are provided, process each file separately and find connections between them using visible GUIDs.

## Final Self-Check

Before returning, verify the generated file itself:

- Line 1 is exactly `BEGIN_KNOWLEDGE_HEADER`.
- `PROJECT` is the first header field.
- A supplied project was copied exactly.
- If no project was supplied, the value is exactly `Unassigned`.
- The project was not inferred from content.
- There is exactly one `PROJECT:` line.
- The header fields are present in the required order.
- Every header field is on one line.
- There is exactly one `CONNECTIONS:` line.
- `CONNECTIONS:` uses either `none | No known related knowledge file.` or real GUID/reason pairs.
- `none` is not mixed with real GUIDs.
- The header ends with `END_KNOWLEDGE_HEADER`.
- The body starts after the header.
- The filename uses `YYYY-MM-DD_` numeric date format.
- The filename date is the knowledge content date, not the processing date.