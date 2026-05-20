# 04 — API Endpoints: CLI and Programmatic Surface

SQL Compare exposes no HTTP REST API. "API" in this document means the three programmatic
surfaces through which callers control comparison and deployment:

1. `sqlcompare.exe` — the command-line interface, the primary integration surface for CI/CD
2. The SQL Comparison SDK — a .NET class library (`RedGate.SQLCompare.Engine.dll`) embedded
   in applications
3. SSMS / Visual Studio integration — in-process GUI plug-ins (not addressable from scripts)

SQL Change Automation (formerly ReadyRoll, now part of Redgate Deploy / Flyway Enterprise)
wraps surface 1 with a PowerShell module and orchestration layer for multi-database pipelines.
Our clone separates the single-comparison engine from orchestration in the same way (see
`core-modules.md` §Deployment Executor and §Pipeline Orchestrator).

---

## 1. Surface Overview

### 1.1 `sqlcompare.exe` CLI

The binary ships with SQL Compare Professional Edition and is located at:

```
C:\Program Files (x86)\Red Gate\SQL Compare 16\sqlcompare.exe
```

When SQL Change Automation is installed it is also available at:

```
C:\Program Files (x86)\Red Gate\SQL Change Automation PowerShell\SC\sqlcompare.exe
```

**Licensing requirement.** The Professional Edition license is required. Automating on more
than one machine requires a Redgate Deploy or SQL Toolbelt license. A 14-day free trial is
available. On license failure the process exits with code 402 (see §4).

**Runtime requirements.** .NET Framework 2.0 or later; MDAC 2.8 or later.

**Platform support.** Windows (primary). Linux support was added in later SQL Compare 15/16
releases; on Linux, backslash in server names must be escaped (`\\` or quoted).

### 1.2 SQL Comparison SDK (.NET)

The SQL Comparison SDK is a separately licensed .NET class library historically shipped as
`RedGate.SQLCompare.Engine.dll`. It exposes the same comparison engine used by `sqlcompare.exe`
for embedding inside custom applications — database upgrade installers, custom deployment
portals, ETL pipelines, etc.

Key assemblies required at runtime:

| Assembly | Purpose |
|---|---|
| `RedGate.SQLCompare.Engine.dll` | Core engine — Database, Differences, Work, Options |
| `RedGate.SQLCompare.ASTParser.dll` | T-SQL parser used by the engine |
| `RedGate.SQLCompare.Rewriter.dll` | T-SQL script generation and rewriting |
| `RedGate.Shared.SQL.dll` | Shared SQL connectivity helpers |

The SDK is distributed via Redgate's NuGet feed or the product installer. Documentation is
shipped as a `.chm` help file alongside the DLLs.

### 1.3 SSMS / Visual Studio Integration

SQL Compare installs plug-ins into SSMS and Visual Studio. These operate in-process and are
not scriptable or embeddable independently. They share the same engine DLLs but expose only
GUI entry-points. Our clone need not replicate this surface.

### 1.4 SQL Change Automation / Redgate Deploy

SQL Change Automation (SCA) is the orchestration layer that sits above `sqlcompare.exe`. It
handles migrations, baseline generation, state-based / migration-based hybrid workflows, and
PowerShell cmdlets (`Invoke-DlmDatabaseSchemaValidation`, `New-DlmDatabaseRelease`, etc.).

Boundary rule for our clone: **SQL Compare handles a single comparison**; the orchestration
layer handles pipelines, approvals, and multi-step release packages. Keep these concerns in
separate modules (see `core-modules.md` §Deployment Executor vs §Pipeline Orchestrator).

---

## 2. CLI — Invocation Patterns

### Synopsis

```
sqlcompare  <source>  <target>  [action]  [filters]  [options]  [output]
```

Source and target each select exactly one data source type. Action defaults to
comparison-only (no modification). All switches are case-insensitive.

### 2.1 Database to Database — Compare Only

```bat
sqlcompare /server1:SQLDEV01 /db1:WidgetStaging ^
           /server2:SQLPROD01 /db2:WidgetProduction
```

Exit 0 if databases are identical; exit 63 if identical and `/include:Identical` is not set;
exit 1 for any difference found without `/include:Identical`.

### 2.2 Database to Database — Synchronize (Apply)

```bat
sqlcompare /server1:SQLDEV01 /db1:WidgetStaging ^
           /server2:SQLPROD01 /db2:WidgetProduction ^
           /synchronize
```

The **target** (`/db2`) is modified. The source is never touched.

### 2.3 Database to Database — Generate Script Only

```bat
sqlcompare /db1:WidgetStaging /db2:WidgetProduction ^
           /scriptfile:"C:\Releases\v2.3.0_schema.sql" ^
           /force
```

Produces a T-SQL migration script. No changes are applied to either database.
`/force` overwrites an existing file; without it the process exits 74 if the file exists.

### 2.4 Scripts Folder to Database — Validate Branch Against Test DB

```bat
sqlcompare /scripts1:"D:\repos\widget\database\schema" ^
           /db2:WidgetTest ^
           /server2:SQLTEST01 ^
           /options:Default,IgnoreWhiteSpace
```

### 2.5 Scripts Folder to Database — Apply (State-Based Deploy)

```bat
sqlcompare /scripts1:"D:\repos\widget\database\schema" ^
           /db2:WidgetTest ^
           /server2:SQLTEST01 ^
           /synchronize /force
```

### 2.6 Database to Scripts Folder — Export Schema to Files

```bat
sqlcompare /db1:WidgetProduction ^
           /makescripts:"D:\baseline\widget_v2"
```

Errors if the folder already exists; combine with `/synchronize /force` to merge into an
existing folder.

### 2.7 Snapshot to Database — Drift Detection

```bat
sqlcompare /snapshot1:"C:\baselines\prod_2026-05-01.snp" ^
           /db2:WidgetProduction /server2:SQLPROD01
```

Compares a previously captured point-in-time snapshot against the live database. Non-zero
exit indicates schema drift.

### 2.8 Create Snapshot

```bat
sqlcompare /db1:WidgetProduction /server1:SQLPROD01 ^
           /makesnapshot:"C:\baselines\prod_2026-05-20.snp" ^
           /force
```

### 2.9 Project File — Load Saved Settings and Run

```bat
sqlcompare /project:"C:\SQLCompare\Projects\WidgetDeploy.scp"
```

All settings come from the `.scp` file. Override individual settings on the command line
(see §7 for precedence rules).

### 2.10 Report Only — No Changes Applied

```bat
sqlcompare /db1:WidgetStaging /db2:WidgetProduction ^
           /report:"C:\Reports\diff_2026-05-20.html" ^
           /reporttype:Html
```

### 2.11 Backup to Database — Compare from .bak File

```bat
sqlcompare /backup1:"D:\Backups\WidgetProd_20260520.bak" ^
           /db2:WidgetDev ^
           /scriptfile:"C:\fixes\from_backup.sql"
```

---

## 3. CLI — Argument Reference

Switches are prefixed `/` or `-`. Long-form and alias (short-form) names are both accepted.
Values follow a colon with no space: `/server1:MYSERVER`. Strings with spaces must be quoted:
`/scripts1:"C:\My Folder\schema"`.

### 3.1 Server and Database Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Server1` | `/s1` | String | Local | Hostname (optionally `\instance`) for source database. On Linux escape backslashes. Supports appended port, `encrypt=true`, `trustservercertificate=true`. | `/server1:SQLDEV\SQL2019` |
| `/Server2` | `/s2` | String | Local | Hostname for target database | `/server2:"SQLPROD,1433;encrypt=true"` |
| `/Database1` | `/db1` | String | — | Source database name | `/db1:WidgetStaging` |
| `/Database2` | `/db2` | String | — | Target database name | `/db2:WidgetProduction` |

**Advanced server string syntax.** To specify port and encryption on a single `/server` value:

```bat
/server1:"Widget_Server\SQL2019,1433;encrypt=true;trustservercertificate=true"
```

### 3.2 Authentication Switches

When neither `/userName1` nor `/activedirectory1` is specified, Windows Integrated Security
is used for that connection.

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/UserName1` | `/u1` | String | Integrated | SQL auth username for source | `/u1:deploy_user` |
| `/UserName2` | `/u2` | String | Integrated | SQL auth username for target | `/u2:deploy_user` |
| `/Password1` | `/p1` | String | — | SQL auth password for source; must pair with `/u1` | `/p1:P@ssw0rd` |
| `/Password2` | `/p2` | String | — | SQL auth password for target | `/p2:P@ssw0rd` |
| `/activedirectory1` | `/ad1` | Flag | Off | Use Azure Active Directory auth for source. If `/u1`+`/p1` also supplied, uses AAD password auth; otherwise uses AAD integrated. | `/ad1` |
| `/activedirectory2` | `/ad2` | Flag | Off | Use Azure Active Directory auth for target | `/ad2` |

**Windows auth (default):** omit all four `/userName`/`/password` switches.

**Azure SQL with AAD interactive** is only available in the GUI. For automation use AAD
password auth (`/activedirectory1 /u1:user@tenant.com /p1:secret`) or a service principal
(embed credentials via environment variables and pass as `/u1`/`/p1`).

### 3.3 Scripts Folder Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Scripts1` | `/scr1` | Path | — | Scripts folder as source | `/scr1:"D:\repos\db\schema"` |
| `/Scripts2` | `/scr2` | Path | — | Scripts folder as target | `/scr2:"D:\repos\db\schema"` |
| `/MakeScripts` | `/mkscr` | Path | — | Export data source to a new scripts folder | `/mkscr:"D:\exports\prod_v2"` |
| `/ScriptsFolderXML` | `/sfx` | Path | — | Text file containing XML describing a source-control-linked scripts folder location (used with `/sourcecontrol1`/`/sourcecontrol2`) | `/sfx:"C:\sc_config.txt"` |
| `/IgnoreSourceCaseSensitivity` | — | Flag | Off | Disable automatic case-sensitivity detection when creating a scripts folder | `/ignoresourcecasesensitivity` |

### 3.4 Snapshot Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Snapshot1` | `/sn1` | Path | — | Snapshot file as source | `/sn1:"C:\snaps\prod_baseline.snp"` |
| `/Snapshot2` | `/sn2` | Path | — | Snapshot file as target | `/sn2:"C:\snaps\prod_current.snp"` |
| `/MakeSnapshot` | `/mksnap` | Path | — | Create a snapshot from the data source; errors if file exists unless `/force` | `/mksnap:"C:\snaps\prod_20260520.snp"` |

### 3.5 Backup Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Backup1` | `/b1` | Path(s) | — | `.bak` or `.sqb` file(s) as source; separate multiple files with semicolons | `/b1:"D:\Full.bak";"D:\Diff.bak"` |
| `/Backup2` | `/b2` | Path(s) | — | Backup file(s) as target | `/b2:"D:\target.bak"` |
| `/BackupSet1` | `/bks1` | String | Latest | Named backup set within a multi-set file | `/bks1:"2026-05-20 Full Backup"` |
| `/BackupSet2` | `/bks2` | String | Latest | Named backup set for target backup | `/bks2:"2026-05-20 Full Backup"` |
| `/BackupPasswords1` | `/bpsw1` | String | — | Comma-separated passwords for encrypted source backup | `/bpsw1:P@ss1,P@ss2` |
| `/BackupPasswords2` | `/bpsw2` | String | — | Passwords for encrypted target backup | `/bpsw2:P@ss` |
| `/MakeBackup` | — | Flag | Off | Back up the target before synchronization (uses Redgate SQL Backup Pro or SQL Server native) | `/makebackup` |
| `/BackupFile` | `/bf` | String | Auto | Filename for created backup (`.sqb` for Redgate, `.bak` for native) | `/bf:WidgetProd_pre.sqb` |
| `/BackupFolder` | `/bd` | Path | SQL Server default | Directory for created backup | `/bd:"E:\Backups"` |
| `/BackupType` | `/bt` | Enum | Full | `Full` or `Differential` | `/bt:Differential` |
| `/BackupProvider` | `/bpr` | Enum | Native | `Native` (`.bak`) or `SQB` (Redgate format) | `/bpr:Native` |
| `/BackupCompression` | `/bc` | 1–3 | Off | Redgate backup compression level (1=fastest, 3=smallest) | `/bc:2` |
| `/BackupEncryption` | `/be` | Flag | Off | Encrypt backup with 128-bit encryption; requires `/BackupPassword` | `/be /bp:secret` |
| `/BackupPassword` | `/bp` | String | — | Password for encrypted backup | `/bp:P@ssw0rd` |
| `/BackupNumberOfThreads` | `/bth` | Int | 1 | Parallel backup threads (max 32; recommended = CPUs − 1) | `/bth:3` |
| `/BackupOverwriteExisting` | `/boe` | Flag | Off | Overwrite existing backup file of same name | `/boe` |

### 3.6 Source Control Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Sourcecontrol1` | — | Flag | Off | Use a source-control-linked scripts folder as source; requires `/sfx` | `/sourcecontrol1 /sfx:cfg.txt` |
| `/Sourcecontrol2` | — | Flag | Off | Source-control-linked scripts folder as target | `/sourcecontrol2 /sfx:cfg.txt` |
| `/Revision1` | `/r1` | String | HEAD | Source control revision for source (TFS, SVN, Vault); `HEAD` = latest | `/r1:1042` |
| `/Revision2` | `/r2` | String | HEAD | Source control revision for target | `/r2:HEAD` |
| `/VersionUserName1` | `/vu1` | String | Saved | Username for source control server linked to source | `/vu1:svc_build` |
| `/VersionUserName2` | `/vu2` | String | Saved | Username for source control server linked to target | `/vu2:svc_build` |
| `/VersionPassword1` | `/vp1` | String | Saved | Password for source control server linked to source | `/vp1:secret` |
| `/VersionPassword2` | `/vp2` | String | Saved | Password for source control server linked to target | `/vp2:secret` |

### 3.7 SQL Change Automation Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Sca1` | — | Path | — | Path to `.sqlproj` file for an SCA project as source | `/sca1:"C:\db\MyDb.sqlproj"` |
| `/Sca2` | — | Path | — | Path to `.sqlproj` file for an SCA project as target | `/sca2:"C:\db\MyDb.sqlproj"` |

### 3.8 Project File Switch

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Project` | `/pr` | Path | — | Load a `.scp` SQL Compare project file; most settings come from the project; individual switches can override (see §7) | `/project:"C:\Projects\Widget.scp"` |
| `/OutputProject` | `/outpr` | Path | — | Save the effective settings used by this run to a new `.scp` file | `/outproject:"C:\out\Widget_run.scp"` |
| `/Argfile` | — | Path | — | Load an XML argument specification file (see §6); only `/verbose` or `/quiet` may be combined on the command line | `/argfile:"C:\args\prod_deploy.xml"` |

### 3.9 Deployment and Synchronization Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Synchronize` | `/sync`, `/__synchronise` | Flag | Off | Apply the migration script to the target database after comparison | `/sync` |
| `/ScriptFile` | `/sf` | Path | — | Write the generated migration T-SQL to a file without applying it; errors if file exists unless `/force` | `/sf:"C:\rel\migrate.sql"` |
| `/SyncScriptEncoding` | `/senc` | Enum | UTF8 | Encoding for the generated script file: `UTF8`, `UTF8WithPreamble`, `Unicode`, `ASCII` | `/senc:UTF8WithPreamble` |
| `/TransactionIsolationLevel` | `/til` | Enum | Server default | Isolation level written into the deployment script: `READ UNCOMMITTED`, `READ COMMITTED`, `REPEATABLE READ`, `SNAPSHOT`, `SERIALIZABLE` | `/til:SERIALIZABLE` |
| `/AbortOnWarnings` | `/aow` | Enum | None | Abort deployment if warnings are found at this level or higher: `None`, `Medium`, `High`; returns exit 61 | `/aow:Medium` |
| `/ShowWarnings` | `/warn` | Flag | Off | Display deployment warnings in console output even when not aborting | `/warn` |
| `/MigrationsFolder` | `/mf` | Path | — | Path to a folder containing migration scripts that should be included in the deployment | `/mf:"D:\db\migrations"` |
| `/MigrationsFolderXML` | `/mfx` | Path | — | Text file describing source-control location of migration scripts | `/mfx:"C:\mig_cfg.txt"` |
| `/empty2` | — | Flag | Off | Use an empty database as target — produces a CREATE script for the entire source schema. Designed for use with SQL Packager. | `/empty2` |
| `/MakeBackup` | — | Flag | Off | Back up the target before synchronizing | `/sync /makebackup` |

### 3.10 Filter and Include/Exclude Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/include` | — | String | All | Include objects matching `[Type][:regex]`; multiple `/include` switches allowed | `/include:Table:Widget.*` |
| `/exclude` | — | String | None | Exclude objects matching `[Type][:regex]`; takes priority over `/include` | `/exclude:Table:_temp.*` |
| `/Filter` | `/ftr` | Path | — | Path to a `.scpf` filter file created in the SQL Compare GUI; cannot combine with `/include` or `/exclude` | `/filter:"C:\filters\mktg.scpf"` |

**Supported object type tokens for `/include` and `/exclude`:**

`Additional`, `Missing`, `Different`, `Identical`, `StaticData`, `Assembly`,
`AsymmetricKey`, `Certificate`, `Contract`, `DdlTrigger`, `EventNotification`,
`ExtendedProperty`, `ExternalDataSource`, `ExternalFileFormat`, `ExternalTable`,
`FullTextCatalog`, `FullTextStoplist`, `Function`, `MessageType`, `PartitionFunction`,
`PartitionScheme`, `Queue`, `Role`, `Route`, `Rule`, `Schema`, `SearchPropertyList`,
`Sequence`, `Service`, `ServiceBinding`, `StoredProcedure`, `SymmetricKey`, `Synonym`,
`Table`, `User`, `UserDefinedType`, `View`, `XmlSchemaCollection`

**Mutual-exclusivity rules:**
- `/include` and `/exclude` cannot be used with `/project`
- `/filter` cannot be used with `/include` or `/exclude`
- `/filter` overrides the filter saved in a project file

### 3.11 Comparison Options Switch

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Options` | `/o` | CSV | Default | Comma-separated list of option names to apply (see §5 for full option table) | `/options:Default,IgnoreComments` |
| `/DataCompareOptions` | `/dco` | CSV | (see below) | Options for static-data comparison | `/dco:Default,DropConstraintsAndIndexes` |

Default `/DataCompareOptions`: `IgnoreSpaces`, `IncludeIdentities`, `DisableKeys`,
`OutputCommentHeader`, `ReseedIdentity`, `MissingFrom2AsInclude`.

Use `none` to start with zero options: `/options:none,IgnoreWhiteSpace`.
Use `Default` to include all default-on options plus any additional ones listed.

### 3.12 Report Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Report` | `/r` | Path | — | Generate a comparison report and write to file | `/report:"C:\rpt\diff.html"` |
| `/ReportType` | `/rt` | Enum | XML | Report format: `XML`, `Html`, `Classic`, `Excel` | `/rt:Html` |
| `/ReportAllObjectsWithDifferences` | `/rad` | Flag | Off | Include all differing objects in the report (default is selected objects only) | `/rad` |

Report type is also inferred from the file extension: `.html`/`.htm` → Html, `.xml` → XML,
`.xls` → Excel. `Classic` HTML is not available on Linux.

### 3.13 Output and Logging Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Out` | — | Path | stdout | Redirect console output to file | `/out:"C:\logs\run.txt"` |
| `/OutputWidth` | — | Int | 80 | Force console output column width; prevents truncation when redirecting | `/outputwidth:200` |
| `/Verbose` | `/v` | Flag | Off | Per-object detailed output | `/verbose` |
| `/Quiet` | `/q` | Flag | Off | Suppress all output (only exit code) | `/quiet` |
| `/LogLevel` | `/log` | Enum | None | Create a structured log file at: `None`, `Error`, `Warning`, `Verbose` | `/loglevel:Verbose` |

### 3.14 Control and Assertion Switches

| Switch | Alias | Type | Default | Description | Example |
|---|---|---|---|---|---|
| `/Assertidentical` | — | Flag | Off | Exit 0 if objects are identical; exit 79 if any difference found | `/assertidentical` |
| `/Force` | `/f` | Flag | Off | Overwrite existing output files (script, report, snapshot, project) | `/force` |
| `/IgnoreParserErrors` | — | Flag | Off | Continue (do not exit 62) when the scripts folder contains objects that fail to parse | `/ignoreparsererrors` |
| `/TempInstance` | `/ti` | String | LocalDB | SQL Server instance used instead of LocalDB for temporary operations | `/ti:"Server=localhost;..."` |
| `/allUsers` | — | Flag | Off | Activate SQL Compare for all users including Windows service accounts | `/allusers` |

### 3.15 Licensing Switches

| Switch | Type | Description |
|---|---|---|
| `/activateSerial:<key>` | String | Activate SQL Compare with a serial number |
| `/deactivateSerial` | Flag | Deactivate (requires internet); case-sensitive |

### 3.16 Help Switches

| Switch | Alias | Type | Description |
|---|---|---|---|
| `/Help` | `/?` | Flag | Display all switches with basic descriptions; exits 0 |
| `/HTML` | — | Flag | Output help as HTML (must combine with `/help`) |

---

## 4. CLI — Exit Codes

The process exit code is the primary signal for callers. **Always test `$LASTEXITCODE`
(PowerShell) or `%ERRORLEVEL%` (cmd) rather than parsing console output.**

| Code | Category | Meaning | Recommended Caller Behavior |
|---|---|---|---|
| **0** | Success | Comparison or deployment completed; databases are identical (with `/assertidentical`) | Continue pipeline |
| **1** | General error | Unspecified failure during execution | Log output; fail build |
| **3** | Argument error | A switch was supplied more than once | Fix command line; fail build |
| **8** | Argument error | Missing required argument, or mutually exclusive switches combined | Fix command line; fail build |
| **32** | Argument error | Numeric argument value is out of range | Fix argument; fail build |
| **33** | Argument error | Numeric argument value overflows the type | Fix argument; fail build |
| **34** | Argument error | Argument contains an invalid value | Fix argument; fail build |
| **35** | License error | License or trial period has expired | Renew license; fail build |
| **61** | Deployment warning | `/AbortOnWarnings` threshold was met; deployment not applied | Investigate warnings; block deployment |
| **62** | Parser error | Scripts folder contains T-SQL that failed to parse at high level | Use `/ignoreparsererrors` to demote, or fix scripts |
| **63** | Identical | Compared objects are identical (without `/include:Identical`) | Treat as success if drift-detection is not expected to find differences |
| **64** | Argument error | Command-line syntax or flag combination is incorrect | Fix command line |
| **65** | Data error | Invalid or corrupt required data | Verify source/target integrity |
| **69** | Resource error | Required resource or dependency unavailable | Check connectivity, service state |
| **70** | Unhandled exception | An exception was not caught internally | Report bug; check logs for stack trace |
| **73** | I/O error | Report file generation failed | Verify output path and permissions |
| **74** | I/O error | Output file already exists and `/force` was not specified | Add `/force` or remove existing file |
| **77** | Permission error | Insufficient database or file system permissions | Grant required rights to service account |
| **79** | Not identical | `/assertidentical` flag used and differences were found | Treat as drift; alert; block deployment |
| **126** | SQL Server error | Error during SQL Server execution (e.g., connection failure, statement error) | Check SQL Server logs; verify credentials |
| **130** | User interrupt | Ctrl-Break was pressed | Retry; investigate why run was interrupted |
| **400** | Bad request | Argument combination is semantically invalid | Remove conflicting switches |
| **402** | License error | No valid license found | Activate product |
| **499** | Activation | License activation was cancelled | Restart activation |
| **500** | Unhandled exception | Unexpected error; details in console output | Check logs; report bug |

### Quick-reference for CI/CD callers

```powershell
# PowerShell — standard pattern
& sqlcompare.exe /db1:Staging /db2:Production /sync /aow:Medium
switch ($LASTEXITCODE) {
    0   { Write-Host "Success" }
    61  { Write-Error "Deployment aborted: warnings exceeded threshold"; exit 1 }
    63  { Write-Host "Databases are identical — nothing to deploy" }
    74  { Write-Error "Output file already exists; use -Force"; exit 1 }
    default { Write-Error "SQL Compare failed: exit code $LASTEXITCODE"; exit 1 }
}
```

---

## 5. CLI — Output Modes

### 5.1 Default Console Output

Produces a human-readable summary: object counts, difference categories, warnings, and a
final status line. Suitable for interactive use. Example (abbreviated):

```
Connecting to WidgetStaging...
Connecting to WidgetProduction...
Comparing databases...
  Tables:     12 identical, 3 different, 1 missing from target
  Views:      5 identical
  Procedures: 2 different
Deployment warnings: none
Synchronizing...
Deployment complete.
```

### 5.2 Verbose Mode (`/verbose`)

Each object that differs is listed with its type, name, and change category
(Added, Dropped, Modified). Use for audit trails:

```
[DIFFERENT]  Table  [dbo].[Orders]
[MISSING]    Table  [dbo].[OrderArchive]
[DIFFERENT]  StoredProcedure  [dbo].[usp_GetOrder]
```

### 5.3 Quiet Mode (`/quiet`)

Suppresses all output. Only the exit code is meaningful. Use when the invoking system
handles its own logging and console noise is unwanted.

### 5.4 Report Files

Use `/report` with `/reporttype` to produce a structured file:

| Format | `/reporttype` value | Extension | Notes |
|---|---|---|---|
| HTML (modern) | `Html` | `.html` | Browser-viewable; recommended |
| HTML (classic) | `Classic` | `.html` | Legacy; not available on Linux |
| XML | `XML` (default) | `.xml` | Machine-parseable; good for dashboards |
| Excel | `Excel` | `.xls` | For spreadsheet-based sign-off workflows |

**Example — HTML report for PR review:**

```bat
sqlcompare /scripts1:"D:\repos\feature-branch\schema" ^
           /db2:WidgetTest ^
           /report:"C:\artifacts\schema_diff.html" ^
           /reporttype:Html ^
           /rad
```

The `/rad` flag (ReportAllObjectsWithDifferences) ensures every differing object appears in
the report even if not selected for deployment.

### 5.5 Generated T-SQL Script

The `/scriptfile` output is a complete, idempotent T-SQL migration that can be reviewed,
stored in source control, and executed later. The script includes:

- A header comment block (unless `DoNotOutputCommentHeader` option is set)
- `SET` statements (`ANSI_NULLS`, `QUOTED_IDENTIFIER`, transaction isolation)
- `IF EXISTS`/`IF NOT EXISTS` guards (when `ObjectExistenceChecks` option is set)
- The migration statements grouped by dependency order
- A transaction wrapper (unless `NoTransactions` option is set)

---

## 6. CLI — Argfile Format

An argfile is an XML document that captures a full set of switches. It is the recommended
approach when a command line becomes long, when the same switches are reused across runs, or
when credentials must not appear in shell history.

**Invocation:**

```bat
sqlcompare /argfile:"C:\config\prod_deploy.xml"
```

Only `/verbose` or `/quiet` may be added to the command line alongside `/argfile`. All other
switches must be inside the XML.

### 6.1 XML Schema

```xml
<?xml version="1.0" encoding="utf-8"?>
<commandline>
  <!-- Every switch maps to an element with the same name (case-insensitive).
       Flag switches use self-closing elements.
       Value switches use element text content. -->
  <switchname>value</switchname>
  <flagswitch/>
</commandline>
```

### 6.2 Complete Example — Staging to Production Deploy with Report

```xml
<?xml version="1.0" encoding="utf-8"?>
<commandline>

  <!-- Source: staging database -->
  <server1>SQLDEV01</server1>
  <database1>WidgetStaging</database1>
  <username1>deploy_svc</username1>
  <password1>$(DB_PASSWORD)</password1>

  <!-- Target: production database -->
  <server2>SQLPROD01</server2>
  <database2>WidgetProduction</database2>
  <username2>deploy_svc</username2>
  <password2>$(DB_PASSWORD)</password2>

  <!-- Comparison options -->
  <options>Default,IgnoreComments,IgnoreWhiteSpace</options>

  <!-- Only deploy tables, views, and stored procedures -->
  <include>Table</include>
  <include>View</include>
  <include>StoredProcedure</include>
  <exclude>Table:_tmp.*</exclude>

  <!-- Abort if there are medium or higher deployment warnings -->
  <abortOnWarnings>Medium</abortOnWarnings>

  <!-- Generate script (for review / audit) but also apply -->
  <scriptfile>C:\Releases\v2.3.0_widget_schema.sql</scriptfile>
  <force/>
  <synchronize/>

  <!-- Back up production before applying -->
  <makebackup/>
  <backupfolder>E:\DBBackups</backupfolder>
  <backupprovider>Native</backupprovider>

  <!-- Report -->
  <report>C:\Releases\v2.3.0_diff_report.html</report>
  <reporttype>Html</reporttype>

  <!-- Encoding for the generated script -->
  <syncscriptencoding>UTF8</syncscriptencoding>

</commandline>
```

### 6.3 Argfile for Snapshot Generation

```xml
<?xml version="1.0" encoding="utf-8"?>
<commandline>
  <server1>SQLPROD01</server1>
  <database1>WidgetProduction</database1>
  <username1>readonly_svc</username1>
  <password1>$(READONLY_PASSWORD)</password1>
  <makesnapshot>C:\Baselines\prod_2026-05-20.snp</makesnapshot>
  <force/>
</commandline>
```

### 6.4 Argfile for Drift Detection

```xml
<?xml version="1.0" encoding="utf-8"?>
<commandline>
  <snapshot1>C:\Baselines\prod_baseline.snp</snapshot1>
  <server2>SQLPROD01</server2>
  <database2>WidgetProduction</database2>
  <username2>readonly_svc</username2>
  <password2>$(READONLY_PASSWORD)</password2>
  <assertidentical/>
  <report>C:\Alerts\drift_report.html</report>
  <reporttype>Html</reporttype>
</commandline>
```

### 6.5 Credential Handling Note

Argfiles may contain passwords in plaintext. Restrict file permissions (read for the service
account only). In CI/CD systems, substitute secrets at runtime using environment variable
expansion or a secrets manager — do not commit argfiles containing passwords to source
control.

---

## 7. CLI — Project File Reuse

A SQL Compare project file (`.scp`) stores a complete configuration including connection
details, filter settings, comparison options, and object selections. Project files are created
and saved from the SQL Compare GUI.

### 7.1 Basic Usage

```bat
sqlcompare /project:"C:\Projects\WidgetDeploy.scp"
```

All settings come from the project file. All objects are included regardless of the object
selection saved in the GUI project.

### 7.2 Overriding Project Settings

Command-line switches that specify a data source override the values in the project:

```bat
sqlcompare /project:"C:\Projects\WidgetDeploy.scp" ^
           /server2:SQLPROD02 ^
           /db2:WidgetProductionUS
```

This uses all settings from the project file (source, options, filters) but deploys to a
different target server and database.

### 7.3 Precedence Rules

| Priority | Source |
|---|---|
| 1 (highest) | Command-line switches for data sources (`/server2`, `/db2`, etc.) |
| 2 | Project file settings |
| 3 | Default values |

**Mutual-exclusivity constraints still apply even when using `/project`:**
- `/include` and `/exclude` cannot be combined with `/project`
- `/filter` can be combined with `/project` and overrides the project's saved filter

### 7.4 Saving Effective Settings

To capture the effective settings of a run (useful for reproducing CI results locally):

```bat
sqlcompare /db1:WidgetStaging /db2:WidgetProduction ^
           /options:Default,IgnoreComments ^
           /outputproject:"C:\Projects\Reproduced.scp"
```

---

## 8. CLI — Automation Patterns

### 8.1 PR Validation — Compare Scripts Folder to Test Database

**Goal:** Fail the pull request build if the schema changes in the branch introduce deployment
warnings or cannot be applied to the test environment.

**GitHub Actions (`.github/workflows/schema-validate.yml`):**

```yaml
name: Schema Validation

on:
  pull_request:
    paths:
      - 'database/schema/**'

jobs:
  validate-schema:
    runs-on: windows-latest

    steps:
      - uses: actions/checkout@v4

      - name: Validate schema against test database
        env:
          DB_PASSWORD: ${{ secrets.TEST_DB_PASSWORD }}
        shell: pwsh
        run: |
          $sqlcompare = "C:\Program Files (x86)\Red Gate\SQL Compare 16\sqlcompare.exe"

          & $sqlcompare `
            /scripts1:"${{ github.workspace }}\database\schema" `
            /server2:"${{ vars.TEST_SQL_SERVER }}" `
            /db2:"WidgetTest" `
            /username2:"deploy_svc" `
            /password2:"$env:DB_PASSWORD" `
            /options:Default,IgnoreWhiteSpace `
            /aow:High `
            /report:"${{ github.workspace }}\artifacts\schema_diff.html" `
            /reporttype:Html

          if ($LASTEXITCODE -eq 63) {
            Write-Host "No schema differences detected."
            exit 0
          }
          if ($LASTEXITCODE -ne 0) {
            Write-Error "Schema validation failed with exit code $LASTEXITCODE"
            exit 1
          }

      - name: Upload diff report
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: schema-diff-report
          path: artifacts/schema_diff.html
```

### 8.2 Auto-Deploy to Test — Apply Schema on Merge

**Goal:** Automatically apply schema changes to the test database when a PR is merged to
the `main` branch.

**GitHub Actions:**

```yaml
name: Deploy Schema to Test

on:
  push:
    branches: [main]
    paths:
      - 'database/schema/**'

jobs:
  deploy-test:
    runs-on: windows-latest

    steps:
      - uses: actions/checkout@v4

      - name: Generate migration script
        env:
          DB_PASSWORD: ${{ secrets.TEST_DB_PASSWORD }}
        shell: pwsh
        run: |
          $sqlcompare = "C:\Program Files (x86)\Red Gate\SQL Compare 16\sqlcompare.exe"
          $scriptPath = "${{ github.workspace }}\artifacts\migration_${{ github.sha }}.sql"

          & $sqlcompare `
            /scripts1:"${{ github.workspace }}\database\schema" `
            /server2:"${{ vars.TEST_SQL_SERVER }}" `
            /db2:"WidgetTest" `
            /username2:"deploy_svc" `
            /password2:"$env:DB_PASSWORD" `
            /scriptfile:$scriptPath `
            /force `
            /aow:Medium

          if ($LASTEXITCODE -eq 63) {
            Write-Host "No differences — skipping deployment."
            exit 0
          }
          if ($LASTEXITCODE -ne 0) {
            Write-Error "Script generation failed: $LASTEXITCODE"
            exit 1
          }

      - name: Apply migration script
        env:
          DB_PASSWORD: ${{ secrets.TEST_DB_PASSWORD }}
        shell: pwsh
        run: |
          $sqlcompare = "C:\Program Files (x86)\Red Gate\SQL Compare 16\sqlcompare.exe"

          & $sqlcompare `
            /scripts1:"${{ github.workspace }}\database\schema" `
            /server2:"${{ vars.TEST_SQL_SERVER }}" `
            /db2:"WidgetTest" `
            /username2:"deploy_svc" `
            /password2:"$env:DB_PASSWORD" `
            /synchronize /force `
            /aow:Medium

          if ($LASTEXITCODE -eq 63) { exit 0 }
          if ($LASTEXITCODE -ne 0) {
            Write-Error "Deployment failed: $LASTEXITCODE"; exit 1
          }

      - name: Upload script artifact
        uses: actions/upload-artifact@v4
        with:
          name: migration-script
          path: artifacts/migration_*.sql
```

### 8.3 Drift Detection — Alert When Production Diverges from Baseline

**Azure DevOps Pipeline (`azure-pipelines-drift.yml`):**

```yaml
trigger: none

schedules:
  - cron: "0 6 * * *"   # 06:00 UTC daily
    displayName: Daily drift check
    branches:
      include: [main]
    always: true

pool:
  vmImage: windows-latest

steps:
  - task: PowerShell@2
    displayName: Capture baseline snapshot
    inputs:
      targetType: inline
      script: |
        $sqlcompare = "C:\Program Files (x86)\Red Gate\SQL Compare 16\sqlcompare.exe"
        $baseline  = "$(Build.SourcesDirectory)\baselines\prod_baseline.snp"

        & $sqlcompare `
          /server1:"$(PROD_SQL_SERVER)" `
          /db1:"WidgetProduction" `
          /username1:"readonly_svc" `
          /password1:"$(PROD_DB_PASSWORD)" `
          /snapshot1:$baseline `
          /db2:"WidgetProduction" `
          /username2:"readonly_svc" `
          /password2:"$(PROD_DB_PASSWORD)" `
          /assertidentical `
          /report:"$(Build.ArtifactStagingDirectory)\drift_report.html" `
          /reporttype:Html

        if ($LASTEXITCODE -eq 79) {
          Write-Host "##vso[task.logissue type=warning]Schema drift detected in production!"
          Write-Host "##vso[task.complete result=SucceededWithIssues;]Drift detected"
        } elseif ($LASTEXITCODE -ne 0) {
          Write-Host "##vso[task.complete result=Failed;]Drift check failed"
          exit 1
        }

  - task: PublishBuildArtifacts@1
    displayName: Publish drift report
    condition: always()
    inputs:
      PathtoPublish: $(Build.ArtifactStagingDirectory)
      ArtifactName: drift-report
```

### 8.4 Release Packaging — Generate Script for Manual Review

**PowerShell script for manual release workflow:**

```powershell
# generate-release-script.ps1
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $SourceServer,
    [Parameter(Mandatory)] [string] $SourceDb,
    [Parameter(Mandatory)] [string] $TargetServer,
    [Parameter(Mandatory)] [string] $TargetDb,
    [Parameter(Mandatory)] [string] $Password,
    [string] $OutputDir = "C:\Releases"
)

$sqlcompare  = "C:\Program Files (x86)\Red Gate\SQL Compare 16\sqlcompare.exe"
$scriptFile  = Join-Path $OutputDir "release_$Version_schema.sql"
$reportFile  = Join-Path $OutputDir "release_$Version_diff.html"

Write-Host "Generating release script for v$Version..."

& $sqlcompare `
    /server1:$SourceServer /db1:$SourceDb `
    /server2:$TargetServer /db2:$TargetDb `
    /username1:deploy_svc /password1:$Password `
    /username2:deploy_svc /password2:$Password `
    /options:Default,IgnoreWhiteSpace `
    /aow:High `
    /scriptfile:$scriptFile `
    /report:$reportFile `
    /reporttype:Html `
    /force

switch ($LASTEXITCODE) {
    0   { Write-Host "Script generated: $scriptFile" }
    61  { Write-Error "Release blocked: deployment warnings exceeded threshold."; exit 1 }
    63  { Write-Host "No differences between environments."; exit 0 }
    74  { Write-Error "Output file exists. Use -Force or remove existing file."; exit 1 }
    default { Write-Error "sqlcompare.exe failed with exit code $LASTEXITCODE"; exit 1 }
}

Write-Host "Diff report: $reportFile"
Write-Host ""
Write-Host "Review the script before executing:"
Write-Host "  sqlcmd -S $TargetServer -d $TargetDb -i '$scriptFile'"
```

---

## 9. SDK — Overview

The SQL Comparison SDK is a .NET class library that exposes the full SQL Compare engine
for embedding in custom applications. It is historically a separately licensed product
from SQL Compare itself, though both share the same underlying engine DLLs.

**Supported frameworks:** .NET Framework 4.x; .NET 6/8 support was added in later SDK
versions (verify against your SDK release notes).

**Target languages:** C#, VB.NET, F#, or any .NET-compatible language.

**Primary namespace:** `RedGate.SQLCompare.Engine`

**Secondary namespaces:**

| Namespace | Purpose |
|---|---|
| `RedGate.SQLCompare.Engine` | Core comparison, synchronization, object model |
| `RedGate.Shared.SQL` | Connection properties, SQL execution helpers |
| `RedGate.SQLCompare.Engine.SchemaObjectTypes` | Object type constants |

**Package distribution:** Available from the Redgate NuGet feed or extracted from the product
installer. The `.chm` help file ships alongside the assemblies. The SDK 11 documentation is
the most recent publicly indexed version; later versions may be available under separate
agreement.

**Licensing model:** Requires a SQL Comparison SDK serial number separate from the SQL
Compare desktop license. Runtime-only deployment (no SDK development) may have different
terms; consult Redgate sales for per-server or OEM licensing.

---

## 10. SDK — Core Types

### 10.1 Type Signatures (Pseudocode)

```csharp
namespace RedGate.SQLCompare.Engine
{
    // Represents one side of a comparison (database, snapshot, or scripts folder)
    public class Database : IDisposable
    {
        // Register a live SQL Server database
        public void Register(ConnectionProperties connection, Options options);

        // Register a snapshot file (.snp)
        public void Register(string snapshotPath, Options options);

        // Register a scripts folder
        public void RegisterForScriptsFolder(
            DirectoryInfo folder, Options options);

        // Run comparison; returns the difference set
        public Differences CompareWith(Database target, Options options);

        // Collection of all schema objects in this database
        public DatabaseObjectCollection Objects { get; }

        // Display name for logging
        public string Name { get; }
    }

    // Holds all differences between two registered databases
    public class Differences : IEnumerable<Difference>
    {
        // Filter by object type
        public IEnumerable<Difference> ByType(ObjectType type);

        // Number of differences
        public int Count { get; }
    }

    // A single schema object difference
    public class Difference
    {
        // The schema object type (Table, View, StoredProcedure, ...)
        public ObjectType Type { get; }

        // Object name (schema-qualified)
        public string Name { get; }

        // Change category
        public DifferenceType DifferenceType { get; }

        // Whether this difference is selected for deployment
        public bool Selected { get; set; }

        // Access to the object definition on each side
        public DatabaseObject DatabaseObject1 { get; }
        public DatabaseObject DatabaseObject2 { get; }
    }

    // Builds and executes the migration work
    public class Work : IDisposable
    {
        public Work();

        // Populate the work from a Differences set
        public void BuildFromDifferences(
            Differences differences,
            Options options,
            bool runtimeOnly);

        // The generated T-SQL script as a string
        public string ScriptDifferences();

        // Execute the script against a target connection
        public ExecutionBlock ExecutionBlock { get; }
    }

    // Represents the executable SQL migration block
    public class ExecutionBlock : IDisposable
    {
        // Execute the block against a SQL Server connection
        public void Execute(ConnectionProperties target);

        // Access the raw SQL
        public string Sql { get; }
    }

    // SQL Server connection details
    public class ConnectionProperties
    {
        public ConnectionProperties(string serverName, string databaseName);
        public ConnectionProperties(
            string serverName, string databaseName,
            string userName, string password);

        public string ServerName { get; set; }
        public string DatabaseName { get; set; }
        public string UserName { get; set; }   // null = Windows auth
        public string Password { get; set; }
        public bool UseWindowsAuthentication { get; set; }
    }

    // Flags enum controlling comparison and deployment behavior
    [Flags]
    public enum Options : long
    {
        None                         = 0,
        Default                      = <composite of default-on flags>,
        IgnoreWhiteSpace             = ...,
        IgnoreComments               = ...,
        IgnoreFillFactor             = ...,
        IgnoreFileGroups             = ...,
        IgnoreUserProperties         = ...,
        IgnoreWithElementOrder       = ...,
        IgnoreDatabaseAndServerName  = ...,
        IncludeDependencies          = ...,
        AddDatabaseUseStatement      = ...,
        IgnoreCollations             = ...,
        IgnoreConstraintAndIndexNames= ...,
        IgnoreExtendedProperties     = ...,
        IgnoreForeignKeys            = ...,
        IgnoreIndexes                = ...,
        IgnorePermissions            = ...,
        IgnoreStatistics             = ...,
        IgnoreTriggers               = ...,
        NoTransactions               = ...,
        ObjectExistenceChecks        = ...,
        ForceColumnOrder             = ...,
        UseCaseSensitiveObjectDefinition = ...,
        // ... (see §11 for full option table)
    }

    // Schema object type discriminator
    public enum ObjectType
    {
        Table, View, StoredProcedure, Function, Trigger,
        Schema, Role, User, Sequence, Synonym,
        Assembly, Certificate, AsymmetricKey, SymmetricKey,
        FullTextCatalog, PartitionFunction, PartitionScheme,
        ExtendedProperty, XmlSchemaCollection,
        // ... (all types listed in §3.10)
    }

    // Category of change for a Difference
    public enum DifferenceType
    {
        Equal,
        OnlyIn1,      // exists in source only
        OnlyIn2,      // exists in target only
        Different     // exists in both but definition differs
    }
}
```

### 10.2 Canonical Workflow — Register, Compare, Script, Execute

```csharp
using RedGate.SQLCompare.Engine;
using RedGate.Shared.SQL;
using System;
using System.Linq;

public static class SchemaDeployer
{
    public static void DeploySchema(
        string sourceServer, string sourceDb,
        string targetServer, string targetDb,
        string userName, string password,
        string outputScriptPath)
    {
        // 1 — Define connection properties
        var sourceConn = new ConnectionProperties(sourceServer, sourceDb, userName, password);
        var targetConn = new ConnectionProperties(targetServer, targetDb, userName, password);

        // 2 — Build options: start from defaults, add IgnoreComments
        var opts = Options.Default | Options.IgnoreComments;

        // 3 — Register both sides
        using var source = new Database();
        using var target = new Database();

        source.Register(sourceConn, opts);
        target.Register(targetConn, opts);

        // 4 — Compare
        Differences diff = source.CompareWith(target, opts);

        if (!diff.Any())
        {
            Console.WriteLine("Databases are identical — nothing to deploy.");
            return;
        }

        // 5 — Select all differences for deployment
        foreach (Difference d in diff)
            d.Selected = true;

        // 6 — Build the work (migration script)
        using var work = new Work();
        work.BuildFromDifferences(diff, opts, runtimeOnly: false);

        // 7 — Write script to file for audit
        string sql = work.ScriptDifferences();
        System.IO.File.WriteAllText(outputScriptPath, sql, System.Text.Encoding.UTF8);
        Console.WriteLine($"Script written to: {outputScriptPath}");

        // 8 — Execute against target
        using ExecutionBlock block = work.ExecutionBlock;
        block.Execute(targetConn);

        Console.WriteLine("Deployment complete.");
    }
}
```

---

## 11. SDK — Comparison Options

The `Options` flags enum mirrors the CLI `/options` switch. Options with a default-on
designation are included in `Options.Default`. Compose options using bitwise OR:

```csharp
var opts = Options.Default | Options.IgnoreComments | Options.IgnoreCollations;
// To remove a default-on option:
var opts = (Options.Default & ~Options.IgnoreWhiteSpace) | Options.IgnoreCollations;
```

### Full Option Reference

| Option Name | CLI Token | Default | Description |
|---|---|---|---|
| `IgnoreWhiteSpace` | `iw` | **On** | Ignore whitespace differences in object definitions |
| `IgnoreFillFactor` | `if` | **On** | Ignore fill factor and index pad settings |
| `IgnoreFileGroups` | (see `IgnoreFileGroupsPartition...`) | **On** | Ignore filegroup, partition scheme, and partition function clauses |
| `IgnoreUserProperties` | `iup` | **On** | Ignore user properties; compare names only |
| `IgnoreWithElementOrder` | `iweo` | **On** | Ignore order of WITH clause elements |
| `IgnoreDatabaseAndServerName` | `idsn` | **On** | Ignore server/db names in synonym definitions |
| `IncludeDependencies` | `incd` | **On** | Include dependent objects in comparison and deployment |
| `DecryptPost2KEncryptedObjects` | — | **On** | Decrypt objects created WITH ENCRYPTION |
| `AddDatabaseUseStatement` | `adus` | Off | Add USE statement to top of deployment script |
| `DropAndCreateForReRunnableScripts` | `dac` | Off | Replace ALTER with DROP/CREATE for views, procs, functions, triggers |
| `CreateOrAlterForReRunnableScripts` | `coa` | Off | Change ALTER to CREATE OR ALTER |
| `AddNoPopulation` | `anp` | Off | Add NO POPULATION clause to new full-text indexes |
| `ObjectExistenceChecks` | `oec` | Off | Add IF EXISTS guards to deployment scripts |
| `OnlineIndexBuild` | `oib` | Off | Add ONLINE = ON to new relational indexes |
| `AddWithEncryption` | `we` | Off | Add WITH ENCRYPTION to procs/functions/views/triggers |
| `NoAutoColumnMapping` | `nacm` | Off | Disable automatic column mapping by name similarity |
| `ForceColumnOrder` | `f` | Off | Rebuild tables when columns are inserted mid-table |
| `NoTransactions` | `nt` | Off | Remove transactions from deployment scripts |
| `NoErrorHandling` | `neh` | Off | Remove error handling from deployment scripts |
| `DoNotOutputCommentHeader` | `nc` | Off | Suppress comment header in deployment scripts |
| `DisableAndReenableDdlTriggers` | `drd` | Off | Disable DDL triggers before deployment, re-enable after |
| `NoDeploymentLogging` | `ndl` | Off | Disable SQL Monitor integration logging |
| `UseCaseSensitiveObjectDefinition` | `cs` | Off | Enable comparison of objects that differ only by case |
| `UseCompatibilityLevel` | `ucl` | Off | Use database compatibility level instead of server version |
| `IgnoreSchemaObjectAuthorization` | `isoa` | Off | Ignore authorization clauses on schema-qualified objects |
| `IgnoreBindings` | `ib` | Off | Ignore sp_bindrule / sp_bindefault clauses |
| `IgnoreCertificatesAndCryptoKeys` | `icc` | Off | Only deploy permissions for certs and crypto keys |
| `IgnoreChangeTracking` | `ict` | Off | Ignore change tracking settings |
| `IgnoreCheckConstraints` | `ich` | Off | Ignore check constraints |
| `IgnoreCollations` | `ic` | Off | Ignore collation differences on character columns |
| `IgnoreComments` | `icm` | Off | Ignore comments in views, procedures, etc. |
| `IgnoreConstraintAndIndexNames` | `icn` | Off | Ignore constraint and index naming differences |
| `IgnoreDataCompression` | `idc` | Off | Ignore page and row compression settings |
| `IgnoreTriggerOrder` | `ito` | Off | Ignore DML trigger execution order |
| `IgnoreTriggers` | `it` | Off | Ignore all DML triggers |
| `IgnoreDynamicDataMasking` | `iddm` | Off | Ignore MASKED clauses on columns |
| `IgnoreExtendedProperties` | `ie` | Off | Ignore extended properties |
| `IgnoreForeignKeys` | `ifk` | Off | Ignore foreign key constraints |
| `IgnoreFullTextIndexing` | `ift` | Off | Ignore full-text catalogs and indexes |
| `IgnoreIdentityPropertiesOnColumns` | `iip` | Off | Ignore identity property designation |
| `IgnoreIdentitySeedAndIncrementValues` | `isi` | Off | Ignore identity seed and increment values only |
| `IgnoreIndexes` | `ii` | Off | Ignore all indexes and primary/unique key constraints |
| `IgnoreInsteadOfTriggers` | `iit` | Off | Ignore INSTEAD OF triggers |
| `IgnoreLockPropertiesOfIndexes` | `ilpi` | Off | Ignore PAGE LOCK and ROW LOCK on indexes |
| `IgnoreMigrationScripts` | `ims` | Off | Exclude migration scripts from comparison |
| `IgnoreNocheckAndWithNocheck` | `inwn` | Off | Ignore NOCHECK and WITH NOCHECK arguments |
| `IgnoreNotForReplication` | `infr` | Off | Ignore NOT FOR REPLICATION on constraints/triggers |
| `IgnoreNullability` | `in` | Off | Ignore column nullability differences |
| `IgnorePerformanceIndexes` | `ipi` | Off | Ignore indexes except primary/unique keys |
| `IgnorePermissions` | `ip` | Off | Ignore object-level permissions |
| `IgnoreReplicationTriggers` | `irpt` | Off | Ignore replication-specific triggers |
| `IgnoreQuotedIdentifiersAndAnsiNullSettings` | `iq` | Off | Ignore SET QUOTED_IDENTIFIER and SET ANSI_NULLS |
| `IgnoreSensitivityClassification` | `isc` | Off | Ignore column sensitivity classifications |
| `IgnoreStatisticsIncremental` | `isinc` | Off | Ignore Statistics_Incremental property |
| `IgnoreSquareBrackets` | `isb` | Off | Ignore square bracket escaping |
| `IgnoreStatistics` | `ist` | Off | Ignore statistics |
| `IgnoreStatisticsNorecompute` | `isn` | Off | Ignore STATISTICS_NORECOMPUTE property |
| `IgnoreSystemNamedConstraintAndIndexNames` | `iscn` | Off | Ignore system-generated constraint/index names only |
| `IgnoretSQLt` | `itst` | Off | Ignore tSQLt framework, tests, and related schemas |
| `IgnoreUsersPermissionsAndRoleMemberships` | `iu` | Off | Ignore user permissions and role memberships |
| `IgnoreWithEncryption` | `iwe` | Off | Ignore WITH ENCRYPTION statements |
| `IgnoreWithNocheck` | `iwn` | Off | Ignore WITH NOCHECK on constraints |

Cross-reference with `data-models.md` §OptionFlags for the internal bitmask representation
used by our clone's configuration store.

---

## 12. SDK — Streaming and Eventing

### 12.1 Progress Callbacks

The SDK raises events during long-running operations. Subscribe before calling `Register`
or `CompareWith`:

```csharp
source.OnTableDataReceived += (sender, args) =>
{
    Console.WriteLine($"Received data for table: {args.TableName}");
};

source.OnProgress += (sender, args) =>
{
    Console.Write($"\r{args.Message} {args.Percent:F0}%");
};
```

### 12.2 Cancellation

The SDK does not expose `CancellationToken` directly in older versions. For cooperative
cancellation in a hosted service, run the operation in a background thread and track a
shared `CancellationToken`; abort by calling `Thread.Abort()` (Framework) or by
disposing the `Database` objects, which causes the operation to fail with an
`ObjectDisposedException` that you catch and translate.

In SDK 10+ a `CancellationToken` overload was added to `Register`:

```csharp
CancellationToken ct = cts.Token;
await Task.Run(() => source.Register(connection, options, ct), ct);
```

### 12.3 Log Subscribers

Redirect SDK log output by subscribing to the static log sink:

```csharp
RedGate.SQLCompare.Engine.Log.AddListener(new ConsoleLogListener());

public class ConsoleLogListener : ILogListener
{
    public void Log(LogEntry entry)
    {
        Console.WriteLine($"[{entry.Level}] {entry.Message}");
    }
}
```

---

## 13. SDK — Embedding Examples

### 13.1 Example A — Database to Database (Full Deploy)

```csharp
using RedGate.SQLCompare.Engine;

public class DbToDbDeployer
{
    public void Deploy(string srcServer, string srcDb,
                       string tgtServer, string tgtDb,
                       string user, string pass)
    {
        var srcConn = new ConnectionProperties(srcServer, srcDb, user, pass);
        var tgtConn = new ConnectionProperties(tgtServer, tgtDb, user, pass);
        var opts    = Options.Default | Options.IgnoreComments;

        using var src = new Database();
        using var tgt = new Database();

        src.Register(srcConn, opts);
        tgt.Register(tgtConn, opts);

        Differences diffs = src.CompareWith(tgt, opts);
        if (!diffs.Any()) return;

        foreach (var d in diffs) d.Selected = true;

        using var work = new Work();
        work.BuildFromDifferences(diffs, opts, runtimeOnly: false);

        using var block = work.ExecutionBlock;
        block.Execute(tgtConn);
    }
}
```

### 13.2 Example B — Snapshot Diff (Change Auditing)

```csharp
using RedGate.SQLCompare.Engine;
using System.IO;
using System.Linq;

public class SnapshotAuditor
{
    public string GenerateDriftReport(
        string baselineSnapshotPath,
        string currentSnapshotPath)
    {
        var opts = Options.Default;

        using var baseline = new Database();
        using var current  = new Database();

        baseline.Register(baselineSnapshotPath, opts);
        current.Register(currentSnapshotPath, opts);

        Differences diffs = baseline.CompareWith(current, opts);

        if (!diffs.Any())
            return "No schema drift detected.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SCHEMA DRIFT REPORT");
        sb.AppendLine(new string('-', 60));

        foreach (var diff in diffs.OrderBy(d => d.Type.ToString()).ThenBy(d => d.Name))
        {
            sb.AppendLine($"  [{diff.DifferenceType,-10}]  {diff.Type,-20}  {diff.Name}");
        }

        return sb.ToString();
    }
}
```

### 13.3 Example C — Scripts Folder to Database (State-Based Deploy)

```csharp
using RedGate.SQLCompare.Engine;
using System.IO;

public class ScriptsFolderDeployer
{
    public void DeployFromScriptsFolder(
        DirectoryInfo scriptsFolder,
        string targetServer, string targetDb,
        string user, string pass,
        string outputScriptFile)
    {
        var tgtConn = new ConnectionProperties(targetServer, targetDb, user, pass);
        var opts    = Options.Default | Options.IgnoreWhiteSpace;

        using var src = new Database();
        using var tgt = new Database();

        // Register source as scripts folder
        src.RegisterForScriptsFolder(scriptsFolder, opts);

        // Register target as live database
        tgt.Register(tgtConn, opts);

        Differences diffs = src.CompareWith(tgt, opts);
        if (!diffs.Any())
        {
            Console.WriteLine("Target is already up to date.");
            return;
        }

        foreach (var d in diffs) d.Selected = true;

        using var work = new Work();
        work.BuildFromDifferences(diffs, opts, runtimeOnly: false);

        // Write the script to a file before executing
        string sql = work.ScriptDifferences();
        File.WriteAllText(outputScriptFile, sql, System.Text.Encoding.UTF8);

        // Apply
        using var block = work.ExecutionBlock;
        block.Execute(tgtConn);

        Console.WriteLine($"Deployment complete. Script saved to: {outputScriptFile}");
    }
}
```

---

## 14. SDK — Errors and Exceptions

| Exception Type | When Thrown | Recovery |
|---|---|---|
| `RedGate.SQLCompare.Engine.SqlException` | SQL Server errors during registration or execution (connection failure, permission denied, T-SQL error) | Check connection string; verify service account rights; inspect `SqlException.InnerException` |
| `RedGate.SQLCompare.Engine.CompareException` | Comparison engine failure (corrupt objects, unsupported features) | Check `Message` for object name; use `IgnoreParserErrors` equivalent option; file a bug if unexpected |
| `RedGate.SQLCompare.Engine.LicenseException` | SDK license not found or expired | Verify license key is activated; check license file location |
| `System.IO.IOException` | Script file, snapshot, or scripts folder I/O error | Verify path and write permissions; use `/force` equivalent |
| `System.ArgumentNullException` | Null connection properties or null options | Validate inputs before calling `Register` |
| `System.InvalidOperationException` | Calling `CompareWith` before `Register`; calling `BuildFromDifferences` on an empty diff set | Check call order; guard with `diffs.Any()` check |
| `System.OperationCanceledException` | Cancellation token was triggered (SDK 10+) | Clean up `using` blocks; propagate cancellation to caller |

### Defensive Wrapper Pattern

```csharp
public static Result<Differences> SafeCompare(
    Database source, Database target, Options opts)
{
    try
    {
        return Result.Ok(source.CompareWith(target, opts));
    }
    catch (SqlException ex)
    {
        return Result.Fail($"SQL error during comparison: {ex.Message}");
    }
    catch (CompareException ex)
    {
        return Result.Fail($"Engine error during comparison: {ex.Message}");
    }
    catch (LicenseException ex)
    {
        return Result.Fail($"License error: {ex.Message}. Verify SDK license.");
    }
}
```

---

## 15. API Stability and Versioning

### 15.1 Redgate's Policy (Observed)

Redgate follows a broadly semver-compatible approach for the SDK:

- **Minor versions** (e.g., 11.x → 11.y): backward-compatible additions. Existing calling
  code continues to compile and run without modification.
- **Major versions** (e.g., 10 → 11): may include breaking changes. Redgate documents these
  in a "Breaking changes" page per major SDK version. Review that page before upgrading.
- **Deprecated symbols**: marked `[Obsolete]` in the assembly with a replacement noted in the
  message. Deprecation warnings appear at compile time; deprecated members are removed in the
  next major version.

The CLI switches follow a similar pattern: deprecated switches (e.g., `/AllowIdenticalDatabases`,
`/IncludeIdentical`) are silently accepted but redirect to their modern equivalents; they are
not removed until the next major version.

### 15.2 Recommended Policy for Our Clone

For the clone's internal API surface:

| Rule | Rationale |
|---|---|
| Version the comparison engine as `{major}.{minor}.{patch}` | Communicates break risk clearly |
| `[Obsolete]` all deprecated options for one major version before removal | Gives callers one release cycle to migrate |
| Never change the exit code contract without a major version bump | Callers test `$LASTEXITCODE`; silent changes break pipelines |
| Publish a `BREAKING_CHANGES.md` alongside every major release | Mirrors Redgate's documented pattern |
| Keep CLI switch naming stable across minor versions | CI/CD scripts are hard to mass-update |

Cross-reference: `core-modules.md` §Deployment Executor for the internal versioning scheme
of the engine module.

---

## 16. Comparison vs. SQL Change Automation

### 16.1 Boundary Definition

| Concern | SQL Compare (this clone) | SQL Change Automation / Redgate Deploy |
|---|---|---|
| Scope | Single comparison: one source + one target | Multi-stage pipeline: baseline → test → staging → production |
| Input | Database, scripts folder, snapshot, backup | Git branch, `sqlproj`, migration folder |
| Output | Diff report, migration script, synchronized DB | Release artifact, approval workflow, audit trail |
| Ordering | None (single step) | Ordered migrations with version tracking |
| State | Stateless per invocation | Tracks applied migrations in a `__MigrationLog` table |
| Orchestration | None | `Invoke-DlmDatabaseSchemaValidation`, `New-DlmDatabaseRelease`, `Use-DlmDatabaseRelease` |

### 16.2 Design Rule for Our Clone

The clone's **Deployment Executor** (`core-modules.md` §4) handles a single comparison and
applies it. The clone does not implement:

- Migration version tracking
- Multi-stage release pipelines
- Approval workflows
- Rollback sequencing

These concerns belong to a separate orchestration layer that calls the clone's CLI or SDK.
If pipeline functionality is required, wrap the clone's CLI the same way SCA wraps
`sqlcompare.exe`.

### 16.3 Integration Point

An orchestration layer calls the clone at this boundary:

```
orchestrator → clone CLI (one invocation per comparison step)
            → reads exit code
            → reads generated script artifact
            → proceeds or halts pipeline
```

The clone need not know about pipeline position, release version, or approval state.

---

*Cross-references:*
- `data-models.md` — option flag bitmask definitions, OptionFlags enum
- `core-modules.md` — Deployment Executor (§4), Comparison Engine (§2), Pipeline Orchestrator (§7)
