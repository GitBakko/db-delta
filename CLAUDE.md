# DbDelta — Claude Code Configuration

## Rules

- Do what has been asked; nothing more, nothing less
- NEVER create files unless absolutely necessary — prefer editing existing files
- NEVER create documentation files unless explicitly requested
- NEVER save working files or tests to root — use `/src`, `/tests`, `/docs`, `/scripts`, `/bench`, `/docfx`
- ALWAYS read a file before editing it
- NEVER commit secrets, credentials, or .env files
- Keep NEW files under 500 lines. Six existing ones are already over —
  measured 2026-09-01, `wc -l`, not remembered: ScriptGenerator 1527,
  MainWindowViewModel 1208, TableScriptEmitter 1004, LiveDbObjectBodyResolver
  845, ComparisonEngine 741, ProjectEndpointPanelViewModel 689. Do not grow
  them without opening an item in `docs/BACKLOG.md`. These numbers age like
  any other: **four of the six drifted in one day** the last time they were
  checked — re-measure before quoting them
- Validate input at system boundaries

## Single source of truth — FUNDAMENTAL

`docs/BACKLOG.md`, the session memory and this file **tell the same story**.
Keeping them aligned is not housekeeping done later; it is part of the change.

- **`docs/BACKLOG.md` is the ONLY task list.** Never start a second one.
- **Close a backlog item in the SAME commit as the code that closes it.**
- **Never depenna an item without evidence** — a `file:line` or a commit hash,
  never a recollection. On 2026-08-18 a full re-verification found 14 of 58
  entries describing a state the code no longer had.
- **A `file:line` rots the moment the file is edited.** If a commit touches a
  file the backlog cites elsewhere, fix those lines in the SAME commit: a
  reference that points at a stray brace is worse than no reference.
- **Commit hashes older than 2026-08-18 do not resolve.** History was rewritten
  that day to remove a credential. Search by message instead:
  `git log --oneline --all --grep="…"`.
- **Handoffs in `docs/review/` AND plans in `docs/superpowers/plans/` are
  HISTORY, not current state** — why something was done that way, which traps
  were paid. Never read them as a status board. The ~625 unticked `- [ ]` boxes
  under `plans/` belong to milestones shipped in v1.0.2: they record how
  something was built, never what is left. **If it is not in
  `docs/BACKLOG.md`, it is not open.**
- **Session memory points at the backlog and does not copy it.** It holds only
  what the repo cannot say: owner decisions, environment traps, working cadence.
- **Any status block ages in days.** A line naming a hash, a version or a test
  count is re-checked against `git status -sb`, `git log -1`, `CHANGELOG.md`
  before being believed — including the one in `docs/BACKLOG.md`.

## UI / UX Invariable Rules (DbDelta Avalonia app)

Non-negotiable — see `src/DbDelta.App.Avalonia/CLAUDE.md`, loaded automatically
whenever you touch files under that directory.

## Build & Test

This is a **.NET 10** solution (`DbDelta.sln`), NOT a Node project.

- ALWAYS run tests after code changes
- ALWAYS verify build succeeds before committing
- **CI gates hard on `dotnet format --verify-no-changes`** (windows-build job).
  Run `dotnet format` on every file you touch before committing, or CI goes red.
- DB-backed tests (LiveDb / Persistence integration / Cli acceptance / compat)
  need Docker (Testcontainers). The nightly compat matrix self-skips unless
  `DBDELTA_COMPAT=1`.

```bash
dotnet build DbDelta.sln -c Debug
dotnet test DbDelta.sln                       # or a specific tests/<project>
dotnet format DbDelta.sln --verify-no-changes # CI gate — must exit 0
```

Backlog / task list for new sessions: `docs/BACKLOG.md`.
