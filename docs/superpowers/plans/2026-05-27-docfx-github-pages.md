# DocFX API site + GitHub Pages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish a DocFX documentation site (auto-generated API reference + a minimal conceptual guide) to GitHub Pages automatically on every push to `main`, satisfying spec invariant #5.

**Architecture:** DocFX is pinned as a local dotnet tool (`.config/dotnet-tools.json`). All site scaffolding lives in a dedicated top-level `docfx/` folder, isolated from the existing `docs/` design tree. A separate `.github/workflows/docs.yml` builds the site (`dotnet docfx`) and deploys via the official GitHub Pages actions. `ci.yml` is untouched.

**Tech Stack:** DocFX 2.x (≥ 2.78) as a dotnet local tool; .NET 10 SDK (pinned in `global.json`); GitHub Actions Pages deploy (`configure-pages` / `upload-pages-artifact` / `deploy-pages`).

---

## Background facts (verified in codebase)

- XML docs already emitted: `Directory.Build.props` has `<GenerateDocumentationFile>true</GenerateDocumentationFile>`.
- Library projects (API ref targets): `src/DbDelta.Core`, `src/DbDelta.Shared`, `src/DbDelta.Persistence`, `src/DbDelta.Providers.LiveDb`. Executables to EXCLUDE: `src/DbDelta.Cli`, `src/DbDelta.App.Avalonia`.
- `global.json` pins SDK `10.0.100`, `rollForward: latestFeature`.
- CLI verbs + options (from `src/DbDelta.Cli/Commands/*.cs`):
  - `compare` — `--source` (req), `--target` (req), `--format` (text|json).
  - `script` — `--source` (req), `--target` (req), `--out` (path or `-` for stdout), `--include-permissions`.
  - `apply` — `--target` (req), `--script` (req, path), `--dry-run`.
  - `report` — `--source` (req), `--target` (req), `--html` (path), `--json` (path).
- `ComparisonOptions` flags (from `src/DbDelta.Core/Options/ComparisonOptions.cs`): `None, IgnoreWhitespace, IgnoreComments, IgnoreCollations, IgnoreFillFactor, IgnoreConstraintNames, IgnorePermissions, IgnoreUserSettings, CaseSensitiveObjectDefinition, IgnoreIndexes, IgnoreKeys, IgnoreStatistics, IgnoreTriggers, IgnoreWithElementOrder, IgnoreFileGroups, IgnoreIdentitySeed, IgnoreUsersPermissionsAndRoleMemberships, NoTransactions, ForceColumnOrder, ThrowOnFileParseFailed, DoNotOutputCommentHeader`. `Default = IgnoreWhitespace | IgnoreComments | IgnoreFillFactor | IgnorePermissions | IgnoreStatistics`.
- All DocFX commands run from the repo root: `D:\Develop\AI\_ClaudeCode\SQL Compare`.
- This repo's CI gates hard on `dotnet format --verify-no-changes`. `docfx/**` is C#-free (json/md/yml), but run `dotnet format DbDelta.sln --verify-no-changes` is unaffected by it; do NOT add docfx files to the solution.

---

## Task 1: DocFX as a pinned local tool

**Files:**
- Create: `.config/dotnet-tools.json`

- [ ] **Step 1: Create the tool manifest**

Run: `dotnet new tool-manifest`
Expected: creates `.config/dotnet-tools.json`.

- [ ] **Step 2: Install DocFX into the manifest**

Run: `dotnet tool install docfx`
Expected: installs latest stable DocFX (≥ 2.78) and records the pinned version in `.config/dotnet-tools.json`.

- [ ] **Step 3: Verify the tool restores + runs**

Run: `dotnet tool restore` then `dotnet docfx --version`
Expected: prints a `2.x` version with no error.

- [ ] **Step 4: Commit**

```bash
git add .config/dotnet-tools.json
git commit -m "build(docs): pin DocFX as a dotnet local tool

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 2: DocFX project skeleton + config + API metadata

**Files:**
- Create: `docfx/docfx.json`
- Create: `docfx/index.md`
- Create: `docfx/toc.yml`
- Create: `docfx/api/index.md`
- Modify: `.gitignore`

- [ ] **Step 1: Add git-ignores for generated output**

Append to `.gitignore` (repo root):
```
# DocFX generated output
docfx/api/*.yml
docfx/api/.manifest
docfx/_site/
```
(Keep `docfx/api/index.md` tracked — only the generated `*.yml` + `.manifest` are ignored.)

- [ ] **Step 2: Write `docfx/docfx.json`**

```json
{
  "metadata": [
    {
      "src": [
        {
          "src": "../src",
          "files": [
            "DbDelta.Core/DbDelta.Core.csproj",
            "DbDelta.Shared/DbDelta.Shared.csproj",
            "DbDelta.Persistence/DbDelta.Persistence.csproj",
            "DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj"
          ]
        }
      ],
      "dest": "api",
      "outputFormat": "mref",
      "disableGitFeatures": false
    }
  ],
  "build": {
    "content": [
      { "files": [ "api/**.yml", "api/index.md" ] },
      { "files": [ "index.md", "toc.yml", "articles/**.md", "articles/**/toc.yml" ] }
    ],
    "output": "_site",
    "template": [ "default", "modern" ],
    "globalMetadata": {
      "_appName": "DbDelta",
      "_appTitle": "DbDelta — open-source SQL Server schema compare",
      "_enableSearch": true,
      "_disableContribution": true
    }
  }
}
```

- [ ] **Step 3: Write `docfx/toc.yml` (top nav)**

```yaml
- name: Articles
  href: articles/
- name: API
  href: api/
```

- [ ] **Step 4: Write `docfx/index.md` (landing)**

```markdown
---
_layout: landing
---

# DbDelta

DbDelta is an open-source clone of Redgate SQL Compare for **SQL Server 2016+
and Azure SQL Database**, built on .NET 10. It compares two live databases,
computes a structural diff across 13 object kinds, and generates a
**dependency-ordered** T-SQL deployment script that it can apply in a
transactional batch.

- **[Getting started](articles/getting-started.md)** — build it and run your first compare.
- **[CLI reference](articles/cli.md)** — the `compare`, `script`, `apply`, and `report` verbs.
- **[Comparison options](articles/comparison-options.md)** — the toggles that shape a diff.
- **[API reference](api/index.md)** — the `Core`, `Shared`, `Persistence`, and `Providers.LiveDb` libraries.

Licensed under Apache 2.0.
```

- [ ] **Step 5: Write `docfx/api/index.md` (API landing)**

```markdown
# API reference

Auto-generated from the XML documentation of DbDelta's library projects:

- **DbDelta.Core** — the pure comparison engine, object model, diff,
  script generator, dependency resolver, and ports (`ISchemaSource`, …). No I/O.
- **DbDelta.Shared** — shared primitives and report contracts.
- **DbDelta.Persistence** — SQL execution, connection testing, project/credential stores.
- **DbDelta.Providers.LiveDb** — the live SQL Server `ISchemaSource` implementation.

Use the table of contents on the left to browse namespaces and types.
```

- [ ] **Step 6: Generate API metadata — verify it works against .NET 10**

Run: `dotnet restore` then `dotnet docfx metadata docfx/docfx.json`
Expected: populates `docfx/api/*.yml` (one `.yml` per type plus `toc.yml`). Confirm files exist, e.g. a YAML for `DbDelta.Core.Diff.ComparisonEngine`.

Run: `ls docfx/api` — expect numerous `*.yml` including `DbDelta.Core.*.yml` and a `toc.yml`.

**Fallback if metadata generation fails on .NET 10** (e.g. MSBuild/Roslyn errors): change each `metadata.src.files` entry from the `.csproj` to the built assembly + xml, i.e. build first (`dotnet build -c Release`) then point `src` at `../src` with files like `DbDelta.Core/bin/Release/net10.0/DbDelta.Core.dll`. Document whichever path is used in the commit message.

- [ ] **Step 7: Commit (config + tracked md only; generated yml are git-ignored)**

```bash
git add docfx/docfx.json docfx/index.md docfx/toc.yml docfx/api/index.md .gitignore
git commit -m "docs(site): DocFX config + landing + API metadata for the four libraries

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 3: Conceptual articles (the minimal guide)

**Files:**
- Create: `docfx/articles/toc.yml`
- Create: `docfx/articles/getting-started.md`
- Create: `docfx/articles/cli.md`
- Create: `docfx/articles/comparison-options.md`

- [ ] **Step 1: Write `docfx/articles/toc.yml`**

```yaml
- name: Getting Started
  href: getting-started.md
- name: CLI Reference
  href: cli.md
- name: Comparison Options
  href: comparison-options.md
```

- [ ] **Step 2: Write `docfx/articles/getting-started.md`**

```markdown
# Getting started

## Prerequisites

- The **.NET 10 SDK** (`10.0.100` or later — see `global.json`).
- A reachable **SQL Server 2016+** or **Azure SQL Database** instance (DbDelta
  compares two *live* databases).

## Build from source

```bash
git clone https://github.com/GitBakko/db-delta.git
cd db-delta
dotnet build -c Release
```

The CLI is produced at `src/DbDelta.Cli/bin/Release/net10.0/dbdelta.dll`:

```bash
dotnet src/DbDelta.Cli/bin/Release/net10.0/dbdelta.dll --help
```

## Your first comparison

Compare two databases and print the differences as text:

```bash
dotnet .../dbdelta.dll compare \
  --source "Server=host;Database=Dev;User Id=sa;Password=…;TrustServerCertificate=True" \
  --target "Server=host;Database=Prod;User Id=sa;Password=…;TrustServerCertificate=True"
```

Generate a deployment script that makes the target match the source:

```bash
dotnet .../dbdelta.dll script --source "…Dev…" --target "…Prod…" --out deploy.sql
```

Review `deploy.sql`, then apply it transactionally:

```bash
dotnet .../dbdelta.dll apply --target "…Prod…" --script deploy.sql
```

## The desktop app

DbDelta also ships an Avalonia desktop app (`src/DbDelta.App.Avalonia`):

```bash
dotnet run --project src/DbDelta.App.Avalonia -c Release
```
```

- [ ] **Step 3: Write `docfx/articles/cli.md`**

```markdown
# CLI reference

DbDelta's CLI exposes four verbs. Every verb takes SQL Server **connection
strings** for `--source` / `--target`. The diff direction is always
*source → target*: the source is the desired state, the target is what gets
modified.

## `compare`

Computes the diff and prints it.

| Option | Required | Description |
|--------|----------|-------------|
| `--source` | yes | Source SQL Server connection string. |
| `--target` | yes | Target SQL Server connection string. |
| `--format` | no | Output format: `text` (default) or `json`. |

```bash
dbdelta compare --source "…" --target "…" --format json
```

## `script`

Generates a dependency-ordered T-SQL deployment script.

| Option | Required | Description |
|--------|----------|-------------|
| `--source` | yes | Source connection string. |
| `--target` | yes | Target connection string. |
| `--out` | no | Output file path, or `-` for stdout. |
| `--include-permissions` | no | Emit `GRANT`/`REVOKE` statements (off by default — Redgate-parity). |

```bash
dbdelta script --source "…" --target "…" --out deploy.sql
```

## `apply`

Executes a pre-generated script against the target, GO-split inside a single
transaction.

| Option | Required | Description |
|--------|----------|-------------|
| `--target` | yes | Target connection string. |
| `--script` | yes | Path to the T-SQL script to apply. |
| `--dry-run` | no | Parse and count batches without executing. |

```bash
dbdelta apply --target "…" --script deploy.sql --dry-run
```

## `report`

Produces a self-contained diff report.

| Option | Required | Description |
|--------|----------|-------------|
| `--source` | yes | Source connection string. |
| `--target` | yes | Target connection string. |
| `--html` | no | Output path for the self-contained HTML report. |
| `--json` | no | Output path for the JSON report. |

```bash
dbdelta report --source "…" --target "…" --html diff.html
```
```

- [ ] **Step 4: Write `docfx/articles/comparison-options.md`**

```markdown
# Comparison options

A comparison is shaped by the `ComparisonOptions` flags enum
(`DbDelta.Core.Options.ComparisonOptions`). The CLI uses the `Default` set;
library consumers can pass any combination. `script --include-permissions`
clears `IgnorePermissions`.

`Default` = `IgnoreWhitespace | IgnoreComments | IgnoreFillFactor |
IgnorePermissions | IgnoreStatistics` — the toggles Redgate ships on.

| Flag | Effect when set |
|------|-----------------|
| `IgnoreWhitespace` | Ignore whitespace differences in module bodies. |
| `IgnoreComments` | Ignore comment-only differences. |
| `IgnoreCollations` | Ignore column/DB collation differences. |
| `IgnoreFillFactor` | Ignore index fill-factor differences. |
| `IgnoreConstraintNames` | Ignore differences in constraint names. |
| `IgnorePermissions` | Skip GRANT/DENY/REVOKE entirely. |
| `IgnoreUserSettings` | Ignore user-level settings. |
| `CaseSensitiveObjectDefinition` | Compare module bodies case-sensitively. |
| `IgnoreIndexes` | Ignore index differences. |
| `IgnoreKeys` | Ignore primary/unique key differences. |
| `IgnoreStatistics` | Ignore statistics objects. |
| `IgnoreTriggers` | Ignore trigger differences. |
| `IgnoreWithElementOrder` | Ignore ordering of `WITH` elements. |
| `IgnoreFileGroups` | Ignore filegroup placement. |
| `IgnoreIdentitySeed` | Ignore identity seed/increment. |
| `IgnoreUsersPermissionsAndRoleMemberships` | Ignore users, permissions, and role memberships together. |
| `NoTransactions` | Emit the script without the transaction envelope. |
| `ForceColumnOrder` | Treat column ordering as significant. |
| `ThrowOnFileParseFailed` | Fail loudly on an unparseable definition. |
| `DoNotOutputCommentHeader` | Suppress the generated comment header. |
```

- [ ] **Step 5: Build verification of the articles**

Run: `dotnet docfx build docfx/docfx.json`
Expected: completes; `docfx/_site/articles/getting-started.html`, `cli.html`, `comparison-options.html` exist.

- [ ] **Step 6: Commit**

```bash
git add docfx/articles/
git commit -m "docs(site): getting-started, CLI, and comparison-options articles

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 4: Clean full-build verification

**Files:** none (verification only)

- [ ] **Step 1: Full DocFX build, treating warnings as errors**

Run: `dotnet docfx docfx/docfx.json --warningsAsErrors`
Expected: build succeeds with exit 0.

**If it fails on missing-XML-doc warnings:** assess the count. If small, fill the missing `<summary>` docs on the offending public members (preferred). If large/pre-existing across the public surface, drop `--warningsAsErrors` for this first cut, capture the warning count in the commit/report, and note "doc-coverage backlog" — do NOT block the site on it. Record the decision.

- [ ] **Step 2: Spot-check the rendered site**

Run: `ls docfx/_site/index.html docfx/_site/api docfx/_site/articles`
Expected: `index.html` exists; `api/` contains type pages (e.g. an HTML for `DbDelta.Core.Diff.ComparisonEngine` and `DbDelta.Core.Abstractions.ISchemaSource`); `articles/` contains the three article pages.

- [ ] **Step 3: (no commit — generated `_site` is git-ignored)**

Confirm `git status --porcelain` shows no tracked changes from the build (only ignored `docfx/_site/` + `docfx/api/*.yml`).

---

## Task 5: GitHub Pages deploy workflow

**Files:**
- Create: `.github/workflows/docs.yml`

- [ ] **Step 1: Write the workflow**

```yaml
name: docs

on:
  push:
    branches: [ main ]
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build-and-deploy:
    name: build DocFX + deploy to Pages
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/Directory.Packages.props') }}
          restore-keys: |
            ${{ runner.os }}-nuget-

      - name: Restore tools
        run: dotnet tool restore

      - name: Build documentation
        run: dotnet docfx docfx/docfx.json

      - name: Configure Pages
        uses: actions/configure-pages@v5

      - name: Upload Pages artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: docfx/_site

      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
```

- [ ] **Step 2: Validate the YAML structurally**

Run: `python -c "print('skip')"` is NOT available without pyyaml — instead confirm indentation by eye against the sibling `.github/workflows/ci.yml` (jobs at 2-space, steps at 6-space). Confirm `on`, `permissions`, `concurrency`, `jobs` are top-level keys.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/docs.yml
git commit -m "ci(docs): build DocFX + deploy to GitHub Pages on push to main

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 6: Enable Pages, push, verify deploy, close out

**Files:**
- Modify: `docs/superpowers/specs/2026-05-27-docfx-github-pages-design.md` (status)
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Enable GitHub Pages (Source: GitHub Actions)**

This is a repo-settings change. Try (requires repo-admin token):
Run: `gh api -X POST repos/GitBakko/db-delta/pages -f build_type=workflow`
Expected: returns the Pages config JSON, or `409` if already enabled (fine).

**If `gh api` returns 403/404 (insufficient scope):** STOP and ask the maintainer to set **Settings → Pages → Build and deployment → Source: GitHub Actions** in the GitHub UI. The workflow cannot deploy until this is done.

- [ ] **Step 2: Push main to trigger the workflow**

Run: `git push origin main`
Expected: pushes the docs commits; the `docs` workflow starts.

- [ ] **Step 3: Watch the workflow to green**

Run: `gh run watch $(gh run list --workflow docs.yml --branch main --limit 1 --json databaseId --jq '.[0].databaseId') --exit-status --interval 20`
Expected: the run completes `success`. Capture the deployed Pages URL from the run's `deployment` step output (`gh run view <id> --json jobs`).

- [ ] **Step 4: Confirm the site is live**

Run: `gh api repos/GitBakko/db-delta/pages --jq .html_url` to get the URL, then `curl -sSI <url> | head -1` → expect `HTTP/2 200`.

- [ ] **Step 5: Update CHANGELOG + spec status**

In `CHANGELOG.md`, under `[Unreleased]`, remove "DocFX site" from the pending list and add a `### Added` bullet: "Published a DocFX documentation site (API reference for the four library projects + getting-started / CLI / comparison-options guides) to GitHub Pages, auto-deployed on push to `main` (`.github/workflows/docs.yml`)."

In `docs/superpowers/specs/2026-05-27-docfx-github-pages-design.md`, change the `**Status:**` line to `implemented 2026-05-27 · live at <Pages URL>`.

- [ ] **Step 6: Commit (do NOT tag)**

```bash
git add CHANGELOG.md docs/superpowers/specs/2026-05-27-docfx-github-pages-design.md
git commit -m "docs: close out #25 DocFX site (CHANGELOG + spec status)

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
git push origin main
```

---

## Self-review notes for the implementer

- **No solution coupling.** Do NOT add `docfx/` files or the tool manifest to `DbDelta.sln`. They are not compiled by the build; `dotnet format --verify-no-changes` (the CI gate) only inspects solution C#.
- **Generated output stays out of git.** Only `docfx.json`, the markdown, the `toc.yml`s, and `docfx/api/index.md` are tracked. `docfx/api/*.yml` and `docfx/_site/` are ignored.
- **Two manual/external dependencies:** (1) GitHub Pages must be set to "GitHub Actions" source (Task 6 Step 1); (2) the DocFX-on-.NET-10 metadata path may need the assembly-based fallback (Task 2 Step 6).
- **Keep `ci.yml` untouched.** The docs pipeline is a separate workflow.
```
