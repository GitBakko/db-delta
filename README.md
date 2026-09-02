# DbDelta

[![Release](https://img.shields.io/github/v/release/GitBakko/db-delta?include_prereleases&label=release)](https://github.com/GitBakko/db-delta/releases)
[![CI](https://github.com/GitBakko/db-delta/actions/workflows/ci.yml/badge.svg)](https://github.com/GitBakko/db-delta/actions/workflows/ci.yml)
[![Docs](https://github.com/GitBakko/db-delta/actions/workflows/docs.yml/badge.svg)](https://gitbakko.github.io/db-delta/)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

Open-source schema comparison and deployment tool for Microsoft SQL Server. An OSS alternative to Redgate SQL Compare.

> **Status:** Stable — [v1.1.0](https://github.com/GitBakko/db-delta/releases/tag/v1.1.0) is the current release; [v1.0.1](https://github.com/GitBakko/db-delta/releases/tag/v1.0.1) was the first final one.
> v1 scope: Live DB ↔ Live DB, 13 object kinds, Windows-first, SQL Server 2016+ and Azure SQL DB.

📖 **Documentation site:** <https://gitbakko.github.io/db-delta/> · [Version history](https://gitbakko.github.io/db-delta/articles/version-history.html)

## Install (Windows)

Grab the MSI from the [latest release](https://github.com/GitBakko/db-delta/releases) — it installs:

- **DbDelta** — the desktop app (Start-Menu shortcut), and
- **`dbdelta`** — the CLI, added to the system `PATH`.

Notes:

- The MSI is **unsigned**, so Windows SmartScreen will warn on first run —
  choose *More info → Run anyway*. Code signing is planned but not yet in
  place; it will land in a later release without changing anything else.
- Every release also carries a `.sha256` file next to the MSI and a signed
  **build-provenance attestation**. To check what you downloaded:
  `Get-FileHash -Algorithm SHA256 DbDelta-<version>-win-x64.msi` against the
  `.sha256`, or `gh attestation verify DbDelta-<version>-win-x64.msi --repo GitBakko/db-delta`.
- **Coming from an earlier version?** Install the current MSI straight over it.
  The one exception is the release candidates: they all carry the numeric
  ProductVersion `1.0.0`, which is why the first final release was `1.0.1` and
  not `1.0.0` — Windows Installer does not upgrade between identical versions,
  and it compares only the first three fields. Upgrading *between* RCs still
  requires uninstalling the previous one first.

## What it does

DbDelta compares the schema (DDL) of two SQL Server databases, computes the differences, and generates a dependency-ordered T-SQL migration script that transforms the target into a structural copy of the source. Deploy scripts are self-contained and verbose, Redgate-style: a full `SET` preamble, one transaction, a `PRINT` per step with an error gate after each batch, and a final succeeded/failed verdict that rolls back on failure — safe to run from `dbdelta apply`, `sqlcmd`, or SSMS as-is.

Script generation is calibrated against Redgate SQL Compare 16 on a live parity fixture ([audits](docs/parity/)) and ships as a desktop GUI built on **Avalonia 11** (cross-platform-ready) plus the `dbdelta` CLI.

## CLI quickstart

After installing the MSI (or `dotnet publish` from source), the CLI offers four verbs — `compare`, `report`, `script`, `apply`:

```bash
# Compare two databases
dbdelta compare \
  --source "Server=.;Database=DevDB;Trusted_Connection=True;Encrypt=False" \
  --target "Server=.;Database=ProdDB;Trusted_Connection=True;Encrypt=False" \
  --format json
```

Exit code reflects outcome — `0` no differences, `1` differences found, `10`/`11`/`20`/`30`/`31`/`40`/`41`/`60`/`99` for the various failure classes (see [spec §4.3](docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md#43-cli-exit-codes)).

```bash
# Self-contained HTML + JSON reports
dbdelta report --source "…" --target "…" --html report.html --json report.json

# Generate the alignment script without touching the target
dbdelta script --source "…" --target "…" --out align.sql

# Execute a generated script against the target (transactional, rolls back on failure)
dbdelta apply --target "…" --script align.sql       # add --dry-run to preview
```

The HTML report is fully self-contained (inline CSS, no external assets), with collapsible sections per Kind colour-coded by difference status.

## The GUI

The Avalonia shell provides connection management, project persistence (`.dbd` XML files), side-by-side DDL diff viewer with diff hints, deployment script preview, and transactional apply with rollback. The version pill in the status bar deep-links to the [version history](https://gitbakko.github.io/db-delta/articles/version-history.html) of the build you are running.

## Build from source

```bash
git clone https://github.com/GitBakko/db-delta.git
cd db-delta
dotnet restore
dotnet build
dotnet test
```

```bash
# Run the GUI from source
dotnet run --project src/DbDelta.App.Avalonia
```

## Architecture

Hexagonal core, ports + adapters:

| Project | Target | Role |
|---------|--------|------|
| `DbDelta.Core` | `net10.0`, pure | Object model, comparison engine, dependency resolver, script emitters |
| `DbDelta.Providers.LiveDb` | `net10.0` | `ISchemaSource` + `IDeploymentExecutor` implementations over `Microsoft.Data.SqlClient` |
| `DbDelta.Persistence` | `net10.0` | `.dbd` XML project file + DPAPI credential store (`Meziantou.Framework.Win32.CredentialManager`) |
| `DbDelta.Shared` | `net10.0` | DTOs + Mapper at the App↔Core boundary + JSON report serializer |
| `DbDelta.Cli` | `net10.0` console | `compare`, `report`, `script`, `apply` verbs |
| `DbDelta.App.Avalonia` | `net10.0` desktop | Avalonia 11 + Fluent theme + CommunityToolkit.Mvvm |

Architecture rules are enforced by `NetArchTest.Rules` tests under `tests/DbDelta.Architecture.Tests/` — Core stays free of I/O dependencies.

## Test suite

```bash
dotnet test
```

Over 900 tests across eleven projects: unit, architecture, headless UI, script-gen golden, FsCheck property, persistence (unit + integration), live-db integration, CLI acceptance, and a nightly SQL Server 2017/2019/2022 compat matrix. Integration tests use `Testcontainers.MsSql` (Docker required); the compat matrix self-skips unless `DBDELTA_COMPAT=1`.

## Documentation

- [Documentation site](https://gitbakko.github.io/db-delta/) — guides + full API reference
- [Version history](https://gitbakko.github.io/db-delta/articles/version-history.html) ([CHANGELOG](CHANGELOG.md) in the repo)
- [Design Spec](docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md) — locked v1 contract
- [Avalonia pivot addendum](docs/superpowers/specs/2026-05-25-avalonia-ui-pivot-addendum.md) — why the GUI moved from Blazor Hybrid to Avalonia
- [Redgate parity audits](docs/parity/)

**Research notes, not DbDelta's own documentation.** `docs/01_architecture.md`,
`docs/02_data_models.md`, `docs/03_core_modules.md` and
`docs/04_api_endpoints.md` describe **Redgate SQL Compare**, reverse-engineered
before this project had any code. They are the reasoning behind the design, and
they name switches, paths and binaries that are Redgate's and have no DbDelta
equivalent — `sqlcompare.exe`, `--abort-on-warnings`. Read them as background;
for what DbDelta actually does, the documentation site above is the only source.

## Contributing

We use the Superpowers workflow: brainstorm → spec → plan → execute. Implementation plans live in `docs/superpowers/plans/` and are **history**: they record how something was built, never what is left to do. The open list is [docs/BACKLOG.md](docs/BACKLOG.md). See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Apache License 2.0 — see [LICENSE](LICENSE).
