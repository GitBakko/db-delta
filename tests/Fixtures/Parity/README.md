# Parity fixture — DbDelta ↔ Redgate SQL Compare

End-to-end calibration: same source / target schema diffed by **DbDelta**
and **Redgate SQL Compare**. The 11-scenario fixture is small enough to
diff outputs line-by-line yet broad enough to surface real divergences
across the kinds DbDelta has shipped (M0 – M13).

## Scenarios

| # | Kind | Divergence |
|--:|------|------------|
| 01 | Table | added column (`Email` only on source) |
| 02 | Table | dropped column (`LegacyCode` only on target) |
| 03 | Table | identity flag flip on existing column — triggers M13-FIX.3 rebuild |
| 04 | Table FK | `ON DELETE` action change (`CASCADE` vs `NO ACTION`) |
| 05 | View | body change (`SELECT 1` vs `SELECT 2`) |
| 06 | Procedure | body change (`TOP (10)` vs `TOP (5)`) |
| 07 | Function | scalar `fnDouble` only on source |
| 08 | Sequence | `START WITH` change (100 vs 1) |
| 09 | Synonym | base-object change (`Customer` vs `Product`) |
| 10 | UDT alias | size change (`nvarchar(200)` vs `nvarchar(100)`) |
| 11 | TableType (UDTT) | column added (`Notes` only on source) |

Users / Roles / Permissions are deliberately out of scope for the v1
parity run — they require server-level logins that complicate fixture
portability and Redgate's defaults skip them too. Re-run parity for
those once the fixture for v0.14 includes them.

## How to run (Windows + SSMS + Redgate SQL Compare)

1. **Bootstrap empty DBs.** Open SSMS against any SQL Server 2016+
   instance (LocalDB / `192.168.3.243` / Azure SQL DB), and execute
   `tests/Fixtures/Parity/00-bootstrap.sql`.

2. **Populate source.** Connect to `DbDeltaParity_Source` and execute
   `tests/Fixtures/Parity/01-source.sql`.

3. **Populate target.** Connect to `DbDeltaParity_Target` and execute
   `tests/Fixtures/Parity/02-target.sql`.

4. **Generate the DbDelta migration script.** From the repo root:

   ```bash
   dotnet src/DbDelta.Cli/bin/Release/net10.0/dbdelta.dll script \
     --source "Server=<host>;Database=DbDeltaParity_Source;User Id=<u>;Password=<p>;TrustServerCertificate=True;Encrypt=False" \
     --target "Server=<host>;Database=DbDeltaParity_Target;User Id=<u>;Password=<p>;TrustServerCertificate=True;Encrypt=False" \
     --out scripts/parity/dbdelta-2026-05-25.sql
   ```

   `scripts/parity/` is git-ignored so the captured output stays local.

5. **Generate the Redgate migration script.** Open Redgate SQL Compare,
   add both DBs as sources, click **Compare Now**, then **Deployment
   Script** → save as `scripts/parity/redgate-2026-05-25.sql`. Use the
   Redgate defaults except:
   - Disable `Include comments` / `Include header` if available so the
     two outputs line up easier — DbDelta only emits a single-line
     header.
   - Keep `Ignore permissions / users / role memberships` ON
     (matches DbDelta's default `ComparisonOptions.IgnorePermissions`).

6. **Paste both outputs back.** Open `docs/parity/redgate-2026-05-25.md`
   and paste the two scripts into the **Captured outputs** section.
   Then ping Claude — the agent diffs the two by scenario and fills the
   verdict table (match / cosmetic / bug).

## Cleanup

```sql
USE [master];
ALTER DATABASE [DbDeltaParity_Source] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [DbDeltaParity_Source];
ALTER DATABASE [DbDeltaParity_Target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [DbDeltaParity_Target];
```

## What a "match" means

The two scripts do not need to be byte-identical — they need to be
**semantically equivalent on the target**: after a SQL Server
successfully runs either of them against an untouched
`DbDeltaParity_Target`, the target schema must match
`DbDeltaParity_Source`.

Common acceptable cosmetic differences:

- `DROP TABLE [dbo].[X]` vs `IF OBJECT_ID('dbo.X') IS NOT NULL DROP TABLE dbo.X`.
- `GO` batch granularity (one statement per batch vs grouped).
- Inline comments / generated-by headers / SET options ordering.
- Identifier quoting style — both `[dbo].[Customer]` and
  `"dbo"."Customer"` are equivalent.
- Naming of system-generated constraints (`PK__Customer__3214EC07…`)
  may differ between source and target; Redgate ignores by default,
  DbDelta currently compares by name. Flag these as **cosmetic**
  unless a destructive drop actually fires.

A **bug** is anything that would either fail to compile on a real
target or leave the target in a state that no longer matches the
source after a successful run.
