# Getting started

## Prerequisites

A reachable **SQL Server 2016+** or **Azure SQL Database** instance: DbDelta
compares two *live* databases. Nothing else — the MSI carries its own .NET
runtime.

## Install (Windows)

Download the MSI from the
[latest release](https://github.com/GitBakko/db-delta/releases). It installs the
desktop app (Start-Menu shortcut) and puts the `dbdelta` CLI on the system
`PATH`, so:

```bash
dbdelta --help
```

The MSI is **unsigned** — SmartScreen warns on first run, choose
*More info → Run anyway*. Each release also ships a `.sha256` next to the MSI
and a signed build-provenance attestation:

```powershell
Get-FileHash -Algorithm SHA256 DbDelta-<version>-win-x64.msi
gh attestation verify DbDelta-<version>-win-x64.msi --repo GitBakko/db-delta
```

## Or build from source

Needs the **.NET 10 SDK** (`10.0.100` or later — see `global.json`).

```bash
git clone https://github.com/GitBakko/db-delta.git
cd db-delta
dotnet build -c Release
dotnet src/DbDelta.Cli/bin/Release/net10.0/dbdelta.dll --help
```

## Your first comparison

Compare two databases and print the differences as text:

```bash
dbdelta compare --source "Server=host;Database=Dev;User Id=sa;Password=...;TrustServerCertificate=True" --target "Server=host;Database=Prod;User Id=sa;Password=...;TrustServerCertificate=True"
```

The exit code carries the answer: `0` no differences, `1` differences found, and
a class-specific code for each failure. In a pipeline, read `0` as "nothing to
deploy" rather than as success.

Generate a deployment script that makes the target match the source:

```bash
dbdelta script --source "...Dev..." --target "...Prod..." --out deploy.sql
```

Review `deploy.sql`, then apply it:

```bash
dbdelta apply --target "...Prod..." --script deploy.sql
```

That script opens its own transaction and rolls itself back on the first
failure, which is why `apply` deliberately does not wrap it in a second one —
see [who owns the transaction](cli.md#who-owns-the-transaction).

## The desktop app

The MSI installs it. From source instead:

```bash
dotnet run --project src/DbDelta.App.Avalonia -c Release
```
