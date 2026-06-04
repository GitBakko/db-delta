# Consultable version history — design

**Date:** 2026-06-04
**Status:** approved (brainstorming with owner, 2026-06-04)

## Goal

Make the project's version history consultable in two linked places:

1. A **Version history** page on the existing DocFX site
   (<https://gitbakko.github.io/db-delta/>), rendered 1:1 from the curated
   `CHANGELOG.md` (single source of truth).
2. A **clickable version label** in the desktop app's main-window status bar
   (bottom row, right side) that deep-links to that page, anchored to the
   running version.

## Decisions (owner-confirmed)

| Topic | Decision |
|---|---|
| Where consultable | DocFX site + version label in main window |
| Label placement | Status bar (24 px bottom row), right side |
| Page shape | `CHANGELOG.md` rendered 1:1 (no enriched timeline, no git-derived page) |
| Click target | Deep-link to the **anchor of the running version** |
| Pipeline | Approach A — transform script at docs-build time injects stable anchors; generated article is gitignored |

## Components

### 1. Docs pipeline

- **`scripts/docs/build-version-history.ps1`** (pwsh; runs on the ubuntu CI
  runner and locally on Windows):
  - reads `CHANGELOG.md` at the repo root;
  - for every version heading `## [<version>]…` injects a stable HTML anchor
    `<a id="v<version>"></a>` on the line above the heading. The id derives
    **only** from the version token (e.g. `v1.0.0-rc1`), never from the rest
    of the heading text, so later heading-format changes cannot break links;
  - writes `docfx/articles/version-history.md` verbatim otherwise (H1 stays
    `# Changelog`, `[Unreleased]` section included).
- **`docfx/articles/toc.yml`**: new entry `Version history` →
  `version-history.md`. The nav label is "Version history"; the page H1
  remains "Changelog".
- **`.gitignore`**: `docfx/articles/version-history.md` (generated artifact).
- **`.github/workflows/docs.yml`**: run the script, then a sanity assertion
  (anchor count ≥ 1), **before** `docfx build`. A local docfx build needs the
  same script run first (documented in the script header); without it the
  docfx build fails fast on the missing toc href (`--warningsAsErrors`),
  which is the intended behaviour rather than silently publishing without
  the page.

### 2. Version stamping (release pipeline)

- **`.github/workflows/release.yml`**: pass
  `-p:Version=${{ steps.ver.outputs.version }}` to **both** publish steps
  (desktop app and CLI). Today neither publish stamps a version, so installed
  binaries report the SDK default; after this change `dbdelta --version` and
  the app label both report the tag-driven semver (including prerelease
  labels such as `-rc1`). Versioning stays tag-driven — no hardcoded release
  version in any csproj.
- **`src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj`**: dev fallback
  `<Version Condition="'$(Version)' == ''">0.0.0-dev</Version>` so local
  builds honestly show `v0.0.0-dev` (CI's `-p:Version` overrides it).

### 3. App: version label + link

- **`AppVersionInfo`** (new static helper in `DbDelta.App`):
  - reads `AssemblyInformationalVersionAttribute` from the entry assembly;
  - strips the `+<metadata>` suffix the SDK appends (e.g. `+abc123`);
  - falls back to `dev` when the attribute is missing (in that case `Display`
    shows plain `dev`, no `v` prefix);
  - exposes `Display` (`v1.0.0-rc1`) and `HistoryUrl`
    (`https://gitbakko.github.io/db-delta/articles/version-history.html#v1.0.0-rc1`).
- **`MainWindowViewModel`**: `AppVersion` property + `OpenVersionHistoryCommand`
  that launches the default browser via
  `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`.
- **`MainWindow.axaml` status bar (grid row 3, 24 px)**: row content becomes a
  two-column layout — left side unchanged (status text + redacted connection
  strings), right side a **mini pill button** showing `v1.0.0-rc1`:
  - visible border (design-system rule: no naked buttons), ~18 px tall inside
    the 24 px bar, font size 11;
  - hand cursor + tooltip "Apri la version history nel browser".

## Error handling

- Script: missing `CHANGELOG.md` → exit 1; zero version headings matched →
  exit 1 (malformed changelog must break the docs build, not publish an empty
  page).
- App: browser launch failure → exception swallowed, message surfaced in
  `AppState.StatusText`; missing version attribute → `dev` fallback.
- Stale anchor (version absent from the page — theoretical, the changelog is
  append-only): browser lands at the top of the page; accepted degradation.

## Testing

- `DbDelta.App.HeadlessTests`: unit tests for `AppVersionInfo`
  (`+sha` stripping, URL composition, dev fallback) and for the view-model
  surface (`AppVersion`, command existence/executability).
- `docs.yml`: post-script assertion (anchor count ≥ 1) before `docfx build`;
  `--warningsAsErrors` already guards toc/href integrity.
- `dotnet format` gate on every touched file (CI hard-gates on it).

## Out of scope (YAGNI)

- Machine-readable JSON history, offline in-app changelog viewer,
  auto-update checks, enriched timeline rendering.
