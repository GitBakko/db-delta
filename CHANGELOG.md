# Changelog

All notable changes to **DbDelta** are tracked in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until the v1.0.0 RC is cut, the project is on an `0.x` series where each `M`-milestone bumps the minor.

## [Unreleased]

### Added

- **The theme can now follow Windows.** The topbar button used to flip between
  light and dark and forgot the choice at every launch, always reopening light.
  It now cycles Chiaro → Scuro → Sistema, the icon shows which of the three is
  active, and the choice is remembered in
  `%LOCALAPPDATA%\DbDelta\ui-settings.json`. "Sistema" tracks the Windows
  light/dark setting; a saved Chiaro or Scuro overrides it.

## [1.0.1] — 2026-08-11 — first final release

**The first final release is `1.0.1`, not `1.0.0`.** Every release candidate
carries the numeric ProductVersion `1.0.0`, and Windows Installer does not
upgrade between identical versions — a `1.0.0` final would have made every RC
user uninstall by hand first. Verified by installing a `1.0.1` MSI over rc5:
one entry in Apps & Features, not two, and the CLI's folder neither duplicated
nor lost from the machine PATH.

The MSI is still unsigned. Code signing needs a certificate that has not been
obtained; it can be added later without changing anything else in the product.

### Fixed

- **A server, once picked, could not be changed.** The server field in the
  new-project dialog was an autocomplete box that filtered its own list by the
  text it had just written: selecting a server left exactly one entry matching,
  so correcting a wrong pick was impossible, and clearing the field to start
  over took the app down. Browsing and typing are now two controls — a
  drop-down that always lists every known server, and a text field that stays
  editable so a server the network scan cannot see is still reachable.
- **Three of the four ways into the new-project dialog opened it half-wired.**
  Only the startup path filled the server list from the connection store and
  started the network scan. "Nuovo", "Modifica" and loading a project from the
  recent list each built the dialog themselves and got neither, so the picker
  came up empty. The scan now belongs to the dialog and the seeding to the
  view-model, where a caller cannot forget them.
- **The "Nuovo" toolbar button did nothing.** It had been left as an empty stub
  and never wired to the dialog it was supposed to open.
- **Loading a project threw away the discovered servers.** They come from the
  network scan and the connection store, not from the project, and clearing
  them emptied the picker exactly when the user was about to change endpoint.

### Added

- **"Nuovo" asks before discarding an open project.** Starting a new project
  silently dropped the current comparison, the ticked rows, and anything not
  yet saved or executed.

### Changed

- **The Apps & Features entry has an icon and an install location.** Found by
  installing the rc5 MSI rather than running from a build folder:
  `ARPPRODUCTICON` alone leaves `DisplayIcon` an empty string, so the entry
  showed a blank tile. `DisplayIcon` is now written explicitly, pointing at the
  application's own executable, and `InstallLocation` is set.

## [1.0.0-rc5] — 2026-08-01 — fifth release candidate

The first release candidate to have deployed a real database end to end and
been re-compared to zero differences: 279 objects from one live database to
another, then a fresh comparison finding nothing left to do. Everything below
came out of that exercise and out of a full adversarial review of the codebase.

### Added

- **A dialog that asks for a value instead of failing on the rows that have
  none.** A `NOT NULL` column with no default cannot be added to a table that
  already has rows — no tool can, without inventing a value. Before generating
  or executing, DbDelta now lists every such column (table, column, type) with
  a suggested value of the right shape, and lets you edit it. The value seeds
  the existing rows through a temporary constraint that the next statement
  drops, so the column ends up exactly as the source declares it. Cancelling
  cancels the whole action: without the values the script would die on the
  first populated table anyway.
- **Everything the server said, one click away.** Errors used to be reduced to
  a single message. DbDelta now keeps every error SQL Server raised *and* every
  `PRINT` the script emitted — the running commentary of which object was being
  worked on when something failed. A pill in the status bar carries the verdict
  and the error count for the last run; opening it shows the full transcript,
  headed the way SSMS heads one, with a copy button.
- **A successful run refreshes the results it just invalidated.** The grid used
  to keep showing the objects that had just been aligned as different, with
  their checkboxes still ticked. On failure nothing is touched, deliberately:
  the script rolls itself back, so the rows still describe the target and the
  selection is what you retry.
- **Select or deselect every visible difference, and every group.** Scoped to
  what the search box leaves on screen: a row added to the selection behind
  your back becomes a statement in the deploy script nobody reviewed.
- **Schemas are compared and deployed.** A source-only schema was never
  reported and no `CREATE SCHEMA` was ever emitted, so any object living in a
  schema the target lacked failed on its first statement.
- **Per-module `QUOTED_IDENTIFIER` and `ANSI_NULLS`.** These are compiled into
  a module, not taken from the session that runs it: with `QUOTED_IDENTIFIER`
  off a `"quoted"` token is a string and not an identifier. Two byte-identical
  definitions compiled under different settings are different objects, and are
  now reported and deployed as such.
- **`DATA_COMPRESSION`, for a table's own rows and for each index.** They are
  independent settings on the server and routinely differ on the same table.
  A change is a `REBUILD` rather than a drop and re-create.
- **A metadata-visibility preflight.** A login that can see object names but
  not their definitions produced a comparison that silently understated the
  differences. DbDelta now refuses the load and says which permission is
  missing.

### Changed

- **A deploy gets ten minutes per batch, not sixty seconds.** An index rebuild
  or a large `CREATE INDEX` passes a minute without anything being wrong; the
  old limit aborted the batch and rolled back everything before it. Not
  unlimited — that belongs with a cancel button, which the execution dialog
  does not yet have. The CLI's `dbdelta apply --command-timeout 0` is
  unaffected: a console has Ctrl-C.
- **Results stop being actionable once they stop describing the endpoints.**
  Repoint a connection, or refresh and have it fail, and the buttons that would
  deploy those rows are disabled with a banner saying why — rather than
  offering to execute a script computed against a different server.

### Fixed

- **A module deployed from its own script now compares identical.** After a
  clean deploy, 33 modules still reported Different, and re-generating the
  script produced byte-for-byte the text already applied — a difference no
  operator could remove. The emitter tested whether the last *character* of a
  body was a semicolon, so a definition ending `";\n"` (most of them) had a
  second one appended, and the comparison stripped only one of the two.
- **A constraint over a column added in the same batch.** SQL Server compiles a
  whole batch before running a line of it, so a `CHECK` over a column the same
  batch adds cannot resolve that column. This broke *every* deploy that added a
  column plus a constraint on it.
- **Object pairing follows the target's collation.** On a case-insensitive
  target, `dbo.Clienti` and `dbo.CLIENTI` are the same object; pairing them
  ordinally produced a `DROP TABLE` for a table the same script was about to
  create.
- **An identity rebuild no longer destroys indexes, triggers and foreign
  keys.** The rebuild re-emits the complete source-side set, including objects
  that are identical on both sides and therefore appear in no delta.
- **Foreign keys are dropped before anything they block.** Dropping a table or
  retyping a column is blocked by an inbound key; those drops now happen in one
  pass up front, covering keys held by tables that are not part of the change.
- **Indexes, keys and CHECK constraints that depend on a column are cleared
  before it is retyped or dropped, and restored afterwards** — including ones
  identical on both sides, which no delta would have re-created.
- **A DEFAULT-only change no longer drops the primary key and every index on
  the column.**
- **Constraint names are schema-scoped, index names are table-scoped.** Keying
  them database-wide dropped the wrong one, or skipped a drop entirely, on any
  database that reuses a name across schemas or tables.
- **The deploy script header no longer echoes a password**, and no longer drops
  permissions the user explicitly selected.
- **Named DEFAULT constraints emit inline on their column**, which is the only
  form `CREATE TABLE` accepts.
- **Database-scoped permissions emit without an `ON` clause**, which is not
  valid T-SQL.
- **Triggers whose parent is a view are read.** Every `INSTEAD OF` trigger was
  invisible to the comparison.
- **The application starts with an unreadable settings file** instead of
  failing at launch, while a save that cannot be completed still reports
  failure rather than overwriting saved connections.
- **A rollback is reported only when it actually happened.** "Nothing was
  applied" and "we could not confirm" are different facts, and the old result
  claimed the first for both.
- **Errors and action outcomes are shown in the shell.** Two different status
  fields existed and the one carrying deploy and save outcomes was displayed
  nowhere.
- **The select-all checkbox in the grid header is visible and clickable** —
  it had zero width, then a clipped glyph, in three successive shapes.
- **An index's `INCLUDE` list comes back in the index's own order**, so two
  reads of an unchanged index no longer disagree and rebuild it.

## [1.0.0-rc4] — 2026-06-05 — fourth release candidate

### Added
- **Redesigned direct-execution dialog.** Executing the alignment script
  against the target is now a full guided flow: a source → target summary
  card (connection strings redacted), a breakdown of the selected objects by
  difference type, a transaction/rollback note, in-dialog execution with a
  busy indicator, and a clear success / failure outcome panel showing the
  real SQL Server error when something goes wrong. The window cannot be
  closed while the script is running.

### Changed
- **The "Identici" group starts collapsed.** When the results grid is
  grouped by difference type, the Identical group — confirmation noise by
  definition — now initialises collapsed; expand it on demand. The other
  groups stay expanded.

### Fixed
- **Un-flattenable whitespace differences on tables.** Column DEFAULT and
  computed-column expressions, CHECK / DEFAULT constraint expressions and
  filtered-index predicates were compared byte-for-byte, while SQL Server
  re-formats those definition texts when storing them — so newline/spacing
  drift between servers was reported as Different and reappeared even right
  after applying the generated alignment script. These texts are now
  compared whitespace-insensitively, and the script generator uses the same
  rule, so cosmetic drift no longer triggers findings or spurious
  constraint/index rebuilds.
- **Diff-table dates showed the wrong timezone.** `sys.objects.modify_date`
  is the DB server's local clock, but it was treated as UTC and then
  converted to the client timezone, showing shifted times whenever client
  and server zones differed. Dates are now displayed verbatim — exactly what
  the server reports.

## [1.0.0-rc3] — 2026-06-04 — third release candidate

### Fixed
- **False positives on modules with a comment banner before `CREATE`.**
  Real-world definitions frequently open with an SSMS-style comment banner
  (`-- ===== Author / Create date =====`) or a block comment before the
  `CREATE` token. The module-header parser only recognised headers at the
  very start of the definition, so banner'd views, procedures, functions and
  triggers never received the stale-name reconciliation shipped in rc2 and
  compared as Different on semantically identical bodies. The parser now
  skips any mix of whitespace and `--` / `/* … */` comments before `CREATE`
  while preserving the banner verbatim, so genuine comment-only differences
  still surface.
- **Generated scripts for banner'd modules deployed as plain `CREATE`.** The
  same parsing limitation meant the script generator failed to upgrade the
  create verb on banner'd bodies, emitting `CREATE` instead of
  `CREATE OR ALTER` — which fails on deploy when the object already exists.
  The verb upgrade now applies regardless of any leading banner, preserving
  the original formatting and the short `PROC` keyword where used.

## [1.0.0-rc2] — 2026-06-04 — second release candidate

### Added
- **Consultable version history.** The docs site gains a
  [Version history](https://gitbakko.github.io/db-delta/articles/version-history.html)
  page rendered from this changelog with a stable anchor per version, and the
  desktop app now shows the running version (status-bar pill and topbar
  banner) — click the pill to open the page at your version. Published
  binaries are stamped with the tag-driven semver, so the app and
  `dbdelta --version` report the real release; local dev builds show
  `0.0.0-dev`.

### Fixed
- **False positives on modules renamed with `sp_rename`.** SQL Server keeps
  the pre-rename name frozen inside `sys.sql_modules.definition`, so two
  databases whose only divergence was that stale embedded name compared as
  Different. The comparison now reconciles the embedded `CREATE … <name>`
  with the catalog identity (views, procedures, functions, triggers), and
  generated `CREATE OR ALTER` scripts always target the catalog name, never
  the stale one.
- **Missing application icon.** The installed `DbDelta.App.exe` showed the
  generic Windows icon. The exe now embeds a proper multi-size icon, and the
  MSI wires it to the Start-Menu shortcut and the Apps & Features entry.

## [1.0.0-rc1] — 2026-05-28 — first release candidate

First release candidate for v1.0.0. The full v1.0 scope is feature-complete —
milestones M1–M13, the #24 Kahn dependency resolver, the #25 DocFX docs site,
the WiX MSI installer, and the verbose self-contained deploy script are all
shipped (see the 0.x history below). This tag marks the v1.0 feature freeze and
begins release-candidate stabilization; code signing and the public
announcement remain before v1.0.0 final.

### Changed
- CI workflows bumped to Node-24 action majors (`checkout` v6, `setup-dotnet`
  v5, `cache` v5, `upload-artifact` v7, `configure-pages` v6,
  `upload-pages-artifact` v5, `deploy-pages` v5, `action-gh-release` v3) ahead
  of GitHub's 2026-06-02 Node-20 runtime cutover.
- `ScriptGenerator` role-emitter / phase-label cleanup — internal refactor, no
  behavioural change to generated scripts.

### Fixed
- The desktop app's "Salva/Build alignment script" path emitted a malformed
  preamble (a dropped `SET NUMERIC_ROUNDABORT OFF` and an orphaned `GO`) because
  the deploy builder still trimmed the pre-`0.17.0` two-line header. It now
  reuses the generator's full self-contained envelope verbatim. The CLI
  `dbdelta script` path was unaffected.

## [0.17.0] — 2026-05-28 — verbose self-contained deploy script

### Changed
- **Deploy scripts are now self-contained and verbose (Redgate-style).** Every
  operation is preceded by a `PRINT` of the exact phase and followed by an
  `IF @@ERROR <> 0 SET NOEXEC ON` gate, framed by a full `SET` preamble +
  transaction and a final `The database update succeeded`/`failed` verdict that
  rolls back on failure — so a deploy aborts at the first failing step with the
  native error as the reason, instead of running silently. Proven both via
  `dbdelta apply` and standalone (sqlcmd/SSMS). The `NoTransactions` option is
  now honoured. `dbdelta apply` (and the app's execute path) run the
  self-managing script without an outer transaction (`SqlExecutor` gained a
  `useOwnTransaction` flag).

## [0.16.0] — 2026-05-27 — docs site + Windows installer

### Added
- **DocFX documentation site (#25)** — API reference auto-generated from the
  XML docs of the four library projects (`Core`, `Shared`, `Persistence`,
  `Providers.LiveDb`) plus getting-started / CLI / comparison-options guides,
  published to GitHub Pages and auto-deployed on every push to `main`
  (`.github/workflows/docs.yml`). DocFX is pinned as a dotnet local tool.
  Live at <https://gitbakko.github.io/db-delta/>.
- **Windows MSI installer (WiX v5)** — a per-machine MSI installs the DbDelta
  desktop app (Start-Menu shortcut) and the `dbdelta` CLI (added to the system
  `PATH`), self-contained `win-x64`. Built and attached to the GitHub Release on
  `v*` tags (`.github/workflows/release.yml`). The MSI is **unsigned** (no
  code-signing certificate yet), so Windows SmartScreen / UAC shows an "unknown
  publisher" prompt on first run.

## [0.15.0] — 2026-05-27 — #24 dependency resolver

### Added
- **Object-level dependency resolver** (#24, spec M7) — a pure
  `DbDelta.Core/Dependency/` module (`DependencyEdge`, `DependencyResolver`)
  topologically orders deploy-script emission via Kahn's algorithm with a
  deterministic `(kind-rank, schema, name)` tiebreak. `LiveDbSource` populates
  `Database.Dependencies` from `sys.sql_expression_dependencies`.
- **Cross-kind CREATE ordering** — `ScriptGenerator` now emits objects in
  dependency order, fixing scripts that previously failed when a computed
  column referenced a scalar function, a view selected from another view, a
  view referenced a function, or a schemabound TVF referenced a table. With an
  empty edge list the order is byte-identical to the previous
  kind-then-alphabetical output, so existing fixtures are unaffected.
- **Reverse-topological DROP pass** — removed objects are dropped
  dependent-first (e.g. a table is dropped before a sequence it references).
  Foreign-key cycles continue to be handled by the existing final FK phase
  (FK edges are excluded from the topo graph).

### Fixed
- **Module body trailing-semicolon parity** — `BodyNormalizer` strips a single
  trailing `;` so a module whose stored definition lacks the semicolon the
  script generator appends no longer reports a spurious `Different`.

### Tests
- Pure resolver unit tests (Kahn ordering, FK-edge exclusion, deferred-kind
  cycle tolerance, create-validated cycle throw); a live cross-kind round-trip
  integration test (generate → apply on a Testcontainers SQL Server → converge);
  and an FsCheck property asserting valid topological linearization +
  determinism. The `Sequences_Precede_Tables` property was split into the two
  real directional invariants (CREATE sequence-before-table; DROP
  table-before-sequence).

### Known limitations
- DROP ordering uses the source database's dependency edges, so removed
  (target-only) objects are dropped in reverse kind-rank order rather than by
  their target-side dependencies. Safe for standard schemas; supplying
  target-side edges would tighten within-kind drop chains.

## [0.14.0] — 2026-05-27 — M13 wave 2 — parity hardening + quality bar

### Added
- **Column collation diff** (#32, M13-PARITY.5) — `Column.Collation` +
  `Database.DefaultCollation` are now read (`sys.columns.collation_name`
  + `DATABASEPROPERTYEX`) and `TableScriptEmitter` emits a diff-aware
  `COLLATE` clause: skipped when it matches the target DB default,
  defensively explicit when the default is unknown.
- **PK-around-swap identity rebuild + inbound-FK lifecycle** (#33,
  M13-PARITY.6) — `EmitRebuild` drops named non-FK constraints before the
  `_tmp` table; `ScriptGenerator` orchestrates inbound FKs onto rebuilt
  tables via new pipeline sections 0.9 (drop) + 7.9 (re-add), name-deduped
  against the section-7 pair-level FK delta.

### Tests
- **BenchmarkDotNet perf suite** (#19, M13-PERF.1) — `bench/DbDelta.Benchmarks`
  (`ComparisonBench` + `ScriptGenBench`, `SchemaFixtureBuilder`) calibrates
  against spec §6.1. Baseline 2026-05-26: `ComparisonBench.Compare` 10k tables
  = 17.8 ms vs the 3000 ms budget (~170× under).
- **FsCheck property suite** (#20, M13-PERF.2) — `tests/DbDelta.Property.Tests`
  drives FsCheck 3.0 invariants over generated schemas (6 comparison-engine
  + 5 script-generator properties): determinism, ordering, idempotence.
- **Nightly compat matrix** (#21, M13-PERF.3) — `tests/DbDelta.Compat.Tests`
  round-trips read→diff→script→apply against real SQL Server 2017/2019/2022
  images via Testcontainers; self-skips unless `DBDELTA_COMPAT=1` + Docker.
  SQL Server 2016 has no Linux container (SQL-on-Linux began at 2017) so it
  remains a min-compat target exercised on live Windows instances only.
  `ci.yml` gains a `schedule` (cron 03:17 UTC) + scheduled-only
  `nightly-compat-matrix` job.
- 385 / 385 passing across 11 test projects (compat cases skipped by default).

### Documentation
- `docs/parity/redgate-2026-05-25.md` updated with the #31..#33 follow-up
  status table; `tests/Fixtures/Parity/` ships scenario 12 (`dbo.[Order]`
  identity-flip + `dbo.OrderLine` inbound FK).

## [0.13.1] — 2026-05-25 — Redgate parity patch

### Fixed
- **Sequence diff** — when only seed / increment / min / max / cycle /
  cache differ (data type unchanged) `ScriptGenerator` now emits
  `ALTER SEQUENCE [schema].[name] RESTART WITH n …` rather than
  DROP + CREATE. The old DROP path broke any column with
  `DEFAULT NEXT VALUE FOR <seq>` because the dependent default
  constraint was dropped alongside the sequence. DROP + CREATE
  remains the fallback when the base data type actually changes.
  Caught by the 2026-05-25 Redgate SQL Compare parity run
  (`docs/parity/redgate-2026-05-25.md`, scenario 08).

### Tests
- +8 `SequenceAlterTests` cover seed-only, increment-only,
  combined, data-type fallback to DROP+CREATE, cycle toggle,
  cache size change, cache disable, min/max change.
- 355 / 355 passing (+8).

### Documentation
- `docs/parity/redgate-2026-05-25.md` records the full 11-scenario
  parity matrix (6 match + 4 cosmetic + 1 bug, all triaged).
- `tests/Fixtures/Parity/` ships the deterministic 11-scenario
  fixture so anyone can rerun the parity audit against Redgate
  SQL Compare 16.x.

## [0.13.0] — 2026-05-25 — M13 wave 1 alpha (RC candidate)

### Fixed
- **Critical** — `ScriptGenerator` now emits DDL for every M5/M6 object
  kind. Until this release `Sequence`, `Synonym`, alias `UserDefinedType`,
  `User`, `Role`, and `Permission` emitters existed as classes but were
  never called by the pipeline; deployment scripts for any schema using
  those kinds silently shipped without the corresponding DDL. New
  pipeline order: prologue (Sequence → UserDefinedType → TableType →
  User → Role) → body (Table → Index → View → Function → Procedure →
  Trigger) → epilogue (Synonym → FK → Permission, gated on
  `!IgnorePermissions`).
- `TableScriptEmitter` now performs the spec §3.4 temp-table rebuild
  when an existing column's IDENTITY flag flips or its seed / increment
  changes. Previously the path emitted `DROP COLUMN + ADD COLUMN`,
  which silently erased every row's value in the affected column.

### Added
- **CLI `script` verb** — `dbdelta script --source --target [--out path | -]
  [--include-permissions]` runs the comparison and writes a deployment
  script to disk (or stdout). Closes the spec §1.2 CLI surface gap.
- **CLI `apply` verb** — `dbdelta apply --target --script <path> [--dry-run]`
  executes a pre-generated script via the existing `SqlExecutor`
  GO-batched single-transaction runner; `--dry-run` parses the script
  and reports its batch count without touching the target.
- **Table-type UDT (UDTT) — 13th object kind.** New `TableTypeUdt`
  record, comparison engine path, `TableTypeUdtScriptEmitter`
  (CREATE TYPE … AS TABLE / DROP TYPE), and live-DB
  `TableTypeUdtReader` over `sys.table_types`. `KindCatalog.KnownKinds`
  grows from 11 → 12; new Italian display label "Tipi tabella".
- **Hexagonal sharedness** — `Cli/CliErrorMapper` pulled up to centralise
  the `Error → exit code + JSON stderr` mapping shared by `compare`,
  `report`, `script`, and `apply` verbs.

### Refactored
- DRY: `Views/Controls/PasswordBox.axaml` extracted from three call
  sites (`ProjectSetupDialog` Source + Target halves and
  `ConnectionEditDialog`); the six hand-rolled
  `PointerPressed/Released` reveal handlers now live inside the
  control. CLAUDE.md UI rule #3.
- DRY: shared `Styles/Templates.axaml` resource dictionary holds the
  canonical `DiscoveredServerItemTemplate`; the three inline 40-line
  copies in `ProjectSetupDialog` + `ConnectionEditDialog` collapse to
  `<StaticResource ResourceKey="DiscoveredServerItemTemplate" />`. The
  `ConnectionEditDialog` site automatically picks up the section-header
  variant it had been missing.

### Documentation
- `README` refreshed to reflect the Avalonia 11 GUI pivot (away from
  Blazor Hybrid + WebView2). Adds `report` / `script` / `apply` verb
  examples and an architecture table.
- New `docs/superpowers/specs/2026-05-25-avalonia-ui-pivot-addendum.md`
  capturing the rationale for the UI stack change and superseding the
  Blazor / WebView2 rows of the v1 design spec.
- `scripts/SMOKE-RESULTS.md` — sanitised end-to-end smoke run against
  the live PcrmV2Pl_test2 / PcrmV2Pl endpoints (680 objects compared,
  4 TableType UDTTs surfaced, 4 691-line migration script generated).
- `scripts/PUBLISH-SIZES.md` — measured single-file binary sizes
  (framework-dependent 20 MB CLI / 28 MB App vs §6.1 budget), explains
  why self-contained + trim is post-RC work.

### Tests
- 347 / 347 passing across nine projects: Core unit 207
  (+8 TableType, +6 table-rebuild, +24 orphan-kind, +1 KindCatalog
  TableType slot), Shared unit 4, Architecture 3, App Headless 39
  (+4 PasswordBox), ScriptGen Golden 28, Persistence unit 30,
  Persistence integration 6, LiveDb integration 14
  (+1 TableTypeUdtReader), CLI acceptance 16 (+5 script/apply).

## [0.12.0] — 2026-05-25 — M12 Reports HTML + JSON

### Added
- `Core/Reports/KindCatalog` — single source of truth for Kind sort order, Italian display labels, and Status visual priority.
- `Core/Reports/HtmlReportGenerator` — self-contained HTML5 report with embedded CSS, collapsible `<details>` sections per Kind, palette-coded rows (cobalt `#0064C8` / crimson `#B31220` / emerald `#007339` / grey `#9097A0`).
- `Shared/Reports/JsonReportGenerator` — pretty-printed camelCase JSON via the existing `Mapper.ToDto` projection.
- `Cli/Commands/ReportCommand` — `dbdelta report --source --target --html=<path> --json=<path>` (at least one of `--html`/`--json` is required).

### Refactored
- DRY: `Cli/CliErrorMapper` pulled up from `CompareCommand` to be reused by `ReportCommand`.
- DRY: `tests/DbDelta.Cli.AcceptanceTests/CliRunner` shared between `compare` and `report` acceptance suites.

### Tests
- 299 passing (up from 233 at end of M8): +66 across Core unit (KindCatalog + HtmlReportGenerator), new `tests/DbDelta.Shared.UnitTests` project for JsonReportGenerator, and four `report` CLI acceptance tests.

## [0.11.1] — 2026-05-23 — M11-bis Connection Management
- DPAPI-backed per-server credential store (`Meziantou.Framework.Win32.CredentialManager`).
- Connection edit dialog with password reveal / copy / paste behaviour.
- Per-band server discovery with Italian date formatting.

## [0.11.0] — M11 Persistence
- `.dbd` XML project file v2 (`XmlProjectStore`), `JsonRecentProjectsStore`, configurable `ProjectsFolder`.
- Save / Load / Edit project verbs in the Avalonia shell; MRU list; per-project selection persistence.

## [0.10.0] — M10 GUI polish (Avalonia)
- 15 rounds of UX iteration on top of the Avalonia shell:
  - Red-Gate-style topbar restructure, `ProjectHeaderStrip`, results-grid `DataGrid`.
  - Dual-pane SQL diff viewer with minimap, synced scroll, section navigation.
  - Crimson `ConfirmExecuteDialog`, busy overlays, in-button compact spinner.
  - DiscoveredServer sectioned picker, IP-in-band badges, auto-scan + clone config.
  - Theme-aware diff lines, dark-mode contrast audit, accordion chevron + single-click toggle.
  - Compact loaders (`LoadingContent` UserControl), invariable UI rules (no naked buttons, 32 px monoline).

### Architecture decisions in this window
- The original Blazor Hybrid + WebView2 GUI was abandoned in favour of Avalonia 11 + Fluent + CommunityToolkit.Mvvm. See [the pivot addendum](docs/superpowers/specs/2026-05-25-avalonia-ui-pivot-addendum.md).

## [0.9.0] — M9 Deployment executor
- `SqlExecutor` — GO-splitting transactional batch runner with `SET XACT_ABORT ON` and rollback on failed batch.
- `DeployScriptBuilder` — alignment script composer that wraps emitted DDL in the spec-§3.1 batch envelope.

## [0.8.0] — M8 Script-gen polish (shipped with M7 in commit `2af2200`)
- `TableScriptEmitter.EmitAlter` complete: drop columns, alter columns, add columns, drop / add constraints.
- `ScriptGenerator` emits index + FK delta for `Different` tables (not just columns).
- Golden tests added for ALTER permutations.

## [0.7.0] — M7 Dependency resolver
- Two-phase ordering: `CREATE TABLE` for all tables first, then FKs batched after. Handles circular cross-table FKs without needing an explicit cycle-break pass for v1.

## [0.6.0] — 2026-05-23 — M6 User + Role + Permission
- `Database.Users / Roles / Permissions` read paths via `LiveDbSource`.
- Comparison engine for users (type code + login + default schema), roles (owner + member set), permissions (Grantee + Action + Target presence/absence).
- `UserScriptEmitter`, `RoleScriptEmitter`, `PermissionScriptEmitter`.

## [0.5.0] — M5 Sequence + Synonym + alias UserDefinedType
- New object kinds with full read / diff / emit support.
- `BodyNormalizer` extended to handle module-less kinds via property comparison.

## [0.4.0] — M4 Functions + Triggers
- Scalar, inline TVF, and multi-TVF `Function` read + diff + emit.
- DML `Trigger` with disabled / not-for-replication state diffing on top of body equality.

## [0.3.0] — M3 Views + Stored Procedures
- `View` + `StoredProcedure` modules with `BodyNormalizer` whitespace / comment handling.
- `ViewScriptEmitter` + `ProcedureScriptEmitter` with `CREATE OR ALTER` semantics.

## [0.2.0] — M2 Constraints + Indexes
- Primary keys, unique constraints, foreign keys, checks, defaults.
- `TableIndex` with key + included columns, filter expression, uniqueness, clustered flag.
- `ComparisonOptions.IgnoreKeys` / `IgnoreIndexes` flags honoured.

## [0.1.0] — M1 Walking skeleton — Table only
- `Database / Schema / Table / Column` object model.
- `TableReader` over `sys.tables` + `sys.columns` (two batched queries).
- `ComparisonEngine` table identity pairing + column diff.
- `TableScriptEmitter` for `CREATE TABLE`, `DROP TABLE`, `ALTER TABLE ADD COLUMN`.
- `dbdelta compare` CLI verb with JSON + text formatters; spec §4.3 exit codes wired.
- `LiveDbSource` `ISchemaSource` adapter over `Microsoft.Data.SqlClient`.

## [0.0.0] — M0 Repo bootstrap
- Solution scaffold (`src/` + `tests/`), Central Package Management, `Directory.Build.props`, `.editorconfig`.
- NetArchTest layering tests gating Core purity.
- GitHub Actions CI on `windows-latest` + .NET 10 SDK.
- Apache 2.0 licence, design system v1 import, M0/M1 implementation plan.
