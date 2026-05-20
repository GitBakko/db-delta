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
- Latest stable NuGet versions only — Renovate-bot opens PRs weekly.

## Commit Style

Conventional Commits: `feat:`, `fix:`, `docs:`, `chore:`, `build:`, `test:`, `refactor:`.
