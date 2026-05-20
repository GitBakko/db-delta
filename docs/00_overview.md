# SQL Compare — Product Overview

> **Document version:** 1.0 (research baseline, May 2026)
> **Primary source:** Redgate SQL Compare 16 official documentation at `documentation.red-gate.com/sc`
> **Purpose:** Foundation reference for building a faithful clone of Redgate SQL Compare.

---

## Table of Contents

1. [Product Identity](#1-product-identity)
2. [Core Value Proposition](#2-core-value-proposition)
3. [Supported Sources and Targets — The Comparison Matrix](#3-supported-sources-and-targets--the-comparison-matrix)
4. [Feature Inventory](#4-feature-inventory)
   - 4.1 [Object Support](#41-object-support)
   - 4.2 [Comparison Options](#42-comparison-options)
   - 4.3 [Filters and Projects](#43-filters-and-projects)
   - 4.4 [Deployment Options](#44-deployment-options)
   - 4.5 [Snapshots](#45-snapshots)
   - 4.6 [Source Control Integration](#46-source-control-integration)
   - 4.7 [Migration Scripts](#47-migration-scripts)
   - 4.8 [Command-Line and Automation](#48-command-line-and-automation)
   - 4.9 [Reporting](#49-reporting)
5. [Primary User Workflows](#5-primary-user-workflows)
6. [Editions and Licensing](#6-editions-and-licensing)
7. [SQL Server Compatibility Matrix](#7-sql-server-compatibility-matrix)
8. [Integration Surface](#8-integration-surface)
9. [Non-Goals and Out-of-Scope Boundaries](#9-non-goals-and-out-of-scope-boundaries)
10. [Glossary](#10-glossary)

---

## 1. Product Identity

### What SQL Compare Is

Redgate SQL Compare is the industry-standard schema comparison and deployment tool for Microsoft SQL Server. First released in the early 2000s, it has reached version 16 as of late 2025. The tool answers a single core question: **"How does the structure of database A differ from database B, and how do I make them the same?"**

SQL Compare compares the Data Definition Language (DDL) of two data sources, presents the differences in a structured UI with side-by-side T-SQL diffs, and generates a syntactically correct, dependency-ordered deployment script that transforms the target into a structural copy of the source.

### Problem Being Solved

Database schema management has historically been a manual, error-prone discipline:

- DBAs hand-write `ALTER TABLE` or `CREATE INDEX` scripts that may be incomplete.
- Scripts are applied in the wrong order, breaking foreign key dependencies.
- Drift accumulates between environments (dev, test, staging, prod) because schema changes are applied inconsistently.
- Rollback from a bad deployment requires knowing exactly what changed.
- Source-controlling a database schema is non-trivial without tooling.

SQL Compare automates all of these tasks. It eliminates the "works on my machine" database problem by making schema state explicit, comparable, and deployable.

### Target Users

| User Type | Primary Use |
|-----------|-------------|
| **Database Administrators (DBAs)** | Audit production schema drift, validate deployments, rollback recovery via snapshots |
| **Database Developers** | Synchronize personal dev DB with team standards; generate migration scripts |
| **DevOps / Release Engineers** | Automate schema deployment in CI/CD pipelines via CLI |
| **Application Developers** | Bring local DB schema up to date after pulling new source code |
| **QA / Test Engineers** | Verify test environment matches production schema |
| **Enterprise Architects** | Enforce governance; document schema differences for compliance |

### Market Positioning

SQL Compare is Redgate's flagship SQL Server developer tool. According to Redgate's marketing, 71% of Fortune 100 companies use it, including AstraZeneca, Google, FedEx, Pepsi, IBM, and Fujitsu. It is the de facto standard for SQL Server schema comparison in the Windows/.NET enterprise ecosystem. Competitors include:

- **ApexSQL Diff** (ApexSQL)
- **dbForge Schema Compare for SQL Server** (Devart)
- **SQL Server Data Tools (SSDT) Schema Compare** (Microsoft, built into Visual Studio — free but less featureful)
- **Liquibase / Flyway** (open-source, migration-script approach rather than state-based comparison)

SQL Compare's differentiation is its depth of object support, comparison options granularity, SSMS integration, snapshot mechanism, and the breadth of its source-type matrix (live DB, backup, scripts folder, snapshot, source control).

---

## 2. Core Value Proposition

SQL Compare delivers value across five pillars:

### Pillar 1: Schema Comparison

Compare any two supported data sources and immediately see which objects differ, which are missing from one side, and which are identical. The comparison is object-level (each table, view, stored procedure is a row in the results grid) with line-level T-SQL diff within each object.

### Pillar 2: Intelligent Deployment Script Generation

Generate a single T-SQL deployment script that:
- Applies changes in correct dependency order (so foreign keys are not created before their referenced tables)
- Handles drop/re-create scenarios where ALTER is insufficient (e.g., adding a column with a constraint)
- Wraps everything in a transaction with error handling by default
- Optionally checks for object existence before altering or dropping

### Pillar 3: Multiple Source Types

Not just live database to live database. SQL Compare can compare databases, script folders checked into source control, native SQL Server backup files (without restoring them), and binary snapshot files. This enables offline comparisons, audits, and disaster-recovery scenarios.

### Pillar 4: Automation and CI/CD

The full UI feature set is accessible from the `SQLCompare.exe` command line. Teams can embed schema comparison and deployment into build pipelines (Azure DevOps, Jenkins, TeamCity, GitHub Actions via script) using project files and XML argument files for repeatability.

### Pillar 5: Safety and Review

Before any deployment runs, SQL Compare shows:
- A summary of all objects being changed, added, or dropped
- Warnings about potentially dangerous operations (e.g., dropping a table)
- The full deployment script for DBA review
- Dependency analysis showing what else is affected

---

## 3. Supported Sources and Targets — The Comparison Matrix

SQL Compare supports six distinct source/target types. Any combination of these can be compared (with the caveats noted below).

### Source/Target Types

| Type | Description | Key Characteristics |
|------|-------------|---------------------|
| **Live Database** | A running SQL Server instance (on-prem, Azure SQL DB, Amazon RDS) | Full feature support; requires network connection and credentials |
| **Scripts Folder** | A directory of `.sql` files organized by object type | One file per object; folder structure mirrors the object hierarchy |
| **Snapshot (.snp)** | Binary file capturing schema at a point in time | Read-only; schema only, no data; portable across machines |
| **Native Backup (.bak / .sqb)** | SQL Server native backup file | No restore needed; Professional Edition only; some feature limits |
| **Source Control Project** | Scripts folder linked to a VCS (Git, SVN, TFS) via SQL Source Control | Accessed via the SSMS add-in; requires SQL Source Control license |
| **SQL Clone** | Virtual copy of a database created by Redgate SQL Clone | Behaves exactly like a live database; no special handling needed |

### Comparison Matrix (All Valid Combinations)

| Source →  Target ↓ | Live DB | Scripts Folder | Snapshot | Backup | Source Control | SQL Clone |
|---------------------|---------|----------------|----------|--------|----------------|-----------|
| **Live DB**         | Yes     | Yes            | Yes*     | Yes**  | Yes            | Yes       |
| **Scripts Folder**  | Yes     | Yes            | Yes*     | Yes**  | Yes            | Yes       |
| **Snapshot**        | Yes*    | Yes*           | Yes*     | Yes*   | Yes*           | Yes*      |
| **Backup**          | Yes**   | Yes**          | Yes*/**  | Yes**  | Yes**          | Yes**     |
| **SQL Clone**       | Yes     | Yes            | Yes*     | Yes**  | Yes            | Yes       |

> **Note:** `*` = When a Snapshot is the **target**, deployment generates a script targeting the originating database (the snapshot itself cannot be modified in place). `**` = Backup as source or target requires Professional Edition; encrypted and natively-compressed backups are not supported.

### Directionality

Comparison is always **directional**: Source → Target. The deployment script makes the target match the source. The direction can be reversed in the UI with a single button click before generating the deployment script. The CLI uses `/Database1` (or `/Scripts1`, `/Snapshot1`, `/Backup1`) as the conceptual source and `/Database2` as target; `/Synchronize` deploys source-to-target.

### Source-Specific Caveats

**Scripts Folder:**
- SQL Compare creates and maintains the folder hierarchy (one subfolder per object type)
- Files are named `ObjectName.sql` (or `SchemaName.ObjectName.sql` for multi-schema databases)
- When an object is dropped during deployment *to a scripts folder*, its file is **not** automatically deleted from disk
- Parsing can fail on malformed SQL; `/IgnoreParserErrors` or the `ThrowOnFileParseFailed` option controls behavior

**Native Backup:**
- SQL Compare reads the backup file directly without restoring it to SQL Server
- Unsupported: natively compressed backups, encrypted backups
- Unsupported object types in backups: file tables, memory-optimized tables, temporal tables, sequences (SQL Server 2012+)
- Supports both `.bak` (SQL Server native) and `.sqb` (SQL Backup Pro format)

**Snapshot:**
- Schema-only; contains no row data
- Immutable once created (read-only binary)
- Can be created from: live database, backup, scripts folder, another snapshot
- Case sensitivity is auto-detected from the source when creating from a live DB; unavailable as a setting when source is already a snapshot
- Backwards compatible: snapshots from SQL Compare versions 3–7 can be read in version 16
- A companion utility, `RedGate.SQLSnapper.exe`, ships with SQL Compare for scripted snapshot creation

**Azure SQL Database (Live):**
- Supported for comparison and deployment
- Requires SQL Compare 12.4.9 or later for Azure Active Directory authentication
- Many object types are not supported by Azure SQL DB and will cause deployment failures if included:
  Application Roles, Assemblies, Asymmetric Keys, Certificates, Contracts, Defaults, Event Notifications, Full Text Catalogs, Message Types, Partition Functions, Partition Schemes, Queues, Routes, Rules, Services, Service Bindings, Extended Stored Procedures, Numbered Stored Procedures, Symmetric Keys, Remote Synonyms, System Tables, User-Defined Types, XML Schema Collections
- Azure SQL DB does not support encryption, data compression, or SQL Server replication

> **Caveat:** Azure SQL Managed Instance support is limited. For MI, Redgate recommends Flyway Enterprise rather than SQL Compare.

---

## 4. Feature Inventory

### 4.1 Object Support

SQL Compare can compare and deploy the following database object types. This table is the authoritative list for clone implementation.

| Object Type | Notes |
|-------------|-------|
| **Assembly** | CLR assemblies; `DontAlterAssembly` option avoids ALTER ASSEMBLY |
| **Asymmetric Key** | Comparison and deployment supported |
| **Certificate** | **Comparison only** — deployment of certificates has documented limitations (key material cannot be scripted) |
| **Contract** | Service Broker contract |
| **DDL Trigger** | Server/database-level DDL triggers (not DML triggers, which are part of Tables) |
| **Default** | Standalone default objects (legacy, pre-inline default) |
| **Extended Property** | Documentation metadata attached to any object; can be ignored via `IgnoreExtendedProperties` |
| **Event Notification** | Database-level event notifications; queue-level event notifications controlled separately |
| **External Data Source** | PolyBase external data sources |
| **External File Format** | PolyBase external file formats |
| **External Table** | PolyBase external tables |
| **Full Text Catalog** | Full-text search catalogs; ignoring controlled by `IgnoreFullTextIndexing` |
| **Full Text Stoplist** | Custom stopword lists for full-text search |
| **Function** | Scalar, inline table-valued, and multi-statement TVFs |
| **Message Type** | Service Broker message types |
| **Partition Function** | Controlled by `IgnoreFileGroupsPartitionSchemesAndPartitionFunctions` |
| **Partition Scheme** | Controlled by `IgnoreFileGroupsPartitionSchemesAndPartitionFunctions` |
| **Queue** | Service Broker queues |
| **Role** | Database roles (not server roles) |
| **Route** | Service Broker routes |
| **Rule** | Standalone rule objects (legacy) |
| **Schema** | User-defined schemas |
| **Search Property List** | Full-text search property lists (SQL Server 2012+) |
| **Security Policy** | Row-level security policies (SQL Server 2016+) |
| **Sequence** | Sequence objects (SQL Server 2012+); **not supported in backup comparisons** |
| **Service** | Service Broker services |
| **Service Binding** | Service Broker remote service bindings |
| **Stored Procedure** | Standard stored procedures; numbered and extended SPs not supported in all contexts |
| **Symmetric Key** | Symmetric encryption keys |
| **Synonym** | Local synonyms; remote synonyms excluded from Azure SQL deployments |
| **Table** | Full table structure including columns, data types, nullability, identity, defaults, computed columns, check constraints, primary keys, unique constraints, foreign keys, indexes (clustered, non-clustered, columnstore, XML, spatial), filegroup placement, compression, change tracking, data masking |
| **User** | Database users; `IgnoreUserProperties` compares names only |
| **User Defined Type** | Both alias types (`CREATE TYPE ... FROM`) and CLR UDTs |
| **View** | Standard and indexed (schema-bound) views |
| **XML Schema Collection** | XML type collections |

#### Objects NOT Supported

| Object Type | Reason / Notes |
|-------------|----------------|
| **File Tables** | Not supported (backup comparisons especially) |
| **Memory-Optimized Tables** | Not supported (backup comparisons especially) |
| **Temporal Tables** | Limited support; not supported in backup comparisons |
| **Server-Level Objects** | Logins, server roles, linked servers — SQL Compare is database-scoped, not server-scoped |
| **Data** | Row data is out of scope — use SQL Data Compare |
| **Agent Jobs** | Not a database schema object |
| **SSIS / SSRS / SSAS Objects** | Not SQL Server relational objects |

#### Table Sub-Objects (detail)

Tables are compared as composite objects. The following table sub-objects are compared as part of the table:

| Sub-Object | Comparison Notes |
|------------|-----------------|
| Columns (name, data type, ordinal position) | `ForceColumnOrder` option controls whether column reorder triggers a table rebuild |
| NULL / NOT NULL | `IgnoreNullability` option |
| IDENTITY property | `IgnoreIdentityPropertiesOnColumns` and `IgnoreIdentitySeedAndIncrementValues` options |
| DEFAULT constraints | Inline defaults tracked; system-named defaults affected by `IgnoreSystemNamedConstraintAndIndexNames` |
| CHECK constraints | `IgnoreCheckConstraints` option |
| PRIMARY KEY constraints | `IgnoreIndexes` option |
| UNIQUE constraints | `IgnoreIndexes` option |
| FOREIGN KEY constraints | `IgnoreForeignKeys` option |
| Non-clustered indexes | `IgnoreIndexes`, `IgnoreFillFactor`, `IgnoreLockPropertiesOfIndexes` options |
| Clustered indexes | Part of table structure; same options apply |
| Columnstore indexes | Compared as part of table indexes |
| FILEGROUP placement | `IgnoreFileGroupsPartitionSchemesAndPartitionFunctions` option |
| Data compression (page/row) | `IgnoreDataCompression` option |
| DML Triggers | `IgnoreTriggers`, `IgnoreTriggerOrder`, `IgnoreInsteadOfTriggers`, `IgnoreReplicationTriggers` options |
| Change Tracking | `IgnoreChangeTracking` option |
| Dynamic Data Masking | `IgnoreDynamicDataMasking` option |
| Sensitivity Classification | `IgnoreSensitivityClassification` option |
| Temporal table history settings | Temporal table history retention policies |

---

### 4.2 Comparison Options

SQL Compare exposes a rich set of named comparison options. Options can be combined freely. The full list, including their CLI identifiers, follows.

#### Default-On Options (applied unless explicitly removed)

These options are active out of the box:

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `IgnoreWhiteSpace` | `iw` | Ignores whitespace differences (newlines, tabs, spaces) in object bodies |
| `IgnoreDatabaseAndServerName` | `idsn` | Ignores database and server name differences in synonym targets |
| `IgnoreUserProperties` | `iup` | Compares only user names, not their properties |
| `IgnoreWithElementOrder` | `iweo` | Ignores the order of elements in WITH clauses |
| `IgnoreFillFactor` | `if` | Ignores fill factor and padding settings on indexes |
| `IgnoreFileGroups` | `ifg` | Ignores filegroup/partition scheme/partition function differences |
| `IncludeDependencies` | `incd` | Includes dependent objects automatically when comparing and deploying |
| `DecryptPost2KEncryptedObjects` | — | Decrypts encrypted text objects in SQL Server 2008+ databases by default |

#### Mapping and Behavior Options

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `NoAutoColumnMapping` | `nacm` | Disables automatic column mapping; requires exact name match |
| `ForceColumnOrder` | `f` | Rebuilds tables to enforce correct column order when columns are reordered |
| `UseCompatibilityLevel` | `ucl` | Uses DB compatibility level instead of SQL Server version for comparisons |
| `UseCaseSensitiveObjectDefinition` | `cs` | Enables case-sensitive comparison for object names and stored procedure bodies |
| `ConsiderNextFilegroupInPartitionSchemes` | `cfgps` | Considers next filegroup in partition scheme comparisons |
| `DecryptEncryptedObjects` | `deo` | Decrypts WITH ENCRYPTION objects for comparison (requires sysadmin) |
| `ThrowOnFileParseFailed` | `tofpf` | Throws exception (and exits non-zero) when scripts folder parsing fails |

#### Deployment Script Generation Options

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `AddDatabaseUseStatement` | `adus` | Prepends `USE [DatabaseName]` to prevent execution against wrong DB |
| `ObjectExistenceChecks` | `oec` | Adds `IF EXISTS` / `IF NOT EXISTS` guards around DDL statements |
| `DropAndCreateForReRunnableScripts` | `dac` | Converts `ALTER` to `DROP` + `CREATE` for views, procs, functions, triggers |
| `CreateOrAlterForReRunnableScripts` | `coa` | Converts `ALTER` to `CREATE OR ALTER` (SQL Server 2016+) |
| `AddNoPopulation` | `anp` | Adds `NO POPULATION` clause to full-text index creation |
| `OnlineIndexBuild` | `oib` | Adds `ONLINE = ON` when creating indexes |
| `AddWithEncryption` | `we` | Adds `WITH ENCRYPTION` to all routines in the deployment script |
| `DoNotOutputCommentHeader` | `nc` | Suppresses the generated comment header block in deployment scripts |
| `DontAlterAssembly` | `daa` | Avoids generating `ALTER ASSEMBLY` statements for CLR objects |
| `NoTransactions` | `nt` | Removes transaction wrappers from deployment scripts |
| `NoErrorHandling` | `neh` | Removes error-handling statements (useful for debugging) |
| `NoDeploymentLogging` | `ndl` | Disables SQL Monitor integration deployment logging |
| `DisableAndReenableDdlTriggers` | `drd` | Wraps deployment in DDL trigger disable/enable to prevent unwanted firing |

#### Ignore — Schema and Structure

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `IgnoreBindings` | `ib` | Ignores rule and default bindings |
| `IgnoreCertificatesAndCryptoKeys` | `icc` | Ignores certificates, asymmetric keys, symmetric keys |
| `IgnoreChangeTracking` | `ict` | Ignores change tracking settings on tables |
| `IgnoreCheckConstraints` | `ich` | Excludes CHECK constraints from comparison |
| `IgnoreCollations` | `ic` | Disregards collation differences on character columns |
| `IgnoreConstraintAndIndexNames` | `icn` | Ignores manually-assigned constraint/index name differences |
| `IgnoreDataCompression` | `idc` | Ignores page and row compression settings |
| `IgnoreDatabaseAndServerNameInSynonyms` | `idsn` | Ignores DB/server name in synonym definitions |
| `IgnoreEventNotificationsOnQueues` | `iqen` | Ignores event notifications on Service Broker queues |
| `IgnoreExtendedProperties` | `ie` | Ignores extended property metadata |
| `IgnoreFileGroupsPartitionSchemesAndPartitionFunctions` | `ifg` | Ignores filegroup placement and partitioning |
| `IgnoreFillFactor` | `if` | Ignores index fill factor |
| `IgnoreForeignKeys` | `ifk` | Excludes foreign keys from comparison |
| `IgnoreFullTextIndexing` | `ift` | Ignores full-text catalogs, indexes, and stoplists |
| `IgnoreIndexes` | `ii` | Ignores indexes, unique constraints, and primary keys |
| `IgnoreSchemaObjectAuthorization` | `isoa` | Ignores AUTHORIZATION on schema objects |
| `IgnoreSystemNamedConstraintAndIndexNames` | `iscn` | Ignores system-generated (auto-named) constraint/index names |

#### Ignore — Column Properties

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `IgnoreIdentityPropertiesOnColumns` | `iip` | Ignores the IDENTITY property entirely |
| `IgnoreIdentitySeedAndIncrementValues` | `isi` | Ignores IDENTITY seed/increment values only (preserves IDENTITY flag) |
| `IgnoreNullability` | `in` | Ignores NULL / NOT NULL differences on columns |

#### Ignore — Triggers and Constraints

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `IgnoreTriggers` | `it` | Excludes all DML triggers from comparison |
| `IgnoreInsteadOfTriggers` | `iit` | Ignores INSTEAD OF triggers specifically |
| `IgnoreReplicationTriggers` | `irpt` | Ignores replication-generated triggers |
| `IgnoreTriggerOrder` | `ito` | Ignores first/last trigger order settings |
| `IgnoreNocheckAndWithNocheck` | `inwn` | Ignores NOCHECK and WITH NOCHECK on foreign keys/constraints |
| `IgnoreNotForReplication` | `infr` | Ignores NOT FOR REPLICATION option |
| `IgnoreWithNocheck` | `iwn` | Ignores WITH NOCHECK on constraint activation |

#### Ignore — Performance and Metadata

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `IgnoreLockPropertiesOfIndexes` | `ilpi` | Ignores PAGE LOCK and ROW LOCK settings on indexes |
| `IgnorePerformanceIndexes` | `ipi` | Ignores non-key performance index features |
| `IgnoreStatistics` | `ist` | Excludes user-created statistics |
| `IgnoreStatisticsIncremental` | `isinc` | Ignores incremental statistics property |
| `IgnoreStatisticsNorecompute` | `isn` | Ignores STATISTICS_NORECOMPUTE property |

#### Ignore — Code and Object Metadata

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `IgnoreComments` | `icm` | Ignores inline comments in object bodies (comments preserved in deploy scripts) |
| `IgnoreDynamicDataMasking` | `iddm` | Ignores MASKED WITH clauses on columns |
| `IgnoreQuotedIdentifiersAndAnsiNullSettings` | `iq` | Ignores SET QUOTED_IDENTIFIER / SET ANSI_NULLS in object headers |
| `IgnoreSensitivityClassification` | `isc` | Ignores data sensitivity classifications |
| `IgnoreSquareBrackets` | `isb` | Ignores differences between `[Name]` and `Name` in SQL text |
| `IgnoreWithElementOrder` | `iweo` | Ignores ordering of WITH clause elements |
| `IgnoreWithEncryption` | `iwe` | Ignores WITH ENCRYPTION statements |
| `IgnoreWhiteSpace` | `iw` | Ignores all whitespace differences in module bodies |

#### Ignore — Security and Framework

| Option | CLI Short | Description |
|--------|-----------|-------------|
| `IgnoreMigrationScripts` | `ims` | Excludes migration script objects from comparison |
| `IgnorePermissions` | `ip` | Ignores object-level GRANT / DENY / REVOKE |
| `IgnoretSQLt` | `itst` | Ignores tSQLt unit testing framework objects |
| `IgnoreUserProperties` | `iup` | Compares only user names, not login mappings or default schema |
| `IgnoreUsersPermissionsAndRoleMemberships` | `iu` | Compares role structure only, ignores membership |

#### Option Syntax (CLI)

```bash
# Apply one option
sqlcompare /db1:Dev /db2:Prod /Options:IgnoreComments

# Apply multiple options
sqlcompare /db1:Dev /db2:Prod /Options:IgnoreComments,IgnoreWhiteSpace,IgnoreFillFactor

# Start from defaults and add more
sqlcompare /db1:Dev /db2:Prod /Options:Default,IgnoreForeignKeys

# No options (compare everything strictly)
sqlcompare /db1:Dev /db2:Prod /Options:None
```

---

### 4.3 Filters and Projects

#### Filter Files (.scpf)

A filter defines which objects are visible and eligible for deployment in a comparison. Objects excluded by a filter cannot be selected for deployment.

| Aspect | Detail |
|--------|--------|
| **File extension** | `.scpf` |
| **Default storage** | `%USERPROFILE%\Documents\SQL Compare\Filters\` |
| **Auto-discovery** | All `.scpf` files in the Filters directory appear automatically in the Filter dropdown |
| **Portability** | Can be copied between machines; shared across SQL Compare, SQL Source Control, DLM Dashboard, and SQL Change Automation |
| **Default filter** | "Nothing Excluded" — all objects visible |
| **Unsaved indicator** | An asterisk (*) appears next to the filter name when unsaved changes exist |

**Filter capabilities:**

| Filter Type | Description |
|-------------|-------------|
| Object type inclusion/exclusion | Toggle entire object type categories (e.g., exclude all Statistics) |
| Object name exact match | `@NAME = 'MyTable'` |
| Object name wildcard | `@NAME LIKE 'Temp%'` (percent wildcard; available SQL Compare 12+) |
| Object name NOT LIKE | `@NAME NOT LIKE 'legacy_%'` |
| Schema name filter | Filter by schema name property |
| Compound expressions | `(@NAME = 'IGNORE') OR (@NAME LIKE 'TEMP%')` |

**CLI usage:**
```bash
sqlcompare /db1:Dev /db2:Prod /filter:"C:\Filters\ExcludeLegacy.scpf"
```

**CLI object exclusion (inline):**
```bash
# Exclude a single stored procedure
sqlcompare /db1:Dev /db2:Prod /exclude:StoredProcedure:uspSearchCandidateResumes

# Exclude using regex pattern
sqlcompare /db1:Dev /db2:Prod /exclude:StoredProcedure:usp.*Temp
```

#### Project Files (.scp)

A project file captures the full state of a comparison configuration for reuse.

| Aspect | Detail |
|--------|--------|
| **File extension** | `.scp` |
| **Default storage** | `%USERPROFILE%\Documents\SQL Compare\SharedProjects\` |
| **Contents** | Data sources (source + target), comparison options, filter reference, deployment settings |
| **CLI usage** | `sqlcompare /project:"C:\Projects\WidgetDeploy.scp"` |
| **Creation** | File → Save Project in the UI |

> **Note:** Project files reference filter files by path. If a filter file is moved, the project's filter reference breaks. Use absolute paths or keep filters in the default directory.

#### XML Argument Files

For complex CLI invocations, all switches can be specified in an XML file:

```bash
sqlcompare /argfile:"C:\Args\production-deploy.xml"
```

The XML file encodes all switches as elements, enabling version-controlled automation configurations.

---

### 4.4 Deployment Options

#### Deployment Methods (UI)

The Deployment Wizard presents three methods:

| Method | Description |
|--------|-------------|
| **Create a deployment script** | Generate and save the T-SQL script; do not execute it |
| **Deploy using SQL Compare** | Execute the script directly against the target within the tool |
| **Update scripts folder** | Write changed object `.sql` files to the target scripts folder |

> **Note:** When "Deploy using SQL Compare" is selected, an option exists to also save a copy of the script before execution. This is strongly recommended for audit trails.

#### Transaction Handling

By default, all deployment scripts are wrapped in a transaction. If any statement fails, the entire deployment rolls back.

| Setting | Effect |
|---------|--------|
| Default (transaction on) | Failure → full rollback to pre-deployment state |
| `NoTransactions` option | No rollback on failure; changes up to the failure point are committed |
| `NoErrorHandling` option | No error handling at all; useful for debugging individual statements |

> **Caveat:** `NoTransactions` is dangerous in production. Use only when the deployment is known safe or when individual step tracking is needed.

#### Drop Behavior

- When an object exists in the target but not the source, SQL Compare generates a `DROP` statement.
- When deploying **to a scripts folder**, dropping an object removes it from the comparison result but **does not delete the `.sql` file** from disk. Files must be deleted manually or via version control cleanup.
- The deployment wizard shows a **Warnings** tab highlighting all `DROP` statements for DBA review.

#### Dependency Handling

The `IncludeDependencies` option (default: on) ensures that when you select an object for deployment, all objects it depends on are automatically included. For example, deploying a stored procedure that references a view will include the view in the deployment script.

#### Rollback Strategy

SQL Compare does not have a built-in "undo deployment" button. The canonical rollback approach is:

1. Create a snapshot of the target database **before** deployment.
2. If rollback is needed, open SQL Compare with the pre-deployment snapshot as the **source** and the (now modified) live database as the **target**.
3. Deploy source → target, which reverts all changes.

#### Backup Before Deploy

The CLI supports taking a backup before deployment:

```bash
# Default backup provider
sqlcompare /db1:Dev /db2:Prod /sync /makebackup

# Custom backup with SQL Backup Pro
sqlcompare /db1:Dev /db2:Prod /sync /makebackup /BackupProvider:SQB /BackupType:Differential /BackupFolder:C:\Backups
```

#### Always Encrypted / TDE Caveats

- **Transparent Data Encryption (TDE):** SQL Compare can compare schemas of TDE-protected databases since schema metadata is not encrypted. Deployment scripts work normally.
- **Always Encrypted:** Columns protected by Always Encrypted are visible in schema comparison (column exists, data type is visible), but re-keying or key management is outside SQL Compare's scope. Deployment that changes an Always Encrypted column may fail if column master/encryption keys are not accessible.

---

### 4.5 Snapshots

#### What a Snapshot Is

A snapshot (`.snp`) is an immutable binary file that captures the complete DDL structure of a database at a specific point in time. It contains schema metadata only — no row data. Once created, it cannot be modified.

| Property | Value |
|----------|-------|
| **Extension** | `.snp` |
| **Format** | Proprietary binary |
| **Contents** | Schema metadata (parser output); no data |
| **Mutability** | Read-only / immutable |
| **Compatibility** | SQL Compare versions 3–16 (older encrypted snapshots may have issues) |
| **Source for creation** | Live database, native backup, scripts folder, another snapshot |

#### Creating Snapshots

**UI method:** File → Create Snapshot → select source → configure options → Save.

**CLI method:**
```bash
# Create snapshot from live database
sqlcompare /Database1:WidgetProduction /Makesnapshot:"C:\Snapshots\WP_2024-01-15.snp"

# Create snapshot from backup
sqlcompare /Backup1:"D:\Backups\WidgetProd.bak" /Makesnapshot:"C:\Snapshots\WP_backup.snp"
```

**SQL Snapper (`RedGate.SQLSnapper.exe`):** A companion utility bundled in the SQL Compare installation directory. Enables creating snapshots from SQL Server databases in lightweight, scriptable fashion without launching the full SQL Compare UI.

#### Using Snapshots in Comparisons

```bash
# Compare live DB to snapshot (drift detection)
sqlcompare /Database1:WidgetProduction /Snapshot2:"C:\Snapshots\WP_baseline.snp"

# Compare two snapshots (historical diff)
sqlcompare /Snapshot1:"C:\Snapshots\WP_v1.snp" /Snapshot2:"C:\Snapshots\WP_v2.snp"

# Rollback: revert live DB to snapshot state
sqlcompare /Snapshot1:"C:\Snapshots\WP_before.snp" /Database2:WidgetProduction /sync
```

#### Snapshot Use Cases

| Use Case | Approach |
|----------|----------|
| **Baseline capture** | Snapshot production before every release |
| **Drift detection** | Compare scheduled snapshot against live DB; alert on differences |
| **Rollback** | Use pre-deployment snapshot as source; deploy to live DB |
| **Offline comparison** | Share snapshot files across disconnected networks; no DB access needed |
| **Historical audit** | Archive monthly snapshots; compare any two time points |
| **POC versioning** | Snapshot dev DB at each proof-of-concept milestone before committing to VCS |
| **Multi-environment audit** | Snapshot each environment (dev, test, prod); compare pairwise |

#### Snapshot as Target

When a snapshot is used as the **target** of a comparison, SQL Compare generates a deployment script targeting the **originating database** (the live DB from which the snapshot was taken). The snapshot itself cannot be written to — it is always read-only.

---

### 4.6 Source Control Integration

#### Scripts Folder as VCS Representation

SQL Compare's primary mechanism for source control integration is the **scripts folder**:

- Each database object is stored as an individual `.sql` file
- Folder structure: `<root>/<ObjectType>/<Schema>.<ObjectName>.sql`
- The folder can be placed in a Git, SVN, or TFS working copy
- Developers commit the `.sql` files via their normal VCS workflow
- SQL Compare reads/writes the folder; the VCS client manages versioning

**Round-trip workflow:**

```
Developer DB changes
    → SQL Compare: compare DB to scripts folder
    → Review diff in SQL Compare
    → Deploy (DB → scripts folder): updates .sql files
    → Developer: git commit updated .sql files
    → CI: git pull → SQL Compare: compare scripts folder to target DB → deploy
```

#### SQL Source Control (SSMS Add-in)

SQL Source Control is a separate Redgate product (SSMS add-in) that provides a GUI for the scripts-folder VCS workflow from within SSMS. When SQL Source Control is installed alongside SQL Compare, the SQL Compare SSMS add-in gains the ability to use "Source Control" as a comparison target — selecting a specific VCS revision or branch tip.

**Key capability:** Compare a live database against any historical revision or branch in source control, and generate the deployment script to move from the current DB state to that VCS revision.

#### Git-Specific Notes

- SQL Compare's scripts folder format is Git-friendly: one file per object, minimal merge conflicts when two developers change different objects
- Merge conflicts do occur when two developers modify the same object; conflict markers appear inside the `.sql` file body
- SQL Compare can parse and display conflicted files but cannot automatically resolve merges

#### TFS / Azure DevOps

- TFS workspace can be the scripts folder target
- SQL Compare can check in changed `.sql` files to TFS directly (via SQL Source Control integration)
- Command-line integration enables gated check-in builds

---

### 4.7 Migration Scripts

#### What Migration Scripts Solve

Schema comparison generates state-based deployment scripts (ALTER, CREATE, DROP). For certain changes — particularly those involving data — state-based scripts are insufficient:

- Splitting a column into two columns requires a data migration step
- Renaming a column (DROP old + CREATE new) loses data
- Populating a new NOT NULL column with initial values
- Reordering data before adding a constraint

Migration scripts allow teams to inject custom T-SQL at specific points in the deployment.

#### How Migration Scripts Work

Migration scripts are custom `.sql` files placed in reserved subdirectories of the scripts folder:

| Directory | Execution Timing |
|-----------|-----------------|
| `Pre-Deployment\` | Executed **before** the SQL Compare synchronization script |
| `Post-Deployment\` | Executed **after** the SQL Compare synchronization script |

**Behavior:** If a pre-deployment script creates an object (e.g., a staging table), SQL Compare excludes that object from its comparison — it will not try to drop/alter something the pre-deploy script just created.

**CLI option:** `IgnoreMigrationScripts` (`ims`) excludes migration scripts from consideration entirely.

#### Integration with SQL Source Control

SQL Source Control v4+ introduced native migration script authoring: developers write migration scripts directly in SSMS via the SQL Source Control pane, and those scripts are versioned alongside the schema scripts in VCS. When SQL Compare deploys, it detects applicable migration scripts and weaves them into the deployment sequence.

> **Note:** Migration scripts in SQL Compare are a simpler, manual concept compared to the versioned migration approach of Flyway or Liquibase. Teams requiring fully automated, numbered migrations should consider SQL Change Automation instead.

---

### 4.8 Command-Line and Automation

#### Executable

`SQLCompare.exe` — located in the SQL Compare installation directory (typically `C:\Program Files\Red Gate\SQL Compare 16\`).

#### Core Switches

| Switch | Description |
|--------|-------------|
| `/Server1:name` | SQL Server instance for source |
| `/Database1:name` | Database name for source |
| `/Server2:name` | SQL Server instance for target |
| `/Database2:name` | Database name for target |
| `/Scripts1:path` | Scripts folder as source |
| `/Scripts2:path` | Scripts folder as target |
| `/Snapshot1:path` | Snapshot file as source |
| `/Snapshot2:path` | Snapshot file as target |
| `/Backup1:path` | Backup file as source |
| `/Backup2:path` | Backup file as target |
| `/Synchronize` or `/sync` | Execute deployment (source → target) |
| `/Makesnapshot:path` | Create a snapshot from source instead of comparing |
| `/Makescripts:path` | Export source schema to a scripts folder |
| `/ScriptFile:path` | Save deployment script to file (do not execute) |
| `/Options:list` | Comma-separated comparison options |
| `/Filter:path` | Apply a `.scpf` filter file |
| `/Project:path` | Load a saved `.scp` project file |
| `/Report:path` | Generate a comparison report |
| `/ReportType:type` | Report format: `Interactive`, `Simple`, `XML`, `Excel` |
| `/Exclude:type:name` | Exclude specific object(s); name supports regex |
| `/Include:Identical` | Suppress exit code 63 when databases are identical |
| `/AssertIdentical` | Return exit code 79 if databases differ (useful in CI assertions) |
| `/AbortOnWarnings:level` | Control which warnings abort the run (`None`, `High`, `Medium`, `Low`) |
| `/IgnoreParserErrors` | Continue despite scripts folder parse errors |
| `/Force` | Overwrite existing output files |
| `/Quiet` or `/q` | Suppress progress output |
| `/LogLevel:level` | Enable logging |
| `/Out:path` | Write all output to a file |
| `/Verbose` or `/v` | Show detailed option information |
| `/MakeBackup` | Create a backup of target before deploying |
| `/BackupProvider:type` | Backup tool: `Native` (default) or `SQB` (SQL Backup Pro) |
| `/BackupFolder:path` | Destination for backup files |
| `/Username:name` | SQL authentication username |
| `/Password:pass` | SQL authentication password |
| `/UseWindowsAuthentication` | Use Windows authentication (default when no username given) |
| `/ActiveDirectory` | Use Azure Active Directory authentication |
| `/Argfile:path` | Load all switches from an XML argument file |

#### Exit Codes

All exit codes are documented; CI pipelines should check these:

| Code | Meaning |
|------|---------|
| `0` | Success — comparison/deployment completed |
| `1` | General / unspecified error |
| `3` | Illegal argument duplication |
| `8` | Unsatisfied argument dependency |
| `32` | Numeric value out of range |
| `33` | Value overflow |
| `34` | Invalid value |
| `35` | Invalid license / trial expired |
| `61` | Deployment warnings encountered |
| `62` | High-level scripts folder parse error |
| `63` | Databases are identical (no diff found) |
| `64` | Command-line usage error (bad flag or syntax) |
| `65` | Data error (invalid/corrupted input) |
| `69` | Resource unavailable |
| `70` | Unhandled exception (check logs) |
| `73` | Failed to create report |
| `74` | I/O error (file exists, `/Force` not specified) |
| `77` | Insufficient permissions |
| `79` | Databases not identical (when `/AssertIdentical` used) |
| `126` | SQL Server execution error |
| `130` | Ctrl-Break (user interrupted) |
| `400` | Bad request (mutually exclusive switches) |
| `402` | Not licensed |
| `499` | License activation cancelled |
| `500` | Unhandled exception |

> **Note:** Exit code `63` (identical databases) is frequently mishandled in CI pipelines. Use `/Include:Identical` to suppress it when "no diff" is a success condition.

#### Licensing for Automation

| Scenario | Required License |
|----------|-----------------|
| Interactive CLI on developer machine | SQL Compare license |
| CLI in CI/CD pipeline on a build server | Flyway Enterprise, Redgate Deploy, or SQL Toolbelt |
| CLI in scheduled automation (non-interactive) | Same as CI/CD |

> **Caveat:** Running `SQLCompare.exe` non-interactively on a server without the appropriate license will exit with code `402` or `35`. This is a hard gate on headless automation.

#### PowerShell Automation Patterns

**Pattern 1: DB to Scripts Folder (export schema to VCS)**
```powershell
$args = @(
    "/server1:$ServerInstance",
    "/database1:$Database",
    "/scripts2:$ScriptsFolderPath",
    "/q", "/sync",
    "/report:$ReportDir\$Database.html",
    "/reportType:Simple",
    "/rad", "/force"
)
& "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" $args
```

**Pattern 2: Scripts Folder to DB (deploy from VCS)**
```powershell
$args = @(
    "/scripts1:$ScriptsFolderPath",
    "/server2:$TargetServer",
    "/database2:$TargetDB",
    "/q", "/sync",
    "/scriptfile:C:\Migrations\$TargetDB-$(Get-Date -f yyyyMMdd).sql"
)
& "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" $args
```

**Pattern 3: Build Script (scripts folder to empty model DB)**
```powershell
$args = @(
    "/scripts1:$ScriptsFolderPath",
    "/server2:$BuildServer",
    "/database2:model",
    "/quiet",
    "/scriptfile:$BuildOutputDir\$Database.sql"
)
& "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" $args
```

#### Linux Support

SQL Compare 16 includes beta support for Linux command-line usage, enabling the CLI to run on Linux-based CI agents without a Windows host.

---

### 4.9 Reporting

SQL Compare can generate comparison reports in multiple formats.

| Format | CLI Value | Use Case |
|--------|-----------|----------|
| **Interactive HTML** | `Interactive` | Self-contained HTML file with collapsible diffs; best for sharing and local browsing |
| **Classic HTML** | `Html` | Simpler static HTML; good for web-based monitoring dashboards |
| **XML** | `Xml` | Machine-readable; use when building custom report processors |
| **Excel** | `Excel` | Spreadsheet format; best for email attachments and management review |

> **Note:** Excel reports do **not** include object creation scripts or line-level T-SQL diffs — only the list of differing objects and their change types.

**CLI report generation:**
```bash
sqlcompare /db1:Dev /db2:Prod /report:"C:\Reports\schema-diff.html" /reportType:Interactive
```

---

## 5. Primary User Workflows

### Workflow 1: Dev → Test Deploy

**Preconditions:**
- Developer has made schema changes to a local development database
- A test environment with a partially outdated schema exists
- Developer has SQL Compare installed on their machine with access to both environments

**Steps:**

1. **Open SQL Compare.** Launch the application from Start Menu or SSMS add-in.
2. **Create a new comparison.** In the New Comparison dialog:
   - Source: select the development SQL Server instance and database
   - Target: select the test SQL Server instance and database
   - Apply any relevant project file or filter (e.g., exclude test-only objects)
3. **Click Compare.** SQL Compare connects to both databases and builds the object list.
4. **Review results.** The results grid shows three groups:
   - Objects different between source and target (highlighted)
   - Objects only in source (missing from target)
   - Objects only in target (orphaned in test)
5. **Select objects for deployment.** Check the objects to include. By default all differences are selected. Uncheck objects that should not be deployed (e.g., test-only stored procedures that exist only in test).
6. **Click Deployment Wizard.** Select "Deploy using SQL Compare."
7. **Review the Warnings tab.** Check for DROP statements or potentially data-affecting changes.
8. **Click Deploy.** SQL Compare executes the deployment script against the test database.
9. **Verify.** Compare the two databases again; result should be "Databases are identical."

**Outcome:** Test database schema matches development.

**Edge cases:**
- If test DB has tables with data and a column type change is required, the deployment may fail mid-script (if not wrapped in a transaction, partial changes persist).
- If a new NOT NULL column without a default is added, SQL Compare generates the deployment with a default or flags a warning — verify the warning before deploying against populated tables.

---

### Workflow 2: Test → Prod Deploy with Review

**Preconditions:**
- Test environment has passed QA sign-off
- Production deployment requires DBA review of the deployment script before execution
- Production access is restricted

**Steps:**

1. **Generate deployment script (not execute).** Run SQL Compare comparing test (source) to prod (target). In the Deployment Wizard, choose "Create a deployment script."
2. **Save the script** to a shared location (e.g., release artifact store, SharePoint, email).
3. **DBA reviews the script.** DBA checks:
   - All `DROP` statements are intentional
   - No data-destructive operations (e.g., `ALTER COLUMN` on a populated table without migration)
   - `USE [ProdDatabase]` statement is correct
   - Transaction wrapper is present
4. **Create a pre-deployment snapshot of production:**
   ```bash
   sqlcompare /Database1:ProdServer\Prod /Makesnapshot:"\\fileserver\snapshots\Prod_before_v1.2.snp"
   ```
5. **Execute the reviewed script** against production using SSMS or the DBA's preferred tool.
6. **Post-deployment comparison.** Run SQL Compare again: test vs prod. Result should be identical.
7. **Archive the snapshot** for rollback capability.

**Outcome:** Production schema updated with full audit trail and rollback capability.

**Edge cases:**
- Time gap between script generation and execution means someone else may have changed prod in the interim. Run a "sanity comparison" immediately before execution.
- If rollback is needed: `sqlcompare /Snapshot1:"\\fileserver\snapshots\Prod_before_v1.2.snp" /Database2:ProdServer\Prod /sync`

---

### Workflow 3: Snapshot Baseline + Drift Detection

**Preconditions:**
- Production is considered authoritative; all changes should go through a deployment process
- Team wants to detect ad-hoc schema changes made directly to production

**Steps:**

1. **Establish baseline snapshot** after each known-good release:
   ```bash
   sqlcompare /Database1:ProdServer\Prod /Makesnapshot:"C:\Baselines\Prod_v1.2_release.snp"
   ```
2. **Schedule daily drift check:**
   ```bash
   sqlcompare /Snapshot1:"C:\Baselines\Prod_v1.2_release.snp" /Database2:ProdServer\Prod
   ```
   - Exit code `63` = no drift (databases identical to baseline)
   - Exit code `0` (or `61`) = differences found
3. **On drift detected:** Generate an HTML report:
   ```bash
   sqlcompare /Snapshot1:"C:\Baselines\Prod_v1.2_release.snp" /Database2:ProdServer\Prod /report:"C:\Drift\$(date).html" /reportType:Interactive
   ```
4. **Investigate and remediate.** Either reverse the unauthorized change (deploy snapshot to live DB) or capture it as an approved change and update the baseline snapshot.
5. **Update baseline** after approved changes are released.

**Outcome:** Continuous production schema integrity monitoring with documented evidence.

**Edge cases:**
- Drift detection on a large database with many objects generates a large HTML report; use Excel format for summary-level alerting.
- Some "drift" is expected (e.g., SQL Server updates statistics automatically). Use `IgnoreStatistics` option in drift-detection comparisons.

---

### Workflow 4: Source Control Round-Trip

**Preconditions:**
- Scripts folder is checked into Git
- Team uses feature branches for database development
- CI pipeline runs on every push

**Steps — Developer side:**

1. **Pull latest from Git** (`git pull origin main`)
2. **Compare scripts folder to local dev DB:**
   ```bash
   sqlcompare /scripts1:"C:\Repos\MyApp\database" /db2:.\DevInstance\MyAppDev /sync
   ```
   This updates the local dev DB to match the committed schema.
3. **Make schema changes** in SSMS (add table, alter column, etc.)
4. **Compare dev DB back to scripts folder:**
   ```bash
   sqlcompare /db1:.\DevInstance\MyAppDev /scripts2:"C:\Repos\MyApp\database"
   ```
   Review changes, then sync (updates `.sql` files in the scripts folder).
5. **Commit the `.sql` files** (`git add -A && git commit -m "Add Customers.PreferredContactMethod column"`)
6. **Push to feature branch** and create a pull request.

**Steps — CI pipeline:**

```yaml
# Azure DevOps example
- script: |
    sqlcompare.exe /scripts1:"$(Build.SourcesDirectory)\database" /db2:$(CI_SERVER)\$(CI_DB) /sync /assertidentical
  displayName: "Deploy schema from source control"
```

**Outcome:** Schema is version-controlled at object level; every change is a diff-able commit.

**Edge cases:**
- If two developers change the same object (e.g., both add a column to Customers), the `.sql` file will have Git merge conflicts. SQL Compare can display the conflicted file but cannot resolve it.
- Deleting an object from the DB side and syncing to the scripts folder removes it from the comparison but does not delete the `.sql` file — the developer must `git rm` the file manually.

---

### Workflow 5: Scripts Folder Workflow (Offline, Version-Controlled)

**Preconditions:**
- Team wants to manage schema purely through source control (no live-to-live direct comparisons)
- Different developers work on separate branches
- DBA reviews all schema changes as code review on pull requests

**Steps:**

1. **Schema-as-code:** The `database/` folder in the repository is the single source of truth. All changes go through this folder.
2. **Developer workflow:**
   - Edit `.sql` files directly in the scripts folder (or use dev DB + sync)
   - PR with `.sql` file changes triggers CI
3. **CI validates scripts folder compiles:**
   ```bash
   # Compare against empty model DB to validate all objects can be created
   sqlcompare /scripts1:"$(Build.SourcesDirectory)/database" /db2:$(CI_SERVER)\model /scriptfile:build.sql
   # If exit code != 0 and != 63, build fails
   ```
4. **On merge to main:** CI deploys to integration environment:
   ```bash
   sqlcompare /scripts1:"$(Build.SourcesDirectory)/database" /db2:$(INT_SERVER)\$(INT_DB) /sync
   ```
5. **Release gate:** DBA compares scripts folder to production and approves the deployment script.

**Outcome:** All schema changes are reviewed as code; no DDL runs without approval; full history in Git.

---

### Workflow 6: CLI Automation in CI/CD

**Preconditions:**
- Flyway Enterprise or SQL Toolbelt license (required for server-side CLI automation)
- Build agent has SQL Compare installed or SQL Compare CLI distributed as a build tool
- Deployment targets are accessible from the build agent

**Complete Azure DevOps / GitHub Actions pipeline example:**

```powershell
# Step 1: Deploy from scripts folder to CI database
$deployArgs = @(
    "/scripts1:$env:BUILD_SOURCESDIRECTORY\database",
    "/server2:$env:CI_SERVER",
    "/database2:$env:CI_DATABASE",
    "/Options:IgnoreStatistics,IgnorePermissions",
    "/filter:$env:BUILD_SOURCESDIRECTORY\filters\ci.scpf",
    "/sync",
    "/scriptfile:$env:BUILD_ARTIFACTSTAGINGDIRECTORY\deploy-ci.sql",
    "/report:$env:BUILD_ARTIFACTSTAGINGDIRECTORY\ci-comparison.html",
    "/reportType:Interactive",
    "/force", "/q"
)
& "C:\SQLCompare\SQLCompare.exe" $deployArgs
if ($LASTEXITCODE -notin @(0, 63)) { exit $LASTEXITCODE }

# Step 2: Assert CI DB now matches scripts folder
$assertArgs = @(
    "/scripts1:$env:BUILD_SOURCESDIRECTORY\database",
    "/server2:$env:CI_SERVER",
    "/database2:$env:CI_DATABASE",
    "/assertidentical"
)
& "C:\SQLCompare\SQLCompare.exe" $assertArgs
# Exit code 79 = assertion failed (drift exists after deploy = problem)
```

**Key patterns for CI:**
- Always use `/scriptfile` to save the deployment script as a build artifact
- Always use `/report` for HTML evidence of what was deployed
- Handle exit code `63` (identical) as success, not error
- Use `/assertidentical` as a post-deployment verification step
- Set `/Options:IgnoreStatistics` for CI environments to avoid false positives from auto-updated statistics

---

### Workflow 7: Disaster Recovery via Snapshot

**Preconditions:**
- A pre-deployment snapshot exists
- A bad deployment has been applied to production
- The database cannot be restored from a full backup (too slow, or data changes since snapshot are acceptable to preserve)

**Steps:**

1. **Do not panic.** The snapshot captures the schema; data is preserved in the live database.
2. **Open SQL Compare.** Configure:
   - Source: the pre-deployment snapshot
   - Target: the production database (live)
3. **Run comparison.** SQL Compare identifies all differences between the snapshot (old state) and the current (bad) state.
4. **Review the deployment script.** It will undo every change the bad deployment made — re-creating dropped objects, reversing ALTER statements.
5. **Deploy.** This executes the rollback script.
6. **Verify.** Compare snapshot to live DB again — result should be identical.

> **Note:** This workflow rolls back schema only. Any data written to new tables/columns since the bad deployment will be lost (since those columns/tables are dropped). Data loss in this scenario must be accepted as a limitation of schema-only snapshots.

> **Caveat:** If the bad deployment dropped a table and data was inserted after the drop, that data is irrecoverable via schema rollback alone. Always pair snapshot-based rollback with a data backup strategy (SQL Data Compare or a full SQL Server backup).

---

## 6. Editions and Licensing

### Current Edition Structure

SQL Compare is sold as a per-user annual subscription. As of the current documentation, the product tiers align with Redgate's broader deployment tooling:

| Tier | Standalone Product | Included In |
|------|--------------------|-------------|
| **SQL Compare (Standard)** | Yes — individual purchase | SQL Toolbelt Essentials |
| **SQL Compare Pro (Professional)** | Yes — individual purchase | SQL Toolbelt, Redgate Flyway Enterprise, Redgate Deploy |

### Edition Feature Differences

| Feature | Standard | Professional |
|---------|----------|--------------|
| Database-to-database comparison | Yes | Yes |
| Scripts folder comparison | Yes | Yes |
| Snapshot comparison | Yes | Yes |
| Native backup comparison | No | **Yes** |
| SQL Graph support | Yes | Yes |
| Temporal table support | Yes | Yes |
| CLI for developer automation | Yes | Yes |
| CLI in CI/CD pipeline (server) | No | **Yes** (requires Flyway Enterprise / SQL Toolbelt) |
| SQL Server version coverage | Flyway Teams coverage | Flyway Enterprise coverage (broader) |
| Command-line licensing | Limited | Full |

> **Note:** The Standard/Professional distinction maps to Flyway Teams/Enterprise support tiers. "Standard" mirrors Flyway Teams SQL Server support; "Professional" mirrors Flyway Enterprise SQL Server support (which includes older and cloud versions).

### SQL Toolbelt Essentials Bundle

SQL Toolbelt Essentials is Redgate's flagship bundle for SQL Server development teams. It includes:

- SQL Compare (Standard)
- SQL Data Compare
- SQL Prompt
- SQL Search
- SQL Source Control
- SQL Test
- SQL Doc
- SQL Backup
- Plus several additional tools

### SQL Toolbelt (Full)

The full SQL Toolbelt adds Professional editions and additional enterprise tools. SQL Compare Professional is included when licensing via Redgate Deploy or Flyway Enterprise.

### Pricing Structure

Pricing uses a tiered per-user model (1-year subscriptions by default):
- 1–4 users
- 5–9 users
- 10–19 users
- 20+ users (contact sales)

Specific pricing is not published in documentation and must be obtained from Redgate's sales team or the product page.

### Free Trial

SQL Compare is available as a free 14-day trial downloadable from red-gate.com. All Professional Edition features are available during the trial.

---

## 7. SQL Server Compatibility Matrix

### On-Premises SQL Server

| SQL Server Version | Supported | Notes |
|-------------------|-----------|-------|
| SQL Server 2008 | Yes | Standard |
| SQL Server 2008 R2 | Yes | Standard |
| SQL Server 2012 | Yes | Standard; adds sequences, search property lists |
| SQL Server 2014 | Yes | Standard |
| SQL Server 2016 | Yes | Standard; adds row-level security, temporal tables GA, Always Encrypted, JSON |
| SQL Server 2017 | Yes | Standard; adds SQL Graph, Linux support |
| SQL Server 2019 | Yes | Standard; adds Big Data Clusters objects |
| SQL Server 2022 | Yes | Current; adds ledger tables, Azure Synapse Link |

### Cloud SQL Server

| Platform | Supported | Notes |
|----------|-----------|-------|
| Azure SQL Database | Yes | Many object types unsupported (see Section 3); AAD auth requires v12.4.9+ |
| Azure SQL Managed Instance | Limited | Not officially supported; use Flyway Enterprise for MI |
| Amazon RDS for SQL Server | Yes | Treated as a standard SQL Server instance |
| Azure SQL Database Hyperscale | Partial | Treated as Azure SQL DB with same limitations |

### Case Sensitivity

SQL Compare automatically detects the case sensitivity of a data source when connecting. When comparing a case-sensitive database with a case-insensitive one, SQL Compare uses the case sensitivity setting of the **source** database for the comparison.

### Always Encrypted

- Column metadata (that a column is encrypted, its encryption type, and key references) is compared
- Key management operations (rotating master keys, re-encrypting) are outside SQL Compare's scope
- Deploying a change that alters an Always Encrypted column may require manual key operations

### Transparent Data Encryption (TDE)

- SQL Compare compares the schema of TDE-encrypted databases without issue
- Schema metadata is not encrypted; SQL Compare reads it normally
- Deployment scripts are standard T-SQL and execute normally against TDE databases

### System Requirements

| Component | Requirement |
|-----------|-------------|
| **Operating System** | Windows Server 2008 R2+, Windows 7+ |
| **.NET Framework** | 4.7.2 or later |
| **SSMS** | Any version supported by the SSMS add-in installer |
| **SQL Server connectivity** | MDAC 2.8 or later |
| **Disk space** | Varies; snapshot files grow with database schema complexity |

---

## 8. Integration Surface

### SSMS Add-in

SQL Compare ships with a free SSMS Integration Pack add-in.

| Feature | Description |
|---------|-------------|
| Installation | Bundled with SQL Compare installer; check "SSMS Integration Pack" during setup |
| Activation | Right-click any database in Object Explorer → "Compare schema to..." |
| Direction swap | Toggle source/target within the add-in UI |
| Source control target | With SQL Source Control installed, compare to any VCS revision |
| SSMS compatibility | Compatible with standard SSMS versions |

### SQL Source Control (Redgate)

A separate Redgate SSMS add-in that provides:
- GUI for checking database objects in/out of VCS from within SSMS
- Migration script authoring in SSMS
- Exposes source control revisions as comparison targets for SQL Compare

### SQL Change Automation (Redgate)

SQL Change Automation (SCA) is Redgate's migration-based deployment tool for SQL Server, integrating with Azure DevOps, Visual Studio, and SSMS. SQL Compare is the underlying schema comparison engine used by SCA:

- SCA generates numbered migration scripts; SQL Compare validates state between migrations
- Drift detection in SCA uses the SQL Compare engine
- Filter files (`.scpf`) are shared between SQL Compare and SCA
- SCA extends SQL Compare with automated versioning, code analysis, and integrated testing

### DLM Dashboard (Deprecated / Legacy)

DLM Dashboard was Redgate's schema drift monitoring tool that sat "in front of" SQL Compare. It organized databases into pipelines, scheduled comparisons, and sent alerts. DLM Dashboard has been superseded by Redgate Monitor and SQL Change Automation. Filter files are shared with DLM Dashboard.

### Redgate Flyway

SQL Compare's engine is integrated into Flyway Enterprise for:
- Schema drift detection between Flyway-managed migrations
- Generating state-based deployment scripts to complement migration scripts
- Snapshot-based rollback support in Flyway Enterprise

### Redgate Deploy

Redgate Deploy is Redgate's modern CI/CD platform that includes SQL Change Automation and Flyway. SQL Compare Professional is part of the Redgate Deploy license.

### SQL Clone (Redgate)

SQL Clone creates virtual copies of databases using Windows virtual disk technology. Clones behave exactly like live SQL Server databases. SQL Compare operates against SQL Clone databases with no special configuration — they appear as normal SQL Server instances.

Typical combined workflow:
1. SQL Clone creates a lightweight clone of production
2. SQL Compare deploys schema changes to the clone
3. Automated tests run against the clone
4. If tests pass, SQL Compare deploys the same changes to production

### Azure DevOps / TFS

- SQL Compare CLI is invoked from Azure Pipelines YAML or Classic pipelines
- Release pipeline gates can use SQL Compare's exit codes for go/no-go decisions
- Build artifacts include the deployment script and HTML comparison report

### Jenkins / GitHub Actions / Other CI

- SQL Compare CLI runs on any Windows build agent
- PowerShell scripts wrap the CLI for parameterized pipeline integration
- Linux CI agents can use SQL Compare CLI (beta) without a Windows host

### SQL Comparison SDK

Redgate publishes a .NET SDK (separate product: SQL Comparison SDK) that exposes the SQL Compare engine as a class library. This enables embedding schema comparison into custom applications:

```csharp
// SDK example — compare two databases and get differences
var db1 = new Database();
db1.Register(new DatabaseConnectionInfo("Server1", "Database1"), Options.Default);
var db2 = new Database();
db2.Register(new DatabaseConnectionInfo("Server2", "Database2"), Options.Default);

var differences = db1.CompareWith(db2, Options.Default);
```

> **Note:** The SQL Comparison SDK is a separate licensed product from SQL Compare the UI tool. It is the underlying engine that the UI tool itself uses.

---

## 9. Non-Goals and Out-of-Scope Boundaries

This section documents what SQL Compare deliberately does **not** do, to guide both product scoping and clone implementation.

| Capability | Out of Scope | Alternative |
|------------|-------------|-------------|
| **Data comparison and sync** | SQL Compare compares schema only. Row data is not compared, moved, or synchronized | SQL Data Compare (companion Redgate product) |
| **Schema design / modeling** | SQL Compare reads existing schemas; it does not provide an ER diagram or design canvas | SQL Server Data Tools (SSDT), ER/Studio, etc. |
| **Query optimization / performance analysis** | No query plan analysis, index recommendations, or performance profiling | SQL Monitor, Redgate SQL Prompt (index analysis) |
| **Server-level objects** | Logins, server roles, linked servers, SQL Agent jobs are not database schema objects | Redgate SQL Multi Script, DBATools |
| **Cross-RDBMS comparison** | SQL Compare is SQL Server only; no PostgreSQL, MySQL, Oracle comparison | pgCompare (Redgate), Schema Compare for MySQL, Schema Compare for Oracle |
| **Database documentation generation** | SQL Compare does not generate ER diagrams or data dictionaries | SQL Doc (Redgate) |
| **Test data generation** | Not a responsibility of a schema comparison tool | SQL Data Generator (Redgate) |
| **Database backup management** | SQL Compare can read backup files; it does not manage the backup lifecycle | SQL Backup Pro (Redgate) |
| **Merge conflict resolution** | SQL Compare can display files with Git conflict markers but cannot resolve conflicts | Manual resolution or VCS merge tools |
| **Numbered migration management** | SQL Compare is state-based; it does not number or track migrations | SQL Change Automation, Flyway |
| **Always Encrypted key rotation** | Comparing columns with AE metadata is supported; key management is not | SQL Server Management Studio, Azure Key Vault tooling |
| **Server-side scheduling** | SQL Compare has no built-in scheduler; use Windows Task Scheduler or CI platforms | Windows Task Scheduler, Azure DevOps pipelines |
| **Conflict-aware multi-developer merge** | No awareness of parallel development or merge strategies | VCS tooling (Git, TFS) |

> **Note:** The boundary between SQL Compare and SQL Data Compare is the most common source of user confusion. A clean mental model: SQL Compare = schema (DDL); SQL Data Compare = rows (DML). They are sibling products that are frequently used together (compare schema, then compare reference/lookup table data) but are not the same tool.

---

## 10. Glossary

| Term | Definition |
|------|------------|
| **Comparison session** | A single instance of comparing two data sources. Defined by: source, target, options, and filter. Can be saved as a project file. |
| **Deployment script** | The T-SQL script generated by SQL Compare that transforms the target schema to match the source. Dependency-ordered, transaction-wrapped by default. |
| **Project file (.scp)** | An XML file storing a complete comparison session configuration: data source connections, options, filter reference, and deployment settings. Default location: `%USERPROFILE%\Documents\SQL Compare\SharedProjects\`. |
| **Snapshot (.snp)** | A binary, immutable, point-in-time capture of a database's schema metadata. Contains no row data. Can be used as a comparison source or target. |
| **Filter file (.scpf)** | An XML file defining which objects to include or exclude from a comparison, by type, name, or pattern. Default location: `%USERPROFILE%\Documents\SQL Compare\Filters\`. Shared across SQL Compare, SQL Source Control, DLM Dashboard, and SCA. |
| **Scripts folder** | A directory containing one `.sql` file per database object, organized into subdirectories by object type. The primary mechanism for schema-as-code and VCS integration. |
| **Migration script** | A custom T-SQL script placed in `Pre-Deployment\` or `Post-Deployment\` subdirectories of a scripts folder. Executed before or after the SQL Compare synchronization script to handle data transformations or non-schema changes. |
| **Ignored object** | An object excluded from comparison via a filter, an `/exclude` CLI switch, or by unchecking it in the results grid. Ignored objects are not included in deployment scripts. |
| **Comparison key** | The identity by which SQL Compare matches objects between source and target (typically: schema + object name + object type). Objects with no match on the other side are flagged as "only in source" or "only in target." |
| **Object existence check** | T-SQL `IF EXISTS` / `IF NOT EXISTS` guards added to deployment scripts when the `ObjectExistenceChecks` option is enabled. Prevents errors if the schema has changed between script generation and execution. |
| **Synchronize** | The act of deploying source schema changes to the target. In the CLI: `/Synchronize` or `/sync`. In the UI: the Deploy action in the Deployment Wizard. |
| **Drift** | Schema differences between a known-baseline state (snapshot or scripts folder) and a live database's current state. Drift typically indicates unauthorized or untracked schema changes. |
| **tSQLt** | An open-source T-SQL unit testing framework. SQL Compare can ignore tSQLt objects during comparison via the `IgnoretSQLt` option. |
| **HNSW** | (Internal note for clone context) Not a SQL Compare concept; appears in the claude-flow configuration. |
| **SQL Snapper** | `RedGate.SQLSnapper.exe` — a lightweight command-line utility bundled with SQL Compare for creating snapshot files from SQL Server databases. |
| **DLM Dashboard** | Database Lifecycle Management Dashboard — a legacy Redgate monitoring tool that used SQL Compare's engine to track schema drift across database pipeline environments. Superseded by Redgate Monitor and SCA. |
| **SQL Change Automation (SCA)** | Redgate's migration-based SQL Server CI/CD tool. Uses SQL Compare's engine internally for state comparison and drift detection. |
| **SQL Toolbelt Essentials** | Redgate's flagship product bundle for SQL Server developers, including SQL Compare, SQL Data Compare, SQL Prompt, SQL Source Control, and others. |
| **Argfile** | An XML argument file for the SQL Compare CLI. Encodes all command-line switches in a file for version-controlled, repeatable automation. Used with `/argfile:path`. |
| **SQL Clone** | A separate Redgate product that creates virtual copies of SQL Server databases using Windows disk virtualization. SQL Compare treats SQL Clones as normal SQL Server databases. |
| **SQL Comparison SDK** | A Redgate .NET class library that exposes the SQL Compare schema comparison engine for embedding in custom applications. A separate licensed product from the SQL Compare UI tool. |
| **PolyBase** | SQL Server feature for querying external data sources (Hadoop, Azure Blob Storage, etc.). SQL Compare supports PolyBase-related objects: External Data Source, External File Format, External Table. |
| **Service Broker** | SQL Server's built-in asynchronous messaging framework. SQL Compare supports Service Broker objects: Contract, Message Type, Queue, Route, Service, Service Binding. |
| **ForceColumnOrder** | A SQL Compare option that forces a table rebuild when column ordinal positions differ between source and target. Without this option, SQL Compare ignores column order differences. |
| **ObjectExistenceChecks** | A SQL Compare deployment option that adds `IF EXISTS` guards to DDL statements, ensuring the script is safe to re-run. |
| **AssertIdentical** | A CLI switch (`/assertidentical`) that causes SQL Compare to exit with code `79` if the two data sources differ. Used in CI pipelines as a post-deployment verification step. |

---

*End of document — SQL Compare Product Overview v1.0*

*Sources consulted: Redgate SQL Compare 16 official documentation (documentation.red-gate.com/sc), Redgate product pages (red-gate.com/products/sql-compare/), Redgate Hub product learning articles, and Redgate community forum posts. Documentation last updated by Redgate: December 10, 2025.*
