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
  --source "Server=host;Database=Dev;User Id=sa;Password=...;TrustServerCertificate=True" \
  --target "Server=host;Database=Prod;User Id=sa;Password=...;TrustServerCertificate=True"
```

Generate a deployment script that makes the target match the source:

```bash
dotnet .../dbdelta.dll script --source "...Dev..." --target "...Prod..." --out deploy.sql
```

Review `deploy.sql`, then apply it transactionally:

```bash
dotnet .../dbdelta.dll apply --target "...Prod..." --script deploy.sql
```

## The desktop app

DbDelta also ships an Avalonia desktop app (`src/DbDelta.App.Avalonia`):

```bash
dotnet run --project src/DbDelta.App.Avalonia -c Release
```
