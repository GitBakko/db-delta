# Parity fixture — DbDelta ↔ Redgate SQL Compare

End-to-end calibration: same source / target schema diffed by **DbDelta**
and **Redgate SQL Compare**. The 21-scenario fixture is small enough to
diff outputs line-by-line yet broad enough to surface real divergences
across the kinds DbDelta has shipped (M0 – M13), including the #24
cross-kind dependency-ordering edges (scenarios 13–17) and, since
2026-08-20, the four shapes the audit had never reached (18–21) plus one
refusal kept in its own pair of databases (22).

**DbDelta's half is verified before you open Redgate.**
`ParityFixtureTests` applies this fixture to two containerised databases,
generates the script, checks each new scenario's expected shape, applies
it and requires the target to come back Identical. It found a real defect
the first time it ran — see scenario 20 — so the audit starts from a side
that is known to work rather than from one being debugged.

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
| 12 | Table + inbound FK | identity flip on `Order` with `OrderLine` FK — M13-PARITY.6 #33 |
| 13 | Computed col → fn | `fnLineTotal` + `PriceList.LineTotal` source-only — #24 CREATE order |
| 14 | View → view | `vSalesBase` → `vSalesDerived` source-only — #24 CREATE order |
| 15 | View → fn | `fnTaxRate` + `vTaxedItems` source-only — #24 CREATE order |
| 16 | Schemabound TVF → table | `Region` + `tvfRegionLookup` source-only — #24 CREATE order |
| 17 | Multi-hop | `Warehouse` → `fnStockValue` → `vStockReport` source-only — #24 transitive |
| 18 | DROP, reverse topology | `LegacyStock` → `fnLegacyTotal` (SCHEMABINDING) → `vLegacyReport`, all target-only: the function must be dropped before the table or Msg 3729 |
| 19 | Index | filtered predicate (`WHERE IsActive = 1`) on source, unfiltered on target |
| 20 | CHECK over another table | `CK_CustomerOrder_WithinLimit` calls `fnCreditLimit`, which reads `CreditLimit`. **Found a real bug:** the constraint's dependency on the function was dropped by the reader, the table was created first and the deploy died on Msg 4121 |
| 21 | Extended properties | source-only `MS_Description`. DbDelta does NOT script these — it declares them in the census. The scenario measures that gap on purpose |
| 22 | Columnstore index | **In `03-refusals.sql`, its own database pair.** DbDelta reads the difference and REFUSES to script it (exit 30). A refusal stops the whole run, so it cannot live in the main fixture |

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

   **Or, with no live credentials and no licensed GUI:** point
   `DBDELTA_PARITY_DUMP` at a path and run `ParityFixtureTests` — it
   builds the same two databases in a container, generates the script
   with the same inputs `dbdelta script` uses, and writes it there.

   ```bash
   DBDELTA_PARITY_DUMP=scripts/parity/dbdelta-<date>.sql \
     dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests \
     --filter "FullyQualifiedName~ParityFixtureTests"
   ```

   Mind the collation: a container's server default is
   `SQL_Latin1_General_CP1_CI_AS` and a typical live host's is
   `Latin1_General_CI_AS`. The fixture declares no `COLLATE` anywhere, so
   if the two halves come from different servers every collation *name*
   will differ and none of it is a divergence. What to check is that the
   `COLLATE` clause sits on the same set of columns in both scripts.

5. **Generate the Redgate migration script.** Open Redgate SQL Compare,
   add both DBs as sources, click **Compare Now**, then **Deployment
   Script** → save as `scripts/parity/redgate-2026-05-25.sql`. Use the
   Redgate defaults except:
   - Disable `Include comments` / `Include header` if available so the
     two outputs line up easier — DbDelta only emits a single-line
     header.
   - Keep `Ignore permissions / users / role memberships` ON
     (matches DbDelta's default `ComparisonOptions.IgnorePermissions`).

6. **Ask for the diff.** Leave both scripts in `scripts/parity/` and say
   so — the agent diffs them scenario by scenario and writes a new
   `docs/parity/redgate-<date>.md` with the verdict table. The artifacts
   themselves stay untracked, so the audit document has to carry the
   evidence it relies on. Latest run: `docs/parity/redgate-2026-08-31.md`.

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
  may differ between source and target; both tools ignore it. Since
  `142fcb7` DbDelta pairs those by shape, not by name, and creates them
  with no `CONSTRAINT` clause so the target mints its own — a script
  that carries a `PK__`/`DF__` hash across servers is a **bug**, not a
  cosmetic difference. Explicitly named constraints are still compared
  by name on both sides.

A **bug** is anything that would either fail to compile on a real
target or leave the target in a state that no longer matches the
source after a successful run.
