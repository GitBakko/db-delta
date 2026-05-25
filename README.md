# DbDelta

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

Open-source schema comparison and deployment tool for Microsoft SQL Server. An OSS alternative to Redgate SQL Compare.

> **Status:** Alpha — v1 scope: Live DB ↔ Live DB only, 13 object kinds, Windows-first, SQL Server 2016+ and Azure SQL DB.

## What it does

DbDelta compares the schema (DDL) of two SQL Server databases, computes the differences, and generates a dependency-ordered T-SQL migration script that transforms the target into a structural copy of the source. Ships as a desktop GUI built on **Avalonia 11** (cross-platform-ready) and a `dotnet`-based CLI.

## Quickstart

```bash
git clone https://github.com/GitBakko/db-delta.git
cd db-delta
dotnet restore
dotnet build
dotnet test
```

### Compare two databases (CLI)

```bash
dotnet src/DbDelta.Cli/bin/Debug/net10.0/dbdelta.dll compare \
  --source "Server=.;Database=DevDB;Trusted_Connection=True;Encrypt=False" \
  --target "Server=.;Database=ProdDB;Trusted_Connection=True;Encrypt=False" \
  --format json
```

Exit code reflects outcome — `0` no differences, `1` differences found, `10`/`11`/`20`/`30`/`31`/`40`/`41`/`60`/`99` for the various failure classes (see [spec §4.3](docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md#43-cli-exit-codes)).

### Produce HTML + JSON reports (CLI)

```bash
dotnet src/DbDelta.Cli/bin/Debug/net10.0/dbdelta.dll report \
  --source "Server=src.example;Database=DevDB;..." \
  --target "Server=tgt.example;Database=ProdDB;..." \
  --html report.html \
  --json report.json
```

At least one of `--html` / `--json` is required; both can be supplied together. The HTML report is fully self-contained (inline CSS, no external assets), with collapsible sections per Kind colour-coded by difference status.

### Launch the GUI

```bash
dotnet run --project src/DbDelta.App.Avalonia
```

The Avalonia shell provides connection management, project persistence (`.dbd` XML files), side-by-side DDL diff viewer, deployment script preview, and transactional apply with rollback.

## Architecture

Hexagonal core, ports + adapters:

| Project | Target | Role |
|---------|--------|------|
| `DbDelta.Core` | `net10.0`, pure | Object model, comparison engine, dependency resolver, script emitters |
| `DbDelta.Providers.LiveDb` | `net10.0` | `ISchemaSource` + `IDeploymentExecutor` implementations over `Microsoft.Data.SqlClient` |
| `DbDelta.Persistence` | `net10.0` | `.dbd` XML project file + DPAPI credential store (`Meziantou.Framework.Win32.CredentialManager`) |
| `DbDelta.Shared` | `net10.0` | DTOs + Mapper at the App↔Core boundary + JSON report serializer |
| `DbDelta.Cli` | `net10.0` console | `compare`, `report` verbs (more under construction) |
| `DbDelta.App.Avalonia` | `net10.0` desktop | Avalonia 11 + Fluent theme + CommunityToolkit.Mvvm |

Architecture rules are enforced by `NetArchTest.Rules` tests under `tests/DbDelta.Architecture.Tests/` — Core stays free of I/O dependencies.

## Test suite

```bash
dotnet test
```

Runs 299 tests across nine projects: unit, architecture, headless UI, script-gen golden, persistence (unit + integration), live-db integration, and CLI acceptance. Integration tests use `Testcontainers.MsSql` (Docker required).

## Documentation

- [Design Spec](docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md) — locked v1 contract
- [Avalonia pivot addendum](docs/superpowers/specs/2026-05-25-avalonia-ui-pivot-addendum.md) — why the GUI moved from Blazor Hybrid to Avalonia
- [Architecture](docs/01_architecture.md)
- [Data Models](docs/02_data_models.md)
- [Core Modules](docs/03_core_modules.md)
- [CLI / API](docs/04_api_endpoints.md)
- [CHANGELOG](CHANGELOG.md)

## Contributing

We use the Superpowers workflow: brainstorm → spec → plan → execute. Implementation plans live in `docs/superpowers/plans/`. See [CONTRIBUTING.md](CONTRIBUTING.md) (coming soon).

## License

Apache License 2.0 — see [LICENSE](LICENSE).
