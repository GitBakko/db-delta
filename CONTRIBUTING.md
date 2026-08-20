# Contributing to DbDelta

## Workflow

DbDelta follows the **Superpowers** workflow:

1. **Brainstorm** — discuss the feature, lock scope.
2. **Spec** — produce a design document in `docs/superpowers/specs/`.
3. **Plan** — break the spec into bite-sized tasks in `docs/superpowers/plans/`.
4. **Execute** — implement task-by-task with TDD discipline, one commit per step.

## Test-Driven Development

All code lands via red → green → refactor:

```bash
dotnet test --filter "FullyQualifiedName~MyNewTest"   # red
# implement
dotnet test --filter "FullyQualifiedName~MyNewTest"   # green
# refactor
dotnet test                                            # full suite still green
```

## Build & Verify

Before opening a PR:

```bash
dotnet format --verify-no-changes
dotnet build
dotnet test
```

CI re-runs the same set on `windows-latest` and additionally runs the integration suite with Testcontainers MS SQL.

## Architecture Rules

- `DbDelta.Core` has zero I/O dependencies. Enforced by `DbDelta.Architecture.Tests/LayeringTests.cs`.
- Providers implement ports from `DbDelta.Core.Abstractions`.
- Latest stable NuGet versions only, pinned centrally in `Directory.Packages.props`.
  `NuGetAudit` plus `TreatWarningsAsErrors` means a new advisory turns into
  `NU1903` and breaks the restore with nobody having committed anything — the
  fix is a `PackageVersion` pin, checked against a real container.

## Commit Style

Conventional Commits: `feat:`, `fix:`, `docs:`, `chore:`, `build:`, `test:`, `refactor:`.

### The task list

`docs/BACKLOG.md` is the only one. An item is closed **in the same commit as
the code that closes it**, and never on a recollection — a `file:line` or a
commit hash. Handoffs in `docs/review/` and plans in `docs/superpowers/plans/`
are history, not a status board.

### UI rules

`src/DbDelta.App.Avalonia/CLAUDE.md` holds the non-negotiable ones (control
heights, the accent bands, "Carica" and never "Apri"). They are asserted by
headless tests under `tests/DbDelta.App.HeadlessTests/`.
