# Tech Stack and Architectural Decisions
## SQL Compare Clone — Working Document

**Status**: Living document. Sections marked *Accepted* are committed; *Proposed* are open for revision.
**Last updated**: 2026-05-20
**Audience**: Contributors, architects, onboarding engineers.

---

## 0. Decision-Recording Format

Every decision in this document follows the lightweight ADR (Architecture Decision Record) structure:

| Field | Meaning |
|---|---|
| **Context** | Why this decision was needed; what forces are in play |
| **Options** | The realistic candidates that were evaluated |
| **Decision** | What we chose |
| **Rationale** | The key reasons; trade-off summary |
| **Consequences** | What becomes easier; what becomes harder; new obligations |
| **Status** | `Proposed` / `Accepted` / `Superseded` |

Inline citations use `[Source: <url>]` notation.

---

## 1. Inferred Redgate SQL Compare Technology Stack

This section documents our best-effort inventory of what Redgate uses internally in SQL Compare, based on public documentation, release notes, NuGet package evidence, and the v16 release notes (the most recent major version as of 2026-05).

### 1.1 Evidence Base

- SQL Compare 16 requirements page: `.NET Framework 4.7.2 or later` required on Windows
  `[Source: https://documentation.red-gate.com/sc/getting-started/requirements]`
- SQL Compare 16.0 release notes disclose the following NuGet dependencies in their breaking-changes section:
  - `Microsoft.Data.SqlClient 5.1.5` — confirmed migration away from `System.Data.SqlClient`
  - `LibGit2Sharp` (mainstream, Redgate no longer maintains a private fork)
  - `Newtonsoft.Json 13`
  - `System.Data.SQLite.Core 1.0.111`
  - `Microsoft.Build` (replacing `Microsoft.Build.Engine`)
  - MSAL (Microsoft Authentication Library) replacing deprecated ADAL
  `[Source: https://documentation.red-gate.com/sc/release-notes-and-other-versions/sql-compare-16-0-release-notes]`
- v16 ships Docker images with Alpine 3.23 base (CLI/headless), indicating the engine runs on Linux
- SmartAssembly obfuscation confirmed used across Redgate products
  `[Source: https://www.red-gate.com/products/smartassembly/]`
- Redgate Simple Talk published "Mixing WPF and WinForms" — confirming WPF+WinForms hybrid in their products
  `[Source: https://www.red-gate.com/simple-talk/development/dotnet-development/mixing-wpf-and-winforms/]`
- SQL Comparison SDK was retired; distribution is now exclusively through SQL Compare product
  `[Source: https://www.red-gate.com/products/sql-comparison-sdk/support/]`
- ScriptDOM open-sourced under MIT in 2023; version 180.18.1 current as of 2026-05
  `[Source: https://techcommunity.microsoft.com/blog/azuresqlblog/scriptdom-net-library-for-t-sql-parsing-is-now-open-source/3804284]`
  `[Source: https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom]`

### 1.2 Inferred Stack Table

| Layer | Redgate's Choice (inferred) | Confidence |
|---|---|---|
| Language | C# | Certain |
| Runtime | .NET Framework 4.7.2 (UI host); .NET Standard 2.0 engine library; Alpine Docker for CLI | High |
| UI framework | WPF (primary) + WinForms panels (legacy/embedded controls) | High |
| T-SQL parser | `Microsoft.SqlServer.TransactSql.ScriptDom` | Very High |
| SQL Server metadata | Mix of SMO and direct `sys.*` catalog queries | Medium-High |
| ADO.NET client | `Microsoft.Data.SqlClient` 5.x (migrated from `System.Data.SqlClient`) | Confirmed |
| Git integration | `LibGit2Sharp` (mainstream) | Confirmed |
| JSON serialization | `Newtonsoft.Json` 13 | Confirmed |
| Auth (cloud) | MSAL | Confirmed |
| Obfuscation | SmartAssembly | High |
| Installer (Windows) | WiX Toolset (inferred from MSI distribution) | Medium |
| Telemetry | Application Insights / internal Redgate telemetry | Medium |
| Licensing | Redgate Licensing Server (in-house) | Certain |
| Credential storage | Windows Credential Manager (confirmed v16 breaking change) | Confirmed |

### 1.3 Key Observations for our Clone

1. Redgate ships the UI as a Windows-only `.NET Framework 4.7.2` host, but the engine is `netstandard2.0`-compatible — a split that enables the CLI on Linux/Alpine.
2. The confirmed migration from `System.Data.SqlClient` to `Microsoft.Data.SqlClient` in v16 is the pattern we should follow from day one.
3. LibGit2Sharp is proven in production by Redgate for reading Git source-controlled schemas — validates our choice.
4. SmartAssembly is not a concern for us (open source does not need obfuscation).
5. The retired SQL Comparison SDK means no public API contract we must match, though design compatibility eases migration for existing users.

---

## 2. Decision: Primary Language and Runtime

**Context**: We must pick a language and runtime that gives us access to the SQL Server tooling ecosystem (ScriptDOM, SMO, Microsoft.Data.SqlClient), a mature type system, cross-platform targeting, and a large contributor pool.

**Options**

| Option | Notes |
|---|---|
| **C# / .NET 8** | Native home of SQL Server tooling; first-class cross-platform; LTS until Nov 2026 |
| Rust | Excellent perf; no SQL Server ecosystem; FFI to SMO impractical |
| Go | Cross-platform; SQL Server ecosystem minimal; no ScriptDOM |
| Java | JVM SQL Server ecosystem exists (jTDS, mssql-jdbc) but ScriptDOM is .NET-only |
| Python | Scripting convenience; pyodbc; no AST-level T-SQL parser matching ScriptDOM quality |

**Decision**: C# on **.NET 8** (LTS).

**Rationale**
- ScriptDOM, SMO, and `Microsoft.Data.SqlClient` are .NET-first; no mature equivalents exist in other ecosystems.
- .NET 8 is LTS and supports Windows, Linux (x64/ARM64), and macOS natively.
- The C# contributor pool is large and familiar with tooling-style applications.
- Our engine library targets `net8.0`; the UI initially targets `net8.0-windows` (WPF) or `net8.0` (Avalonia).
- AOT compilation via .NET 8 NativeAOT is available for the CLI binary if startup time becomes a concern.

**Consequences**
- UI is initially Windows-only if we choose WPF; cross-platform requires Avalonia (Decision 6).
- SMO NuGet packages are large (~80 MB); tree-shaking is limited — consider lazy loading.
- .NET 8 EOL is November 2026; plan upgrade to .NET 10 (LTS, expected Nov 2025) before that date.

**Status**: Accepted

---

## 3. Decision: T-SQL Parser

**Context**: All schema comparison logic depends on parsing T-SQL DDL (CREATE TABLE, CREATE PROCEDURE, etc.) into a structured AST. The parser must handle every SQL Server dialect from 2014 through 2025.

**Options**

| Option | Notes |
|---|---|
| **Microsoft.SqlServer.TransactSql.ScriptDom** | Ships with SSMS; open-sourced MIT 2023; maintained by Microsoft |
| Write our own parser | Multi-year effort; incorrect grammar causes silent schema drift |
| ANTLR4 T-SQL grammar | Community grammar available; significant effort to maintain; lags new syntax |
| TSqlParser (DacFx) | DacFx exposes some parsing; less accessible AST than ScriptDOM |

**Decision**: `Microsoft.SqlServer.TransactSql.ScriptDom` (NuGet `Microsoft.SqlServer.TransactSql.ScriptDom`).

**Rationale**
- Same parser embedded in SSMS and Azure Data Studio — highest SQL Server dialect fidelity available.
- MIT-licensed since April 2023; source on GitHub at `microsoft/SqlScriptDOM`.
  `[Source: https://github.com/microsoft/SqlScriptDOM]`
- Version-gated parsers (`TSql160Parser`, `TSql170Parser`) map directly to SQL Server major versions.
- Handles T-SQL quirks (collation-sensitive identifiers, `GO` batch separators, variable declarations) that hand-rolled parsers miss.
- DacFx itself uses ScriptDOM internally — same ground truth as Microsoft's own comparison tooling.

**Consequences**
- Coupled to Microsoft's release cadence; new SQL Server 2025+ syntax features may lag 3-6 months.
- The NuGet package is ~15 MB; acceptable.
- We do not own the AST shape; breaking AST changes across major versions require adaptation shims.
- Mitigation: abstract AST traversal behind our own visitor interface so parser version bumps are contained.

**Status**: Accepted

---

## 4. Decision: SQL Server Metadata Access

**Context**: To compare schemas we need to read the full DDL of every object (tables, indexes, constraints, views, procedures, functions, types, etc.) from a live SQL Server instance or a snapshot.

**Options**

| Option | Notes |
|---|---|
| **Pure SMO** | High-level OO model; `Scripter` class generates DDL; slow at scale |
| **Pure catalog queries** (`sys.*`) | Fast; full control; requires hand-crafting DDL reconstruction |
| **Hybrid** | Catalog queries for enumeration + metadata; SMO for complex DDL serialization |

**Decision**: **Hybrid approach** — direct `sys.*` catalog queries as the primary path; SMO as a fallback for specific object types where DDL reconstruction is prohibitively complex.

**Rationale**
- At 10,000-object schemas, SMO `Scripter` is prohibitively slow (minutes vs seconds) because it issues one round-trip per object by default.
- Catalog queries allow batched retrieval and parallelism by object kind.
- SMO is appropriate for object types with very complex DDL (e.g., full-text catalogs, Service Broker objects) where reconstructing DDL from catalog is high-risk.
- Redgate itself does not rely on SMO exclusively; their v16 engine is `netstandard2.0`-compatible and ships without SMO in Docker — implying catalog-first approach for the engine.

**Consequences**
- We own version-feature gating (e.g., temporal tables appeared in SQL 2016; graph tables in SQL 2017).
- More code: each object kind needs its own query + DDL reconstruction.
- Better performance and testability (queries are mockable; SMO requires a live connection).
- SMO usage paths must be clearly bounded and documented.

**Status**: Accepted, with measured exceptions for: full-text catalogs, Service Broker, and external data sources.

---

## 5. Decision: Dependency on SMO NuGet Packages

**Context**: SMO is distributed via NuGet (`Microsoft.SqlServer.SqlManagementObjects`). It is heavy (~80 MB of native + managed assemblies) and has version-compatibility concerns with older SQL Server instances.

**Options**

| Option | Notes |
|---|---|
| Take full SMO dependency | Simple; large binary footprint |
| No SMO at all | Eliminates fallback path for complex DDL types |
| **SMO as optional/lazy plugin** | Load only when needed for edge-case object types |

**Decision**: SMO referenced only in `MyCompare.Providers` as an optional dependency, loaded lazily via `AssemblyLoadContext` if the user encounters a SMO-backed code path.

**Rationale**: Keeps the core engine and CLI lean; SMO is not needed for the 95% case.

**Consequences**: Adds plugin-loading complexity; must test SMO version compatibility matrix.

**Status**: Proposed

---

## 6. Decision: UI Framework

**Context**: SQL Compare is a desktop GUI application with a rich diff viewer, object tree, script pane, and connection manager. We need a UI framework that: (a) supports the XAML component model developers know, (b) works cross-platform if we want Linux/macOS GUI, (c) is actively maintained.

**Options**

| Option | Platform | Notes |
|---|---|---|
| **WPF** | Windows only | Mature; large ecosystem; identical to Redgate's choice; no Linux/macOS |
| **Avalonia** | Win + Linux + macOS | Near-identical XAML dialect; Skia-rendered; growing ecosystem |
| WinUI 3 | Windows only | Modern; packaging constraints; no Linux/macOS |
| MAUI | Win + macOS + mobile | Not suited to dense data-grid tooling; weak desktop story |
| Electron + web frontend | All platforms | 200+ MB overhead; C# interop layer needed |

**Path A — WPF (Windows-only, pragmatic)**
- Fastest time to working UI.
- Identical component model to Redgate's product — eases competitive feature comparison.
- No Linux/macOS GUI; headless CLI covers those platforms.
- Appropriate if the GUI user base is predominantly Windows (true today for SQL Compare's market).

**Path B — Avalonia (cross-platform)**
- Avalonia uses XAML syntax intentionally close to WPF; migration guides exist.
  `[Source: https://avaloniaui.net/blog/from-wpf-to-avalonia-a-guide-for-net-developers-exploring-cross-platform-ui-frameworks]`
- Skia rendering is pixel-perfect across platforms.
- Community is growing; JetBrains uses Avalonia for Rider's UI rendering.
- Avalonia XPF is a commercial product that allows running unmodified WPF code cross-platform — an upgrade path if we start with WPF.

**Decision**: **Avalonia** for the GUI project (`MyCompare.Ui.Avalonia`), targeting `net8.0`.

**Rationale**
- We want the clone to run the GUI on macOS for developers using Apple Silicon — a practical need in 2026.
- Avalonia's WPF-compatible XAML means WPF knowledge transfers directly.
- Starting cross-platform avoids a painful WPF→Avalonia migration later.
- The CLI covers Linux server use cases regardless of UI choice.

**Consequences**
- Smaller community than WPF; some third-party controls (e.g., data grids) have fewer options — AvaloniaUI's built-in DataGrid or `Actipro` are the main picks.
- Skia rendering differences are cosmetic; no functional impact.
- Learning curve for team members unfamiliar with Avalonia's `ReactiveUI` + `IObservable` data-binding idiom.

**Status**: Proposed (revisit if Avalonia DataGrid proves insufficient for 10k-row diff views)

**Fallback**: If Avalonia proves problematic before v1 ships, downgrade to WPF in `MyCompare.Ui.Wpf`. The engine, providers, and CLI are UI-agnostic.

---

## 7. Decision: CLI Framework

**Context**: The CLI is a first-class deliverable (`MyCompare.Cli`) for headless use in CI/CD pipelines. It must support subcommands, options, arguments, async execution, and tab completion.

**Options**

| Option | Notes |
|---|---|
| **System.CommandLine** | Microsoft-maintained; async-native; middleware pipeline |
| Spectre.Console.Cli | Beautiful output; opinionated; actively maintained |
| Hand-rolled | Full control; maintenance burden |
| Mono.Options | Old; no async; no subcommand nesting |

**Decision**: `System.CommandLine` (NuGet `System.CommandLine`) as the parsing backbone, with `Spectre.Console` for rich output rendering (tables, progress bars, colors).
`[Source: https://learn.microsoft.com/en-us/dotnet/standard/commandline/]`

**Rationale**
- System.CommandLine 2.0.x (currently beta-5) is the Microsoft-blessed path; stable release aligns with .NET 10 (Nov 2025). Using it now in beta-5 is acceptable given its maturity.
- Spectre.Console complements rather than competes — it handles rendering while System.CommandLine handles parsing.
  `[Source: https://anthonysimmon.com/beautiful-interactive-console-apps-with-system-commandline-and-spectre-console/]`
- Both are .NET Foundation-backed and actively maintained.

**Consequences**
- System.CommandLine 2.0 stable may introduce breaking API changes before our v1 — pin the NuGet version and test on upgrade.
- Spectre.Console requires terminal capabilities; detect `NO_COLOR` and `--no-ansi` flags for CI environments.

**Status**: Accepted

---

## 8. Decision: ADO.NET Client Library

**Context**: All SQL Server connections — for metadata reads, script execution, and deployment — go through an ADO.NET client.

**Options**

| Option | Notes |
|---|---|
| `System.Data.SqlClient` | In maintenance mode since 2021; no new features |
| **`Microsoft.Data.SqlClient`** | Active development; AAD/Entra auth; Always Encrypted; connection resiliency |

**Decision**: `Microsoft.Data.SqlClient` (NuGet `Microsoft.Data.SqlClient`), minimum version 5.x.

**Rationale**
- Redgate confirmed migration to `Microsoft.Data.SqlClient 5.1.5` in SQL Compare v16 breaking changes — validates this choice.
- `Microsoft.Data.SqlClient` supports Azure Active Directory / Entra ID authentication, Always Encrypted, MFA — all needed for modern SQL Server targets.
- `System.Data.SqlClient` is .NET Framework only and receives security patches only.

**Consequences**
- Some SQL Server 2008/2008 R2 targets may have issues; those versions are also being dropped by Redgate.
- Namespace change from `System.Data.SqlClient` to `Microsoft.Data.SqlClient` must be applied consistently.

**Status**: Accepted

---

## 9. Decision: Git Integration

**Context**: One of SQL Compare's key source providers is a Git-controlled folder of migration scripts or schema scripts (e.g., a Flyway project). We need to read branch history, check out commits, and read file trees without requiring a Git installation on the host.

**Options**

| Option | Notes |
|---|---|
| **LibGit2Sharp** | .NET bindings to libgit2; no git CLI dependency; confirmed used by Redgate |
| Spawn `git` CLI | Zero library dependency; requires git installed; output parsing is fragile |
| Octokit | GitHub API only; not a local repo library |

**Decision**: `LibGit2Sharp` (NuGet `LibGit2Sharp`), pinned to 0.31.x or later.
`[Source: https://www.nuget.org/packages/LibGit2Sharp]`

**Rationale**
- Redgate confirmed use of the mainstream `LibGit2Sharp` package (not their private fork) in v16 release notes.
- No git CLI dependency eases deployment in restricted CI environments.
- Active maintenance; MIT license.

**Consequences**
- libgit2 native binaries are bundled per-platform (win-x64, linux-x64, osx-arm64, etc.); adds ~3 MB per RID.
- On macOS ARM64, ensure native binary availability; test explicitly.
- Operations we need: `Repository.Lookup`, `Commit.Tree`, `Branch.Tip` — all supported.
- Alternative path if libgit2 native bindings prove unreliable on a platform: spawn `git` CLI behind a `IGitProvider` interface abstraction.

**Status**: Accepted

---

## 10. Decision: Installer and Distribution

**Context**: We need to distribute the tool to Windows developers (GUI + CLI) and Linux/macOS engineers (CLI-only). We want low friction for first-time install and automated upgrade.

**Options (Windows)**

| Option | Notes |
|---|---|
| **WiX Toolset v4 (MSI)** | Industry standard; Authenticode-signable; group policy support |
| NSIS | Older; less structured |
| InnoSetup | Simpler; not MSI |
| ClickOnce | Auto-update built-in; limited customization |

**Options (Linux/macOS)**

| Option | Notes |
|---|---|
| **Self-contained zip / tarball** | Simplest; always works |
| .deb / .rpm | Native Linux package managers; add maintenance |
| Homebrew formula | macOS standard; requires tap or PR to homebrew-core |
| winget / Chocolatey | Windows package managers; supplement MSI |

**Decision**:
- **Windows**: WiX Toolset v4 MSI + winget manifest + Chocolatey package + signed ZIP.
  `[Source: https://wixtoolset.org/]`
- **Linux**: Self-contained tarball + `.deb` + `.rpm` (via `dotnet publish --sc`).
- **macOS**: Self-contained tarball + Homebrew tap.
- **NuGet SDK**: `MyCompare.Sdk` published to NuGet.org for programmatic use.

**Rationale**
- WiX v4 is the de facto tool for authoring MSI packages in the .NET ecosystem; it integrates with GitHub Actions.
- winget and Chocolatey reach developers without requiring admin rights for the package manager itself.
- Self-contained publish (`dotnet publish -r linux-x64 --sc`) avoids .NET runtime installation requirement on CI agents.

**Consequences**
- WiX authoring has a learning curve; budget 1-2 weeks for first MSI.
- Signing requires an EV certificate or a GitHub Actions secret with a code-signing cert.
- Multi-RID publish artifacts (~70 MB each for self-contained) must be hosted — use GitHub Releases.

**Status**: Proposed

---

## 11. Decision: Serialization Formats

**Context**: SQL Compare uses three file types: project file (`.scp`), snapshot file (schema snapshot), and filter/options file. We must choose formats that are human-readable where practical and compact where schema size demands it.

### 11.1 Project File (.scp)

**Decision**: XML, following Redgate convention.

**Rationale**: XML project files are diffable in Git, readable by humans, and the Redgate `.scp` format is XML — compatibility eases migration. JSON would work equally well but breaks compatibility with existing `.scp` files users may wish to use.

**Consequences**: Verbose for large filter lists; acceptable given project files are small.

### 11.2 Snapshot File (.snap)

**Decision**: Binary format — versioned header + LZ4-compressed JSON payload.

**Rationale**: A 10,000-object schema snapshot in uncompressed JSON is ~50-200 MB. LZ4 achieves ~5:1 compression in <10ms. The binary envelope allows fast header reads (schema version, SQL Server version, timestamp) without deserializing the full payload. The inner payload remains JSON so it is inspectable with `lz4cat | jq`.

**Consequences**: Not human-readable directly; provide a `mycompare snapshot export --format json` command. Requires a versioned migration path for snapshot format changes.

### 11.3 Filter File (.scpf)

**Decision**: XML, matching Redgate convention.

**Status**: Accepted for project and filter files; Proposed for snapshot format (binary envelope detail).

---

## 12. Decision: Logging

**Context**: We need structured logging for both the interactive GUI (log to file), the CLI (log to stderr + file), and eventual telemetry integration.

**Options**: Serilog / NLog / Microsoft.Extensions.Logging only / log4net

**Decision**: **Serilog** with the following sinks:
- `Serilog.Sinks.Console` — colored for CLI
- `Serilog.Sinks.File` (rolling daily) — for diagnostic logs
- `Serilog.Sinks.Seq` — optional; used in development for structured query

`[Source: https://serilog.net/]`

**Rationale**: Serilog is the dominant structured-logging library in the .NET ecosystem. Its sink model cleanly separates output concerns. `ILogger<T>` from `Microsoft.Extensions.Logging` is used in library code so consumers can swap implementations.

**Consequences**: All library projects (`MyCompare.Engine`, `MyCompare.Providers`) depend only on `Microsoft.Extensions.Logging.Abstractions`. Serilog is wired up only in the host projects (CLI, UI).

**Status**: Accepted

---

## 13. Decision: Testing Strategy

**Context**: Schema comparison is a correctness-critical domain. A silent difference in generated DDL can cause data loss in production. The test suite must catch regressions in the diff engine, script generator, and SQL Server metadata readers.

### 13.1 Unit Tests

**Decision**: xUnit.net + FluentAssertions.
- xUnit for test isolation model (constructor DI, no static state).
- FluentAssertions for readable assertion messages.

### 13.2 Integration Tests (SQL Server)

**Decision**: `Testcontainers.MsSql` — spins up `mcr.microsoft.com/mssql/server:2022-latest` per test class.
`[Source: https://dotnet.testcontainers.org/modules/mssql/]`

**Rationale**: No dependency on a developer-maintained local SQL Server instance. Tests are deterministic and run in CI on Linux. The container is destroyed after each test session.

**Consequences**: Requires Docker on developer machine and CI runner. Tests are slower (~30s container startup) — isolate in `*.IntegrationTests` project and run separately from unit tests in CI.

### 13.3 Golden-File (Snapshot) Tests

**Decision**: For the script generator, maintain a `testdata/golden/` directory of expected DDL outputs. Each test creates a schema, generates a script, and diffs against the golden file. A `--update-golden` flag regenerates on intentional changes.

**Rationale**: Script generation regressions are caught immediately; reviewers see the exact diff of what SQL changes.

### 13.4 Property-Based Tests

**Decision**: FsCheck for diff engine invariants, e.g.:
- "Applying the generated script to the source produces a schema equal to the target."
- "Diffing A against A always produces zero changes."

**Consequences**: FsCheck requires custom generators for T-SQL AST nodes — non-trivial but high-value.

### 13.5 Target Coverage

- Engine: 90% line coverage minimum.
- Providers: 80% (integration tests supplement unit tests).
- CLI: 70% (integration tests via process spawn).
- UI: not tracked via line coverage; use Playwright for smoke tests.

**Status**: Accepted (unit + integration + golden); Proposed (property tests — phase 2).

---

## 14. Decision: CI/CD Pipeline

**Context**: We need automated build, test, and release pipelines that run on every PR and produce signed, reproducible release artifacts.

**Decision**: GitHub Actions with the following workflow structure:

```
.github/workflows/
  ci.yml            — PR: build + unit tests (matrix: windows-latest, ubuntu-latest)
  integration.yml   — PR + main: integration tests (ubuntu-latest, Docker)
  release.yml       — tag push: build + sign + publish artifacts
  nightly.yml       — nightly: full golden-file + property tests
```

**Matrix**: `[windows-latest, ubuntu-latest]` for CI; `macos-latest` on release builds only (slow).

**Signing**:
- Windows MSI: Authenticode via `signtool.exe` using a GitHub Actions secret-stored PFX.
- NuGet packages: signed with `dotnet nuget sign` + Sigstore `cosign` for supply-chain transparency.

**Rationale**: GitHub Actions is free for open-source; native support for matrix builds; well-integrated with `gh` CLI and release management.

**Consequences**: Signed releases require a code-signing certificate; EV certificate costs ~$300-500/yr. Alternative: use Sigstore only (free, but less recognized by enterprise AV).

**Status**: Accepted (GitHub Actions structure); Proposed (signing approach — revisit when approaching v1 release).

---

## 15. Decision: Packaging and Distribution Channels

**Context** (see also Decision 10): How do end users discover and install the tool?

**Decision**:

| Channel | Audience | Artifact |
|---|---|---|
| GitHub Releases | All | Signed ZIP (all platforms), MSI (Windows) |
| winget | Windows developers | `winget install MyCompare` |
| Chocolatey | Windows ops/CI | `choco install mycompare` |
| Homebrew tap | macOS developers | `brew install mycompare/tap/mycompare` |
| .deb / .rpm | Linux sysadmins | Direct download + apt/yum repo |
| NuGet.org | Developers using SDK | `dotnet add package MyCompare.Sdk` |

**SDK versioning**: Strict semver. Major version bumps on any public API break.

**Status**: Proposed

---

## 16. Decision: Telemetry

**Context**: Usage telemetry helps prioritize features. Users are rightly suspicious of phone-home behavior in developer tools.

**Decision**:
- Telemetry is **off by default**.
- Users explicitly opt in via `mycompare config set telemetry.enabled true` or a first-run prompt.
- Implementation: OpenTelemetry SDK → OTLP exporter → our hosted Grafana/Tempo instance.
  `[Source: https://opentelemetry.io/docs/languages/net/]`

**What we collect (when opted in)**:
- Feature invocations (e.g., "compare executed", "deploy executed") — no schema names, no SQL.
- Error types and frequencies (no stack traces with user data).
- Platform + .NET version.
- Tool version.

**What we never collect**:
- Database names, server names, or connection strings.
- Schema object names.
- Generated SQL scripts.

**Consequences**: Requires infrastructure to receive OTLP; Grafana Cloud free tier is sufficient initially.

**Status**: Proposed

---

## 17. Decision: Licensing of the Clone

**Context**: We must choose a license that balances open collaboration, protection against proprietary forks, and commercial viability.

**Options**

| License | Notes |
|---|---|
| **Apache 2.0** | Permissive; patent grant; allows commercial use; widely understood |
| MIT | Simpler; no patent grant |
| AGPL v3 | Copyleft; requires source disclosure for SaaS use; deters some adopters |
| Source-available (BSL) | Read-only until 4-year change date; used by HashiCorp |
| Commercial + free tier | Complex; discourages community contributions |

**Decision**: **Apache License 2.0** for all code in this repository.

**Rationale**:
- Apache 2.0 includes an explicit patent grant — important for tooling that touches Microsoft's SQL Server DDL parsing territory.
- Permissive licenses attract more contributors and integrations.
- If commercial revenue is needed, a separate "Enterprise" distribution can be built on top without license conflict.
- Competitors (DacFx, SqlPackage) are MIT/Apache; our choice should not be more restrictive.

**Consequences**:
- Anyone can fork and build a commercial product without sharing changes.
- Mitigation: build a strong community and roadmap — the best moat is velocity, not license restriction.

**Status**: Proposed (legal review recommended before first public release)

---

## 18. Decision: Project Structure (Monorepo Layout)

**Context**: The codebase spans multiple concerns — engine, providers, CLI, UI, SDK, tests. We need a structure that is navigable, enforces separation of concerns, and keeps build times reasonable.

**Decision**: Single Git repository, multi-project .NET solution.

```
SQL Compare/
  src/
    MyCompare.Engine/           # Core diff algorithm + script generator
    MyCompare.Engine.Abstractions/  # Public interfaces + domain models (no deps)
    MyCompare.Providers/        # SQL Server, Git, file-system source providers
    MyCompare.Providers.SqlServer/  # SQL Server-specific metadata reader
    MyCompare.Providers.Git/    # LibGit2Sharp-backed Git provider
    MyCompare.Cli/              # CLI entry point (System.CommandLine)
    MyCompare.Ui.Avalonia/      # Avalonia GUI entry point
    MyCompare.Sdk/              # NuGet-published public SDK
  tests/
    MyCompare.Engine.Tests/
    MyCompare.Engine.IntegrationTests/
    MyCompare.Providers.SqlServer.Tests/
    MyCompare.Providers.SqlServer.IntegrationTests/
    MyCompare.Cli.Tests/
    MyCompare.Golden/           # Golden-file test data
  docs/
  scripts/                      # Build, sign, publish scripts
  .github/
    workflows/
```

**Dependency rules** (enforced by `dotnet-arch-guard` or manual PR review):
- `*.Abstractions` → no dependencies (except BCL)
- `Engine` → `Abstractions`, `ScriptDom`
- `Providers.*` → `Abstractions`, `Microsoft.Data.SqlClient`, `LibGit2Sharp` (where applicable)
- `Cli` → `Engine`, `Providers.*`, `System.CommandLine`, `Spectre.Console`
- `Ui.Avalonia` → `Engine`, `Providers.*`, `Avalonia.*`
- `Sdk` → `Abstractions` only (re-exports engine types)

**Status**: Accepted

---

## 19. Decision: Dependency Injection

**Context**: The application has multiple pluggable components (source providers, diff strategies, script formatters). We need a DI container.

**Decision**: `Microsoft.Extensions.DependencyInjection` (built into .NET; no extra NuGet required).

**Rationale**: Zero-cost (bundled with .NET 8), well-documented, supports keyed services (.NET 8+), and is the de facto standard. No need for Autofac or Unity for our scope.

**Consequences**: Keyed services (`IKeyedServiceProvider`) used to register multiple `ISchemaProvider` implementations (SqlServer, Git, Snapshot). Resolving by key name is idiomatic in .NET 8.

**Status**: Accepted

---

## 20. Decision: Async and Threading Model

**Context**: Schema reading is I/O-bound (many SQL round-trips). Diff computation is CPU-bound. Script generation involves both.

**Decision**:

| Phase | Strategy |
|---|---|
| Schema reading | `Task.WhenAll` across object kinds (tables, views, procs, etc.) per schema; parallel by schema when comparing two live databases |
| Diff computation | Single-threaded initially; `Parallel.ForEach` partitioned by object kind when profiling shows bottleneck |
| Script generation | Streaming — use `IAsyncEnumerable<ScriptStatement>` so caller can pipe to file/network without materializing full script |
| Script execution (deploy) | Sequential by dependency order; `GO` batches sent individually via `SqlCommand.ExecuteNonQueryAsync` |

**Rationale**: Parallel schema reads are safe (read-only); parallel diff is safe (pure function); sequential deploy is required to respect referential dependency order and transactional semantics.

**Consequences**: `CancellationToken` must be threaded through every async path — enforce via code review checklist.

**Status**: Accepted

---

## 21. Decision: Error Handling Strategy

**Context**: Error handling in a schema comparison tool spans: expected domain errors (object not found, permission denied), infrastructure errors (network timeout, SQL Server unavailable), and programming errors (null reference, invariant violation).

**Decision**:

| Error class | Handling |
|---|---|
| Domain errors | Typed `Result<T, CompareError>` — use a lightweight custom `Result<T, TError>` struct (no external dependency required for a simple 2-case discriminated union) |
| Infrastructure errors | Caught at module boundary (`ISchemaProvider` implementations); rethrown as `CompareException` with error code + remediation hint |
| User input errors | Validated at CLI/UI boundary; returned as `ValidationResult` with actionable message |
| Programming errors | Let exceptions propagate; catch at top-level and log with stack trace |

**Alternative considered**: `LanguageExt.Core` for richer functional types (Option, Either, Fin).
`[Source: https://github.com/louthy/language-ext]`
Rejected at v1 because: heavyweight dependency; steep learning curve; `Result<T, E>` alone does not justify the full LanguageExt dependency. Revisit if we expand functional patterns.

**Consequences**: Custom `Result<T, TError>` is ~30 lines; test alongside the types that use it. All public APIs in `MyCompare.Engine.Abstractions` must not throw for expected errors.

**Status**: Accepted

---

## 22. Decision: Configuration

**Context**: The tool has user-scoped settings (credentials, preferences), per-project settings (comparison options, filter), and per-machine policy (proxy, telemetry opt-out for enterprise).

**Decision**:

| Scope | Location | Format |
|---|---|---|
| User settings | `%APPDATA%\MyCompare\settings.json` (Win); `~/.config/mycompare/settings.json` (Linux/macOS) | JSON |
| Per-project | `.scp` alongside the compared script folder, or explicit path | XML |
| Machine policy | `<install_dir>\policy.json` (optional; read-only) | JSON |
| Credentials | Windows Credential Manager (Win); macOS Keychain; libsecret (Linux) | Native secure store |

**Rationale**: Credentials must never be stored in plain-text files (confirmed by SQL Compare v16 breaking change: removed password storage from project files). `Microsoft.Extensions.Configuration` used to layer these sources.

**Status**: Accepted

---

## 23. Decision: Internationalization

**Context**: Localizing developer tooling is expensive and primarily serves markets with strong non-English SQL Server user bases.

**Decision**: **English only at v1.** All user-facing strings live in `Resources.resx` files (not inline) so localization is possible later without code changes.

**Consequences**: Contributors must add strings to resx, not hardcode them. A linter (`Meziantou.Analyzer` rule `MA0074`) can enforce this.

**Status**: Accepted

---

## 24. Decision: Performance Budgets

**Context**: Performance expectations must be explicit so we can design the right data structures from the start and detect regressions early.

| Scenario | Budget | Notes |
|---|---|---|
| Connect + read 10k-object schema | < 30s on developer workstation | Parallel reads; single SQL Server |
| Diff two 10k-object schemas | < 5s | Pure CPU; no I/O |
| Generate deploy script (10k-object diff) | Streaming, first byte < 2s | `IAsyncEnumerable`; no full materialization |
| CLI startup (cold) | < 500ms | AOT compile if needed |
| Snapshot load from disk | < 3s for 100 MB snapshot | LZ4 decompress + JSON parse |

**Benchmarking**: `BenchmarkDotNet` added to `MyCompare.Engine.Benchmarks` project; runs in nightly CI and results stored as GitHub Actions artifacts for trend analysis.

**Status**: Accepted (budgets are targets, not hard failures in CI until v1 beta)

---

## 25. Decision: Security Posture

**Context**: SQL Compare handles database credentials and generates deployment SQL. Security failures here can cause data breaches or production outages.

**Decision**:

| Concern | Mitigation |
|---|---|
| Credential storage | Windows Credential Manager / macOS Keychain / libsecret — NEVER plaintext files |
| Connection strings in project files | Exclude password; reference credential store by name |
| Network | Default connection encryption ON (`Encrypt=true`) matching SQL Compare v16 default |
| Telemetry | Off by default; no PII or schema data ever sent |
| Supply chain | Signed NuGet packages; Sigstore provenance on GitHub Releases; dependency scanning via `dotnet-ossindex` in CI |
| Generated SQL | Warn if deploy script contains TRUNCATE or DROP operations; require `--confirm-destructive` flag |
| Binary signing | Authenticode for MSI + EXE; Sigstore for cross-platform artifacts |
| Input validation | Validate all user-provided SQL identifiers against whitelist pattern before use in dynamic SQL; use parameterized queries for all metadata catalog reads where parameters are applicable |

**Status**: Accepted (design-level); individual controls reviewed in security audit before v1.

---

## 26. Risk Register

Top 10 technical risks, in descending priority.

| # | Risk | Probability | Impact | Mitigation |
|---|---|---|---|---|
| 1 | ScriptDOM version mismatch — new SQL Server syntax not yet in the pinned ScriptDOM version causes parse failures | Medium | High | Pin ScriptDOM to latest stable; subscribe to `SqlScriptDOM` GitHub releases; wrap in version-gated fallback |
| 2 | Catalog query correctness — hand-crafted DDL reconstruction diverges from actual SQL Server behavior for edge-case constraints | Medium | High | Golden-file tests against a live SQL Server; fuzzing with ScriptDOM round-trip validation |
| 3 | Avalonia maturity gap — a required UI component (diff viewer, split-pane editor) is not available or buggy | Low-Medium | Medium | Evaluate AvaloniaUI DataGrid + custom diff renderer in a prototype sprint before full UI work |
| 4 | LibGit2Sharp native binary failure on macOS ARM64 | Low | High | Maintain `IGitProvider` abstraction with a CLI-spawn fallback; test on Apple Silicon in CI |
| 5 | .NET 8 EOL (Nov 2026) — requires migration to .NET 10 before shelf | Certain | Low | Plan .NET 10 upgrade in Q3 2026; .NET 10 is LTS and upgrade is straightforward |
| 6 | Performance regression in diff engine — O(n²) naively matching objects | Medium | High | Use hash-keyed dictionaries for O(1) object lookup; benchmark from day one |
| 7 | Docker/Testcontainers flakiness in CI | Medium | Low | Use `Testcontainers.MsSql` retry logic; set `TESTCONTAINERS_RYUK_DISABLED=false` |
| 8 | WiX v4 authoring complexity delays Windows release | Medium | Medium | Start WiX early; consider NSIS as fallback for initial release |
| 9 | Licensing ambiguity — using Apache 2.0 while redistributing MIT/Apache dependencies | Low | Medium | Track all transitive licenses via `dotnet-project-licenses` in CI; no GPL/LGPL transitive deps |
| 10 | ScriptDOM AST breaking changes on major version bump | Low | Medium | Abstract AST traversal behind visitor interfaces in `MyCompare.Engine`; do not leak ScriptDOM types into `*.Abstractions` |

---

## 27. Open Questions

Items not yet decided; each needs a spike or team decision before implementation.

| # | Question | Owner | Target |
|---|---|---|---|
| OQ-1 | Should we support SQL Azure / Azure SQL Managed Instance as a source in v1, or defer to v1.1? Azure SQL has catalog differences (no Agent jobs, filegroups differ). | Architecture | Sprint 2 |
| OQ-2 | Diff algorithm: naive keyed match by object name, or Myers/histogram diff of normalized DDL text? Keyed match is correct for schema objects; text diff is needed for procedure body comparison. | Engine team | Sprint 3 |
| OQ-3 | Avalonia vs WPF — run a 2-day prototype to validate Avalonia DataGrid performance with 10k rows before committing. | UI lead | Sprint 1 |
| OQ-4 | Should the snapshot format include column statistics / row counts for data-mapping hinting? Adds size; useful for impact analysis. | Product | v1.1 |
| OQ-5 | Do we support `.dacpac` as a source? DacFx is MIT; reading DACPAC would cover SQL Project users. | Architecture | Sprint 4 |
| OQ-6 | Flyway migration script folder format: do we handle out-of-order migrations in v1? | Engine team | Sprint 3 |
| OQ-7 | Telemetry backend: self-hosted Grafana Cloud (free tier) vs a commercial observability SaaS? | DevOps | v1 beta |
| OQ-8 | How do we handle SQL Server instances requiring Kerberos/Windows auth in a Linux CLI context? `Microsoft.Data.SqlClient` supports Kerberos via `Integrated Security=true` on Linux but requires gssapi config. | Platform | Sprint 5 |
| OQ-9 | Public SDK API stability: do we commit to semver-stable public SDK from v1.0, or release SDK as `0.x` until API stabilizes? | Architecture | Before v1 RC |
| OQ-10 | Should we adopt `CommunityToolkit.Mvvm` for the Avalonia MVVM layer, or use `ReactiveUI` which is Avalonia's canonical recommendation? | UI lead | Sprint 1 |

---

## Appendix A: Key NuGet Package Reference

| Package | NuGet ID | Version Floor | License | Role |
|---|---|---|---|---|
| ScriptDOM | `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.x | MIT | T-SQL AST parsing |
| ADO.NET | `Microsoft.Data.SqlClient` | 5.2.x | MIT | SQL Server connectivity |
| Git | `LibGit2Sharp` | 0.31.x | MIT | Git source provider |
| CLI parsing | `System.CommandLine` | 2.0.0-beta5 | MIT | CLI framework |
| CLI rendering | `Spectre.Console` | 0.49.x | MIT | Terminal UI |
| UI | `Avalonia` | 11.x | MIT | Cross-platform XAML UI |
| Logging | `Serilog` | 3.x | Apache 2.0 | Structured logging |
| DI | `Microsoft.Extensions.DependencyInjection` | 8.x | MIT | IoC container |
| Unit tests | `xunit` | 2.8.x | Apache 2.0 | Test runner |
| Assertions | `FluentAssertions` | 6.x | Apache 2.0 | Readable assertions |
| Integration tests | `Testcontainers.MsSql` | 4.x | MIT | SQL Server in Docker |
| Benchmarks | `BenchmarkDotNet` | 0.14.x | MIT | Perf regression detection |
| Compression | `K4os.Compression.LZ4` | 1.3.x | MIT | Snapshot compression |
| Auth | `Microsoft.Identity.Client` (MSAL) | 4.x | MIT | Azure/Entra auth |

---

## Appendix B: Rejected Technologies and Rationale

| Technology | Why Rejected |
|---|---|
| `System.Data.SqlClient` | Maintenance-only; superseded by `Microsoft.Data.SqlClient` |
| Autofac / Unity DI | Unnecessary complexity vs built-in `Microsoft.Extensions.DependencyInjection` |
| WPF (as primary UI) | Windows-only; Avalonia provides equivalent API with cross-platform support |
| MAUI | Desktop story immature for dense data-grid tooling; mobile target not needed |
| ANTLR4 T-SQL grammar | Community grammar lags SQL Server releases; inferior to ScriptDOM fidelity |
| Pure SMO for metadata | Too slow at scale; version-compatibility friction |
| `Newtonsoft.Json` (new code) | Use `System.Text.Json` for new serialization; `Newtonsoft.Json` only if required by a transitive dependency |
| ADAL (Azure auth) | Deprecated; replaced by MSAL — matches Redgate's own v16 migration |
| LanguageExt.Core | Heavyweight for v1; custom `Result<T, E>` is sufficient; revisit if functional patterns expand |

---

*This document is the authoritative record of technology choices for the SQL Compare clone project. Update it when decisions change; mark superseded decisions with `Status: Superseded by Decision N`.*
