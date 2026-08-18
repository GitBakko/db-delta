# DbDelta — Claude Code Configuration

## Rules

- Do what has been asked; nothing more, nothing less
- NEVER create files unless absolutely necessary — prefer editing existing files
- NEVER create documentation files unless explicitly requested
- NEVER save working files or tests to root — use `/src`, `/tests`, `/docs`, `/config`, `/scripts`
- ALWAYS read a file before editing it
- NEVER commit secrets, credentials, or .env files
- Keep files under 500 lines
- Validate input at system boundaries

## Single source of truth — FUNDAMENTAL

`docs/BACKLOG.md`, the session memory and this file **tell the same story**.
Keeping them aligned is not housekeeping done later; it is part of the change.

- **`docs/BACKLOG.md` is the ONLY task list.** Never start a second one.
- **Close a backlog item in the SAME commit as the code that closes it.**
- **Never depenna an item without evidence** — a `file:line` or a commit hash,
  never a recollection. On 2026-08-18 a full re-verification found 14 of 58
  entries describing a state the code no longer had.
- **Handoffs in `docs/review/` are HISTORY, not current state** — why something
  was done that way, which traps were paid. Never read them as a status board.
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
