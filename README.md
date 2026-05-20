# DbDelta

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

Open-source schema comparison and deployment tool for Microsoft SQL Server. An OSS alternative to Redgate SQL Compare.

> **Status:** Alpha — v1 scope: Live DB ↔ Live DB only, 13 object kinds, Windows-only, SQL Server 2016+ and Azure SQL DB.

## What it does

DbDelta compares the schema (DDL) of two SQL Server databases, computes the differences, and generates a dependency-ordered T-SQL migration script that transforms the target into a structural copy of the source. Available as a Windows GUI (Blazor Hybrid + WebView2) and a cross-platform-capable CLI.

## Quickstart

```bash
git clone https://github.com/GitBakko/db-delta.git
cd db-delta
dotnet restore
dotnet build
dotnet test
```

Run the CLI against two local databases:

```bash
src/DbDelta.Cli/bin/Debug/net10.0/dbdelta.exe compare \
  --source "Server=.;Database=DevDB;Trusted_Connection=True;Encrypt=False" \
  --target "Server=.;Database=ProdDB;Trusted_Connection=True;Encrypt=False" \
  --format json
```

## Documentation

- [Design Spec](docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md)
- [Architecture](docs/01_architecture.md)
- [Data Models](docs/02_data_models.md)
- [Core Modules](docs/03_core_modules.md)
- [CLI / API](docs/04_api_endpoints.md)

## Contributing

We use the Superpowers workflow: brainstorm → spec → plan → execute. Implementation plans live in `docs/superpowers/plans/`. See [CONTRIBUTING.md](CONTRIBUTING.md) (coming soon).

## License

Apache License 2.0 — see [LICENSE](LICENSE).
