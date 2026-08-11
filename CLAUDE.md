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
