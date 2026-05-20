# DbDelta — M0 + M1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land a fully bootstrapped DbDelta repository (M0) plus an end-to-end walking skeleton (M1) that compares two live SQL Server databases for **tables only** (schemas + tables + columns), generates `CREATE TABLE` / `ALTER TABLE ADD COLUMN` DDL, and surfaces the comparison results in both a CLI and a minimal Blazor Hybrid GUI.

**Architecture:** Hexagonal — pure `Core` library (no I/O) wraps the object model, diff engine, and script generator behind `ISchemaSource` / `IDeploymentExecutor` ports; `Providers.LiveDb` adapter implements catalog reads via `sys.*` queries with `Microsoft.Data.SqlClient`; `Cli` and `App` are thin hosts. Architectural layering enforced via `NetArchTest.Rules` test that runs in CI.

**Tech Stack:** .NET 10, C# 14, Central Package Management, xUnit v3, FluentAssertions, AutoFixture, Verify.Xunit, Testcontainers.MsSql, bUnit, FsCheck, NetArchTest.Rules, Microsoft.Data.SqlClient 6.x, Microsoft.SqlServer.TransactSql.ScriptDom 180.x, System.CommandLine 2.x, Spectre.Console 0.49.x, Microsoft.AspNetCore.Components.WebView.WindowsForms 10.x, Microsoft.Web.WebView2, Serilog 4.x, GitHub Actions (windows-latest runner).

**Design System:** The App's UI is built on the **DbDelta Design System v1.0** that lives at `docs/design-system/`. The system ships its own `tokens.css` (oklch palette: Cobalt primary, Violet secondary, warm-neutral grays, light + dark themes), `base.css` (reset + typography + utilities), `components-ui.css` (`.btn`, `.input`, `.badge`, `.alert`, …), and `components-domain.css` (`.app-shell`, `.connection-bar`, `.dgrid`, `.source-card`, …). The App copies these assets into `src/DbDelta.App/wwwroot/assets/` and consumes them from `wwwroot/index.html`. Razor components use the design system's class names; do **not** invent new style classes — extend the system in `docs/design-system/` and copy forward instead.

---

## Reference: Spec Sections This Plan Implements

| Spec section | Plan task(s) |
|--------------|--------------|
| §1.2 In scope — Schema, Table, Column subset (skeleton) | T1.1–T1.13 |
| §1.4 Guardrails — Core has no I/O; NetArchTest in CI | T0.10, T0.13 |
| §2.1 Source tree layout | T0.3–T0.9 |
| §2.2 Dependency direction | T0.13 |
| §2.3 Latest stable deps via Directory.Packages.props | T0.2 |
| §3.1 Compare flow (skeleton path) | T1.10 |
| §4 Error model — Result<T, Error> | T1.3 |
| §5.1 Test pyramid (Core unit + Provider integration) | T1.4, T1.7, T1.9 |
| §5.4 CI gates | T0.10–T0.12 |
| DbDelta Design System v1.0 — tokens + components | T1.11a, T1.12, T1.13 |

---

## File Structure Map

This is the final state at end of M1. Each task explicitly creates or modifies a subset.

```
DbDelta/                                            (repo root, already has LICENSE + .gitignore + docs/)
├─ .editorconfig                                    T0.4
├─ .github/
│  └─ workflows/
│     └─ ci.yml                                     T0.11
├─ Directory.Build.props                            T0.2
├─ Directory.Packages.props                         T0.2
├─ DbDelta.sln                                      T0.3
├─ global.json                                      T0.1
├─ README.md                                        T0.12
├─ src/
│  ├─ DbDelta.Core/
│  │  ├─ DbDelta.Core.csproj                        T0.3
│  │  ├─ ObjectModel/
│  │  │  ├─ Database.cs                             T1.1
│  │  │  ├─ Schema.cs                               T1.1
│  │  │  ├─ Table.cs                                T1.1
│  │  │  └─ Column.cs                               T1.1
│  │  ├─ Diff/
│  │  │  ├─ DifferenceStatus.cs                     T1.2
│  │  │  ├─ DifferencePair.cs                       T1.2
│  │  │  ├─ ComparisonResult.cs                     T1.2
│  │  │  └─ ComparisonEngine.cs                     T1.2
│  │  ├─ Abstractions/
│  │  │  ├─ ISchemaSource.cs                        T1.3
│  │  │  └─ Result.cs                               T1.3
│  │  ├─ Options/
│  │  │  └─ ComparisonOptions.cs                    T1.1
│  │  └─ ScriptGen/
│  │     ├─ IScriptEmitter.cs                       T1.6
│  │     ├─ TableScriptEmitter.cs                   T1.6
│  │     └─ ScriptGenerator.cs                      T1.6
│  ├─ DbDelta.Shared/
│  │  ├─ DbDelta.Shared.csproj                      T0.3
│  │  └─ Dtos/
│  │     ├─ ComparisonResultDto.cs                  T1.11
│  │     ├─ DifferenceDto.cs                        T1.11
│  │     └─ Mapper.cs                               T1.11
│  ├─ DbDelta.Providers.LiveDb/
│  │  ├─ DbDelta.Providers.LiveDb.csproj            T0.3
│  │  ├─ LiveDbSource.cs                            T1.4
│  │  ├─ ConnectionFactory.cs                       T1.4
│  │  └─ Readers/
│  │     ├─ SchemaReader.cs                         T1.4
│  │     └─ TableReader.cs                          T1.5
│  ├─ DbDelta.Persistence/
│  │  └─ DbDelta.Persistence.csproj                 T0.3   (placeholder, M11 scope)
│  ├─ DbDelta.Cli/
│  │  ├─ DbDelta.Cli.csproj                         T0.3
│  │  ├─ Program.cs                                 T1.8
│  │  ├─ Commands/
│  │  │  └─ CompareCommand.cs                       T1.9
│  │  ├─ Output/
│  │  │  └─ JsonFormatter.cs                        T1.10
│  │  └─ ExitCodes.cs                               T1.8
│  └─ DbDelta.App/
│     ├─ DbDelta.App.csproj                         T0.3 (asset import: T1.11a)
│     ├─ Program.cs                                 T1.12
│     ├─ MainForm.cs                                T1.12
│     ├─ App.razor                                  T1.12
│     ├─ _Imports.razor                             T1.12
│     ├─ wwwroot/
│     │  ├─ index.html                              T1.12
│     │  └─ assets/                                 T1.11a
│     │     ├─ tokens.css                           (copied from docs/design-system)
│     │     ├─ base.css                             (copied)
│     │     ├─ components-ui.css                    (copied)
│     │     ├─ components-domain.css                (copied)
│     │     ├─ app.js                               (copied)
│     │     └─ logo.svg                             (copied)
│     ├─ Components/
│     │  ├─ ConnectionPicker.razor                  T1.13
│     │  └─ ResultsTree.razor                       T1.13
│     └─ State/
│        └─ AppState.cs                             T1.13
└─ tests/
   ├─ DbDelta.Core.UnitTests/
   │  ├─ DbDelta.Core.UnitTests.csproj              T0.3
   │  ├─ ObjectModel/
   │  │  └─ TableTests.cs                           T1.1
   │  ├─ Diff/
   │  │  └─ ComparisonEngineTests.cs                T1.2
   │  └─ ScriptGen/
   │     └─ TableScriptEmitterTests.cs              T1.6
   ├─ DbDelta.Architecture.Tests/
   │  ├─ DbDelta.Architecture.Tests.csproj          T0.3
   │  └─ LayeringTests.cs                           T0.13
   ├─ DbDelta.Providers.LiveDb.IntegrationTests/
   │  ├─ DbDelta.Providers.LiveDb.IntegrationTests.csproj   T0.3
   │  ├─ LiveDbFixture.cs                           T1.7
   │  └─ TableReaderTests.cs                        T1.7
   ├─ DbDelta.ScriptGen.GoldenTests/
   │  ├─ DbDelta.ScriptGen.GoldenTests.csproj       T0.3
   │  ├─ TableGoldenTests.cs                        T1.6
   │  └─ snapshots/                                 (Verify-managed)
   ├─ DbDelta.Cli.AcceptanceTests/
   │  ├─ DbDelta.Cli.AcceptanceTests.csproj         T0.3
   │  └─ CompareCommandTests.cs                     T1.11
   └─ DbDelta.App.ComponentTests/
      ├─ DbDelta.App.ComponentTests.csproj          T0.3
      └─ ResultsTreeTests.cs                        T1.13
```

---

## M0 — Repo Bootstrap (Tasks T0.1 – T0.13)

### Task T0.1: Pin .NET 10 SDK via global.json

**Files:**
- Create: `global.json`

- [ ] **Step 1: Write global.json**

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

- [ ] **Step 2: Verify SDK resolves**

Run: `dotnet --version`
Expected: prints `10.0.100` (or higher feature band).

- [ ] **Step 3: Commit**

```bash
git add global.json
git commit -m "build: pin .NET 10 SDK via global.json"
```

---

### Task T0.2: Add Directory.Build.props + Directory.Packages.props (Central Package Management)

**Files:**
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`

- [ ] **Step 1: Write Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
    <Authors>DbDelta contributors</Authors>
    <Company>DbDelta</Company>
    <Product>DbDelta</Product>
    <Copyright>Copyright (c) DbDelta contributors. Licensed under Apache 2.0.</Copyright>
    <RepositoryUrl>https://github.com/GitBakko/db-delta</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write Directory.Packages.props**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup Label="Core runtime">
    <PackageVersion Include="Microsoft.Data.SqlClient" Version="6.0.1" />
    <PackageVersion Include="Microsoft.SqlServer.TransactSql.ScriptDom" Version="180.0.0" />
    <PackageVersion Include="Serilog" Version="4.2.0" />
    <PackageVersion Include="Serilog.Sinks.Console" Version="6.0.0" />
    <PackageVersion Include="Serilog.Sinks.File" Version="6.0.0" />
    <PackageVersion Include="Polly" Version="8.5.0" />
  </ItemGroup>
  <ItemGroup Label="CLI">
    <PackageVersion Include="System.CommandLine" Version="2.0.0-beta5" />
    <PackageVersion Include="Spectre.Console" Version="0.49.1" />
  </ItemGroup>
  <ItemGroup Label="App / Blazor Hybrid">
    <PackageVersion Include="Microsoft.AspNetCore.Components.WebView.WindowsForms" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Web.WebView2" Version="1.0.2792.45" />
  </ItemGroup>
  <ItemGroup Label="Persistence">
    <PackageVersion Include="Meziantou.Framework.Win32.CredentialManager" Version="1.6.1" />
  </ItemGroup>
  <ItemGroup Label="Test runtime">
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="xunit.v3" Version="1.0.0" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.0" />
    <PackageVersion Include="FluentAssertions" Version="7.0.0" />
    <PackageVersion Include="AutoFixture" Version="4.18.1" />
    <PackageVersion Include="AutoFixture.Xunit3" Version="4.18.1" />
    <PackageVersion Include="Verify.Xunit" Version="28.5.0" />
    <PackageVersion Include="Testcontainers.MsSql" Version="4.1.0" />
    <PackageVersion Include="bunit" Version="1.34.0" />
    <PackageVersion Include="FsCheck.Xunit" Version="3.0.0" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageVersion Include="BenchmarkDotNet" Version="0.14.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Commit**

```bash
git add Directory.Build.props Directory.Packages.props
git commit -m "build: add Directory.Build.props + Central Package Management"
```

---

### Task T0.3: Create solution + 7 src csproj + 6 test csproj

**Files:**
- Create: `DbDelta.sln`
- Create: `src/DbDelta.Core/DbDelta.Core.csproj`
- Create: `src/DbDelta.Shared/DbDelta.Shared.csproj`
- Create: `src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj`
- Create: `src/DbDelta.Persistence/DbDelta.Persistence.csproj`
- Create: `src/DbDelta.Cli/DbDelta.Cli.csproj`
- Create: `src/DbDelta.App/DbDelta.App.csproj`
- Create: `tests/DbDelta.Core.UnitTests/DbDelta.Core.UnitTests.csproj`
- Create: `tests/DbDelta.Architecture.Tests/DbDelta.Architecture.Tests.csproj`
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/DbDelta.Providers.LiveDb.IntegrationTests.csproj`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/DbDelta.ScriptGen.GoldenTests.csproj`
- Create: `tests/DbDelta.Cli.AcceptanceTests/DbDelta.Cli.AcceptanceTests.csproj`
- Create: `tests/DbDelta.App.ComponentTests/DbDelta.App.ComponentTests.csproj`

- [ ] **Step 1: Scaffold solution + projects**

Run from repo root:

```bash
dotnet new sln -n DbDelta
dotnet new classlib  -n DbDelta.Core                          -o src/DbDelta.Core
dotnet new classlib  -n DbDelta.Shared                        -o src/DbDelta.Shared
dotnet new classlib  -n DbDelta.Providers.LiveDb              -o src/DbDelta.Providers.LiveDb
dotnet new classlib  -n DbDelta.Persistence                   -o src/DbDelta.Persistence
dotnet new console   -n DbDelta.Cli                           -o src/DbDelta.Cli
dotnet new winforms  -n DbDelta.App                           -o src/DbDelta.App
dotnet new xunit     -n DbDelta.Core.UnitTests                -o tests/DbDelta.Core.UnitTests
dotnet new xunit     -n DbDelta.Architecture.Tests            -o tests/DbDelta.Architecture.Tests
dotnet new xunit     -n DbDelta.Providers.LiveDb.IntegrationTests   -o tests/DbDelta.Providers.LiveDb.IntegrationTests
dotnet new xunit     -n DbDelta.ScriptGen.GoldenTests         -o tests/DbDelta.ScriptGen.GoldenTests
dotnet new xunit     -n DbDelta.Cli.AcceptanceTests           -o tests/DbDelta.Cli.AcceptanceTests
dotnet new xunit     -n DbDelta.App.ComponentTests            -o tests/DbDelta.App.ComponentTests
```

Delete the auto-generated `Class1.cs` files in each `classlib` project and `UnitTest1.cs` in each xunit project — they will be replaced by actual code in later tasks.

- [ ] **Step 2: Patch `DbDelta.App.csproj` for Blazor Hybrid**

Open `src/DbDelta.App/DbDelta.App.csproj` and ensure it looks like this:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>DbDelta.App</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebView.WindowsForms" />
    <PackageReference Include="Microsoft.Web.WebView2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\DbDelta.Core\DbDelta.Core.csproj" />
    <ProjectReference Include="..\DbDelta.Shared\DbDelta.Shared.csproj" />
    <ProjectReference Include="..\DbDelta.Providers.LiveDb\DbDelta.Providers.LiveDb.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Patch `DbDelta.Providers.LiveDb.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\DbDelta.Core\DbDelta.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Patch `DbDelta.Cli.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>dbdelta</AssemblyName>
    <RootNamespace>DbDelta.Cli</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.CommandLine" />
    <PackageReference Include="Spectre.Console" />
    <PackageReference Include="Serilog" />
    <PackageReference Include="Serilog.Sinks.Console" />
    <PackageReference Include="Serilog.Sinks.File" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\DbDelta.Core\DbDelta.Core.csproj" />
    <ProjectReference Include="..\DbDelta.Providers.LiveDb\DbDelta.Providers.LiveDb.csproj" />
    <ProjectReference Include="..\DbDelta.Persistence\DbDelta.Persistence.csproj" />
    <ProjectReference Include="..\DbDelta.Shared\DbDelta.Shared.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Patch `DbDelta.Shared.csproj` and `DbDelta.Core.csproj`**

`DbDelta.Shared.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\DbDelta.Core\DbDelta.Core.csproj" />
  </ItemGroup>
</Project>
```

`DbDelta.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.SqlServer.TransactSql.ScriptDom" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Patch test project csprojs**

For every `tests/*/<Name>.csproj`, ensure this layout (adjust ProjectReference per project):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
</Project>
```

For `DbDelta.Architecture.Tests.csproj` also add: `<PackageReference Include="NetArchTest.Rules" />` and a `ProjectReference` to every src project.

For `DbDelta.Providers.LiveDb.IntegrationTests.csproj` add: `<PackageReference Include="Testcontainers.MsSql" />` and `ProjectReference` to `DbDelta.Providers.LiveDb`.

For `DbDelta.ScriptGen.GoldenTests.csproj` add: `<PackageReference Include="Verify.Xunit" />` and `ProjectReference` to `DbDelta.Core`.

For `DbDelta.App.ComponentTests.csproj` change SDK to `Microsoft.NET.Sdk.Razor`, target `net10.0-windows`, add `<PackageReference Include="bunit" />`, and `ProjectReference` to `DbDelta.App`.

For `DbDelta.Cli.AcceptanceTests.csproj` add `<PackageReference Include="Testcontainers.MsSql" />`, and reference `DbDelta.Cli` plus a `<None Include="..\..\src\DbDelta.Cli\$(OutDir)\dbdelta.exe">` so tests can spawn the CLI binary.

- [ ] **Step 7: Add all projects to the solution**

```bash
dotnet sln DbDelta.sln add src/**/*.csproj tests/**/*.csproj
```

- [ ] **Step 8: Verify build**

```bash
dotnet restore
dotnet build
```

Expected: build succeeds with 0 errors (warnings as errors enforced).

- [ ] **Step 9: Commit**

```bash
git add DbDelta.sln src/ tests/
git commit -m "build: scaffold solution + 7 src and 6 test csproj"
```

---

### Task T0.4: Add `.editorconfig`

**Files:**
- Create: `.editorconfig`

- [ ] **Step 1: Write `.editorconfig`**

```ini
root = true

[*]
indent_style = space
indent_size = 4
charset = utf-8
end_of_line = crlf
insert_final_newline = true
trim_trailing_whitespace = true

[*.{yml,yaml,json,md}]
indent_size = 2

[*.cs]
# Style
dotnet_style_qualification_for_field = false:warning
dotnet_style_qualification_for_property = false:warning
dotnet_style_qualification_for_method = false:warning
dotnet_style_qualification_for_event = false:warning
dotnet_style_predefined_type_for_locals_parameters_members = true:warning
dotnet_style_predefined_type_for_member_access = true:warning
csharp_new_line_before_open_brace = all
csharp_prefer_braces = true:warning
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_style_var_for_built_in_types = false:warning
csharp_style_namespace_declarations = file_scoped:warning
csharp_style_expression_bodied_methods = when_on_single_line:suggestion

# Analyzers
dotnet_analyzer_diagnostic.category-Style.severity = warning
dotnet_diagnostic.CA1062.severity = warning
dotnet_diagnostic.IDE0058.severity = none
```

- [ ] **Step 2: Verify formatting**

```bash
dotnet format --verify-no-changes
```

Expected: exit code 0 (nothing to change yet — projects are empty).

- [ ] **Step 3: Commit**

```bash
git add .editorconfig
git commit -m "build: add .editorconfig with C# 14 style rules"
```

---

### Task T0.5: Add README.md (project description + quickstart)

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write `README.md`**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add README with quickstart + status"
```

---

### Task T0.6: Add `app.manifest` to DbDelta.App for high-DPI awareness

**Files:**
- Create: `src/DbDelta.App/app.manifest`

- [ ] **Step 1: Write `app.manifest`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="DbDelta.App"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/DbDelta.App/DbDelta.App.csproj
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.App/app.manifest
git commit -m "build(app): add Windows app manifest with per-monitor DPI awareness"
```

---

### Task T0.7: Wire Serilog bootstrap helper (Core has none — only adapters)

**Files:**
- Create: `src/DbDelta.Cli/Logging/SerilogBootstrap.cs`

- [ ] **Step 1: Write the bootstrap class**

```csharp
using System.IO;
using Serilog;
using Serilog.Events;

namespace DbDelta.Cli.Logging;

/// <summary>
/// Configures Serilog with console + rolling-file sinks for the CLI host.
/// </summary>
internal static class SerilogBootstrap
{
    public static ILogger Build(LogEventLevel minimumLevel, string? logFile)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.Console();

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            configuration = configuration.WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7);
        }

        return configuration.CreateLogger();
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/DbDelta.Cli/DbDelta.Cli.csproj
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Cli/Logging/SerilogBootstrap.cs
git commit -m "feat(cli): add Serilog bootstrap with console + rolling file sinks"
```

---

### Task T0.8: Reserve the CLI exit codes file (referenced by later tasks)

**Files:**
- Create: `src/DbDelta.Cli/ExitCodes.cs`

- [ ] **Step 1: Write exit-codes constants**

```csharp
namespace DbDelta.Cli;

/// <summary>
/// Process exit codes returned by the DbDelta CLI. See spec §4.3.
/// </summary>
internal static class ExitCodes
{
    public const int SuccessNoDifferences = 0;
    public const int SuccessDifferencesFound = 1;
    public const int ConnectionOrAuthError = 10;
    public const int InsufficientPermissions = 11;
    public const int SchemaReadFailure = 20;
    public const int ScriptGenerationFailure = 30;
    public const int UnresolvableDependencyCycle = 31;
    public const int DeploymentFailure = 40;
    public const int DeploymentCancelled = 41;
    public const int ProjectFileError = 60;
    public const int InternalError = 99;
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/DbDelta.Cli/DbDelta.Cli.csproj
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Cli/ExitCodes.cs
git commit -m "feat(cli): reserve exit code constants per spec §4.3"
```

---

### Task T0.9: Replace auto-generated Program.cs in CLI with placeholder

**Files:**
- Modify: `src/DbDelta.Cli/Program.cs`

- [ ] **Step 1: Replace the file contents**

```csharp
using DbDelta.Cli;

// Real CLI is wired in T1.8 (CompareCommand). Until then return success.
return ExitCodes.SuccessNoDifferences;
```

- [ ] **Step 2: Build and run**

```bash
dotnet build src/DbDelta.Cli/DbDelta.Cli.csproj
dotnet run --project src/DbDelta.Cli/DbDelta.Cli.csproj
echo $LASTEXITCODE   # PowerShell: $LASTEXITCODE
```

Expected: exit code 0.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Cli/Program.cs
git commit -m "feat(cli): placeholder Program.cs returning ExitCodes.SuccessNoDifferences"
```

---

### Task T0.10: Add CONTRIBUTING.md

**Files:**
- Create: `CONTRIBUTING.md`

- [ ] **Step 1: Write CONTRIBUTING.md**

```markdown
# Contributing to DbDelta

## Workflow

DbDelta follows the **Superpowers** workflow:

1. **Brainstorm** — discuss the feature, lock scope.
2. **Spec** — produce a design document in `docs/superpowers/specs/`.
3. **Plan** — break the spec into bite-sized tasks in `docs/superpowers/plans/`.
4. **Execute** — implement task-by-task with TDD discipline, one commit per step.

## Test-Driven Development

All code lands via red → green → refactor:

```bash
dotnet test --filter "FullyQualifiedName~MyNewTest"   # red
# implement
dotnet test --filter "FullyQualifiedName~MyNewTest"   # green
# refactor
dotnet test                                            # full suite still green
```

## Build & Verify

Before opening a PR:

```bash
dotnet format --verify-no-changes
dotnet build
dotnet test
```

CI re-runs the same set on `windows-latest` and additionally runs the integration suite with Testcontainers MS SQL.

## Architecture Rules

- `DbDelta.Core` has zero I/O dependencies. Enforced by `DbDelta.Architecture.Tests/LayeringTests.cs`.
- Providers implement ports from `DbDelta.Core.Abstractions`.
- Latest stable NuGet versions only — Renovate-bot opens PRs weekly.

## Commit Style

Conventional Commits: `feat:`, `fix:`, `docs:`, `chore:`, `build:`, `test:`, `refactor:`.
```

- [ ] **Step 2: Commit**

```bash
git add CONTRIBUTING.md
git commit -m "docs: add CONTRIBUTING.md with TDD workflow"
```

---

### Task T0.11: Add GitHub Actions CI workflow

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Write `.github/workflows/ci.yml`**

```yaml
name: ci

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

permissions:
  contents: read

jobs:
  build-and-test:
    runs-on: windows-latest
    timeout-minutes: 30

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/Directory.Packages.props') }}
          restore-keys: |
            ${{ runner.os }}-nuget-

      - name: Restore
        run: dotnet restore

      - name: Verify formatting
        run: dotnet format --verify-no-changes

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Run non-DB tests
        run: |
          dotnet test tests/DbDelta.Core.UnitTests --no-build --configuration Release --logger "trx;LogFileName=core.trx"
          dotnet test tests/DbDelta.Architecture.Tests --no-build --configuration Release --logger "trx;LogFileName=arch.trx"
          dotnet test tests/DbDelta.ScriptGen.GoldenTests --no-build --configuration Release --logger "trx;LogFileName=golden.trx"
          dotnet test tests/DbDelta.App.ComponentTests --no-build --configuration Release --logger "trx;LogFileName=app.trx"

      - name: Run integration tests (Testcontainers MS SQL)
        run: |
          dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --no-build --configuration Release --logger "trx;LogFileName=providers.trx"
          dotnet test tests/DbDelta.Cli.AcceptanceTests --no-build --configuration Release --logger "trx;LogFileName=cli.trx"

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results
          path: '**/TestResults/*.trx'
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add GitHub Actions workflow (windows-latest, dotnet 10)"
```

---

### Task T0.12: Push M0 progress and verify CI green

**Files:** none

- [ ] **Step 1: Push**

```bash
git push origin main
```

- [ ] **Step 2: Open the repo Actions tab and confirm the CI workflow ran on the latest commit and finished green**

Expected: `ci / build-and-test` shows ✅. Address any red builds before continuing.

- [ ] **Step 3 (only if CI fails): Add a fix-up commit and push again**

---

### Task T0.13: Architectural layering test (NetArchTest.Rules)

**Files:**
- Create: `tests/DbDelta.Architecture.Tests/LayeringTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace DbDelta.Architecture.Tests;

public class LayeringTests
{
    private static readonly Assembly CoreAssembly =
        Assembly.Load("DbDelta.Core");

    private static readonly Assembly ProvidersLiveDbAssembly =
        Assembly.Load("DbDelta.Providers.LiveDb");

    [Fact]
    public void Core_must_not_reference_SqlClient_or_any_io_namespace()
    {
        var forbiddenNamespaces = new[]
        {
            "Microsoft.Data.SqlClient",
            "System.Data.SqlClient",
            "System.Net.Http",
            "System.IO.Pipes",
            "Microsoft.AspNetCore"
        };

        var result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Core must remain pure (no I/O dependencies). Offenders: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Providers_LiveDb_may_reference_SqlClient_and_Core_only()
    {
        var result = Types.InAssembly(ProvidersLiveDbAssembly)
            .Should()
            .HaveDependencyOnAny("DbDelta.Core", "Microsoft.Data.SqlClient", "System")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it passes (Core is empty, so no I/O deps possible)**

```bash
dotnet test tests/DbDelta.Architecture.Tests
```

Expected: PASS — both test cases green.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Architecture.Tests/LayeringTests.cs
git commit -m "test(arch): enforce Core has no I/O deps; LiveDb provider only references SqlClient + Core"
```

---

## M1 — Walking Skeleton (Tasks T1.1 – T1.14)

### Task T1.1: ObjectModel — Database, Schema, Table, Column (Core)

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/Database.cs`
- Create: `src/DbDelta.Core/ObjectModel/Schema.cs`
- Create: `src/DbDelta.Core/ObjectModel/Table.cs`
- Create: `src/DbDelta.Core/ObjectModel/Column.cs`
- Create: `src/DbDelta.Core/Options/ComparisonOptions.cs`
- Test: `tests/DbDelta.Core.UnitTests/ObjectModel/TableTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class TableTests
{
    [Fact]
    public void Identity_combines_schema_and_name_case_sensitively_by_default()
    {
        var t1 = new Table("dbo", "Customer", []);
        var t2 = new Table("dbo", "Customer", []);
        var t3 = new Table("dbo", "customer", []);

        t1.Identity.Should().Be(t2.Identity);
        t1.Identity.Should().NotBe(t3.Identity);
    }

    [Fact]
    public void Table_holds_ordered_columns()
    {
        var cols = new[]
        {
            new Column("Id", "int", isNullable: false, ordinal: 1),
            new Column("Name", "nvarchar(100)", isNullable: false, ordinal: 2)
        };

        var table = new Table("dbo", "Customer", cols);

        table.Columns.Should().HaveCount(2);
        table.Columns[0].Name.Should().Be("Id");
        table.Columns[1].Name.Should().Be("Name");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~TableTests"
```

Expected: FAIL — `Table` / `Column` types not defined.

- [ ] **Step 3: Write the production code**

`src/DbDelta.Core/ObjectModel/Database.cs`:

```csharp
using System.Collections.Generic;

namespace DbDelta.Core.ObjectModel;

/// <summary>
/// The root of an in-memory schema graph: a single SQL Server database snapshot.
/// </summary>
public sealed record Database(
    string Name,
    IReadOnlyList<Schema> Schemas,
    IReadOnlyList<Table> Tables);
```

`src/DbDelta.Core/ObjectModel/Schema.cs`:

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server schema (namespace for objects).
/// </summary>
public sealed record Schema(string Name)
{
    public ObjectIdentity Identity => new(SchemaName: Name, ObjectName: string.Empty, Kind: "Schema");
}
```

`src/DbDelta.Core/ObjectModel/Table.cs`:

```csharp
using System.Collections.Generic;

namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A user table (sys.tables row).
/// </summary>
public sealed record Table(
    string Schema,
    string Name,
    IReadOnlyList<Column> Columns)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Table");
}

/// <summary>
/// Tuple identifying an object across two schemas being compared.
/// </summary>
public readonly record struct ObjectIdentity(string SchemaName, string ObjectName, string Kind);
```

`src/DbDelta.Core/ObjectModel/Column.cs`:

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A table column (sys.columns row) — minimal v1 property surface.
/// </summary>
public sealed record Column(
    string Name,
    string DataType,
    bool IsNullable,
    int Ordinal,
    string? DefaultExpression = null,
    bool IsIdentity = false);
```

`src/DbDelta.Core/Options/ComparisonOptions.cs`:

```csharp
using System;

namespace DbDelta.Core.Options;

/// <summary>
/// Bitmap of comparison toggles. v1 covers the most-used 20 options;
/// later milestones add the remaining flags from spec §1.2.
/// </summary>
[Flags]
public enum ComparisonOptions
{
    None = 0,
    IgnoreWhitespace = 1 << 0,
    IgnoreComments = 1 << 1,
    IgnoreCollations = 1 << 2,
    IgnoreFillFactor = 1 << 3,
    IgnoreConstraintNames = 1 << 4,
    IgnorePermissions = 1 << 5,
    IgnoreUserSettings = 1 << 6,
    CaseSensitiveObjectDefinition = 1 << 7,
    IgnoreIndexes = 1 << 8,
    IgnoreKeys = 1 << 9,
    IgnoreStatistics = 1 << 10,
    IgnoreTriggers = 1 << 11,
    IgnoreWithElementOrder = 1 << 12,
    IgnoreFileGroups = 1 << 13,
    IgnoreIdentitySeed = 1 << 14,
    IgnoreUsersPermissionsAndRoleMemberships = 1 << 15,
    NoTransactions = 1 << 16,
    ForceColumnOrder = 1 << 17,
    ThrowOnFileParseFailed = 1 << 18,
    DoNotOutputCommentHeader = 1 << 19,

    /// <summary>
    /// The defaults Redgate ships, mirrored: ignore whitespace, comments,
    /// fill factor, permissions, statistics.
    /// </summary>
    Default = IgnoreWhitespace | IgnoreComments | IgnoreFillFactor
            | IgnorePermissions | IgnoreStatistics,
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~TableTests"
```

Expected: PASS — 2 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ObjectModel src/DbDelta.Core/Options tests/DbDelta.Core.UnitTests/ObjectModel
git commit -m "feat(core): add Database/Schema/Table/Column + ComparisonOptions bitmap"
```

---

### Task T1.2: Diff — DifferencePair, ComparisonResult, ComparisonEngine

**Files:**
- Create: `src/DbDelta.Core/Diff/DifferenceStatus.cs`
- Create: `src/DbDelta.Core/Diff/DifferencePair.cs`
- Create: `src/DbDelta.Core/Diff/ComparisonResult.cs`
- Create: `src/DbDelta.Core/Diff/ComparisonEngine.cs`
- Test: `tests/DbDelta.Core.UnitTests/Diff/ComparisonEngineTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ComparisonEngineTests
{
    private static Database DbWith(params Table[] tables) =>
        new("X", Schemas: [new Schema("dbo")], Tables: tables);

    [Fact]
    public void Identical_tables_produce_zero_differences()
    {
        var cols = new[] { new Column("Id", "int", false, 1) };
        var a = DbWith(new Table("dbo", "Customer", cols));
        var b = DbWith(new Table("dbo", "Customer", cols));

        var result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1)
            .And.OnlyContain(d => d.Status == DifferenceStatus.Identical);
    }

    [Fact]
    public void Missing_table_on_target_yields_OnlyInA()
    {
        var cols = new[] { new Column("Id", "int", false, 1) };
        var a = DbWith(new Table("dbo", "Customer", cols));
        var b = DbWith();

        var result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1);
        result.Differences[0].Status.Should().Be(DifferenceStatus.OnlyInA);
        result.Differences[0].Identity.ObjectName.Should().Be("Customer");
    }

    [Fact]
    public void Extra_table_on_target_yields_OnlyInB()
    {
        var cols = new[] { new Column("Id", "int", false, 1) };
        var a = DbWith();
        var b = DbWith(new Table("dbo", "Customer", cols));

        var result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1);
        result.Differences[0].Status.Should().Be(DifferenceStatus.OnlyInB);
    }

    [Fact]
    public void Different_column_set_yields_Different()
    {
        var a = DbWith(new Table("dbo", "Customer", [
            new Column("Id", "int", false, 1)
        ]));
        var b = DbWith(new Table("dbo", "Customer", [
            new Column("Id", "int", false, 1),
            new Column("Email", "nvarchar(200)", true, 2)
        ]));

        var result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1);
        result.Differences[0].Status.Should().Be(DifferenceStatus.Different);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ComparisonEngineTests"
```

Expected: FAIL — `ComparisonEngine`, `DifferenceStatus`, etc. not defined.

- [ ] **Step 3: Write the production code**

`src/DbDelta.Core/Diff/DifferenceStatus.cs`:

```csharp
namespace DbDelta.Core.Diff;

/// <summary>
/// Three-state classification of a single object pairing. See spec §3 and §5.
/// </summary>
public enum DifferenceStatus
{
    Identical,
    Different,
    OnlyInA,
    OnlyInB,
}
```

`src/DbDelta.Core/Diff/DifferencePair.cs`:

```csharp
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Diff;

/// <summary>
/// One paired (or unpaired) object between sides A and B of a comparison.
/// </summary>
public sealed record DifferencePair(
    ObjectIdentity Identity,
    DifferenceStatus Status,
    object? SideA,
    object? SideB);
```

`src/DbDelta.Core/Diff/ComparisonResult.cs`:

```csharp
using System.Collections.Generic;

namespace DbDelta.Core.Diff;

/// <summary>
/// Outcome of running <see cref="ComparisonEngine.Compare"/>.
/// </summary>
public sealed record ComparisonResult(IReadOnlyList<DifferencePair> Differences);
```

`src/DbDelta.Core/Diff/ComparisonEngine.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Core.Diff;

/// <summary>
/// Pure comparison engine: pair objects by identity and classify their status.
/// </summary>
public sealed class ComparisonEngine
{
    public ComparisonResult Compare(Database a, Database b, ComparisonOptions options)
    {
        var aByIdentity = a.Tables.ToDictionary(t => t.Identity);
        var bByIdentity = b.Tables.ToDictionary(t => t.Identity);
        var allIdentities = new HashSet<ObjectIdentity>(aByIdentity.Keys);
        allIdentities.UnionWith(bByIdentity.Keys);

        var pairs = new List<DifferencePair>(allIdentities.Count);
        foreach (var id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out var sideA);
            bByIdentity.TryGetValue(id, out var sideB);
            var status = ClassifyTable(sideA, sideB, options);
            pairs.Add(new DifferencePair(id, status, sideA, sideB));
        }

        return new ComparisonResult(pairs);
    }

    private static DifferenceStatus ClassifyTable(Table? a, Table? b, ComparisonOptions options)
    {
        if (a is null && b is not null) return DifferenceStatus.OnlyInB;
        if (a is not null && b is null) return DifferenceStatus.OnlyInA;
        if (a is null || b is null) return DifferenceStatus.Identical; // both null — impossible

        return ColumnsEqual(a.Columns, b.Columns, options)
            ? DifferenceStatus.Identical
            : DifferenceStatus.Different;
    }

    private static bool ColumnsEqual(
        IReadOnlyList<Column> ax,
        IReadOnlyList<Column> bx,
        ComparisonOptions options)
    {
        if (ax.Count != bx.Count) return false;
        var bByName = bx.ToDictionary(c => c.Name);
        foreach (var col in ax)
        {
            if (!bByName.TryGetValue(col.Name, out var other)) return false;
            if (col.DataType != other.DataType) return false;
            if (col.IsNullable != other.IsNullable) return false;
            if (col.IsIdentity != other.IsIdentity) return false;
            if ((col.DefaultExpression ?? "") != (other.DefaultExpression ?? "")) return false;

            // ForceColumnOrder option: also require ordinal match
            if (options.HasFlag(ComparisonOptions.ForceColumnOrder) && col.Ordinal != other.Ordinal)
                return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ComparisonEngineTests"
```

Expected: PASS — 4 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Diff tests/DbDelta.Core.UnitTests/Diff
git commit -m "feat(core): add ComparisonEngine with table-level identity pairing + column diff"
```

---

### Task T1.3: Abstractions — ISchemaSource port + Result<T, Error>

**Files:**
- Create: `src/DbDelta.Core/Abstractions/Result.cs`
- Create: `src/DbDelta.Core/Abstractions/ISchemaSource.cs`

- [ ] **Step 1: Write the failing test**

`tests/DbDelta.Core.UnitTests/Abstractions/ResultTests.cs`:

```csharp
using DbDelta.Core.Abstractions;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Abstractions;

public class ResultTests
{
    [Fact]
    public void Success_carries_value_and_no_error()
    {
        var r = Result<int>.Success(42);

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
        r.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_carries_error_and_no_value()
    {
        var r = Result<int>.Failure(new Error(ErrorCode.CannotConnect, "boom"));

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.CannotConnect);
        r.Error.Message.Should().Be("boom");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ResultTests"
```

Expected: FAIL — types not defined.

- [ ] **Step 3: Write the production code**

`src/DbDelta.Core/Abstractions/Result.cs`:

```csharp
using System;

namespace DbDelta.Core.Abstractions;

/// <summary>
/// Strongly-typed domain errors used at port boundaries (see spec §4).
/// </summary>
public enum ErrorCode
{
    CannotConnect,
    AuthFailed,
    DbNotFound,
    InsufficientPermissions,
    UnsupportedSqlServerVersion,
    EncryptedObjectUnreadable,
    CatalogQueryFailed,
    NoComparableObjects,
    UnresolvableDependencyCycle,
    DataPreservationImpossible,
    UnsupportedSchemaChange,
    BatchExecutionFailed,
    TransactionAborted,
    CancelledByUser,
    ProjectFileCorrupt,
    ProjectFileVersionUnsupported,
    InternalError,
}

public sealed record Error(ErrorCode Code, string Message, string? Remediation = null);

public sealed record Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(Value!) : onFailure(Error!);
}
```

`src/DbDelta.Core/Abstractions/ISchemaSource.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Abstractions;

/// <summary>
/// Port for reading a complete <see cref="Database"/> object graph from
/// any source (live DB, scripts folder, snapshot, ...).
/// </summary>
public interface ISchemaSource
{
    string DisplayName { get; }
    Task<Result<Database>> LoadAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ResultTests"
```

Expected: PASS — 2 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Abstractions tests/DbDelta.Core.UnitTests/Abstractions
git commit -m "feat(core): add Result<T>/Error model + ISchemaSource port"
```

---

### Task T1.4: LiveDb provider — ConnectionFactory + SchemaReader

**Files:**
- Create: `src/DbDelta.Providers.LiveDb/ConnectionFactory.cs`
- Create: `src/DbDelta.Providers.LiveDb/Readers/SchemaReader.cs`
- Create: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs`

- [ ] **Step 1: Write the connection factory**

`src/DbDelta.Providers.LiveDb/ConnectionFactory.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb;

/// <summary>
/// Thin wrapper over <see cref="SqlConnection"/> that opens a connection
/// with cancellation support.
/// </summary>
internal static class ConnectionFactory
{
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
```

- [ ] **Step 2: Write the schema reader**

`src/DbDelta.Providers.LiveDb/Readers/SchemaReader.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads non-system schemas from <c>sys.schemas</c>.
/// </summary>
internal sealed class SchemaReader
{
    private const string Query = """
        SELECT s.name AS SchemaName
        FROM sys.schemas s
        WHERE s.principal_id != 4 -- exclude sys
          AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
          AND s.name NOT LIKE 'db[_]%'
        ORDER BY s.name;
        """;

    public async Task<IReadOnlyList<Schema>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        var schemas = new List<Schema>();
        await using var command = new SqlCommand(Query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            schemas.Add(new Schema(reader.GetString(0)));
        }
        return schemas;
    }
}
```

- [ ] **Step 3: Write the LiveDbSource (table reader stub for now — populated in T1.5)**

`src/DbDelta.Providers.LiveDb/LiveDbSource.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using DbDelta.Providers.LiveDb.Readers;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb;

/// <summary>
/// Live SQL Server <see cref="ISchemaSource"/>. Reads via direct sys.* catalog queries.
/// </summary>
public sealed class LiveDbSource : ISchemaSource
{
    private readonly string _connectionString;

    public LiveDbSource(string connectionString, string? displayName = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        var builder = new SqlConnectionStringBuilder(connectionString);
        DisplayName = displayName ?? $"{builder.DataSource}/{builder.InitialCatalog}";
    }

    public string DisplayName { get; }

    public async Task<Result<Database>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await ConnectionFactory.OpenAsync(_connectionString, cancellationToken);
            var schemas = await new SchemaReader().ReadAsync(connection, cancellationToken);
            var tables = await new TableReader().ReadAsync(connection, cancellationToken);
            var dbName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            return Result<Database>.Success(new Database(dbName, schemas, tables));
        }
        catch (SqlException ex) when (ex.Number is 4060 or 18456)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.AuthFailed,
                ex.Message,
                "Verify credentials and that the user has CONNECT permission on the database."));
        }
        catch (SqlException ex) when (ex.Number is 53 or -2)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.CannotConnect,
                ex.Message,
                "Verify server name, network connectivity, and firewall rules."));
        }
        catch (SqlException ex)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.CatalogQueryFailed,
                ex.Message));
        }
    }
}
```

- [ ] **Step 4: Build (TableReader still missing — expect failure)**

```bash
dotnet build src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj
```

Expected: FAIL — `TableReader` type does not exist yet.

- [ ] **Step 5: Commit the in-progress code (T1.5 will green it)**

```bash
git add src/DbDelta.Providers.LiveDb
git commit -m "feat(providers/livedb): scaffold ConnectionFactory + SchemaReader + LiveDbSource (TableReader pending)"
```

---

### Task T1.5: LiveDb provider — TableReader

**Files:**
- Create: `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs`

- [ ] **Step 1: Write `TableReader`**

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads user tables and their columns in a single round-trip per server.
/// </summary>
internal sealed class TableReader
{
    private const string TablesQuery = """
        SELECT
            s.name      AS SchemaName,
            t.name      AS TableName,
            t.object_id AS ObjectId
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name;
        """;

    private const string ColumnsQuery = """
        SELECT
            c.object_id        AS ObjectId,
            c.name             AS ColumnName,
            TYPE_NAME(c.user_type_id) AS TypeName,
            c.max_length       AS MaxLength,
            c.precision        AS Precision,
            c.scale            AS Scale,
            c.is_nullable      AS IsNullable,
            c.is_identity      AS IsIdentity,
            dc.definition      AS DefaultExpression,
            c.column_id        AS Ordinal
        FROM sys.columns AS c
        INNER JOIN sys.tables AS t ON t.object_id = c.object_id
        LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id
                                              AND dc.parent_column_id = c.column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;
        """;

    public async Task<IReadOnlyList<Table>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        // 1. Build a map of objectId -> (schema, name)
        var tableShells = new Dictionary<int, (string Schema, string Name)>();
        await using (var tablesCmd = new SqlCommand(TablesQuery, connection))
        await using (var reader = await tablesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var schemaName = reader.GetString(0);
                var tableName  = reader.GetString(1);
                var objectId   = reader.GetInt32(2);
                tableShells[objectId] = (schemaName, tableName);
            }
        }

        // 2. Fetch all columns and group by object id
        var columnsByObjectId = new Dictionary<int, List<Column>>();
        await using (var columnsCmd = new SqlCommand(ColumnsQuery, connection))
        await using (var reader = await columnsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var objectId       = reader.GetInt32(0);
                var columnName     = reader.GetString(1);
                var typeName       = reader.GetString(2);
                var maxLength      = reader.GetInt16(3);
                var precision      = reader.GetByte(4);
                var scale          = reader.GetByte(5);
                var isNullable     = reader.GetBoolean(6);
                var isIdentity     = reader.GetBoolean(7);
                var defaultExpr    = reader.IsDBNull(8) ? null : reader.GetString(8);
                var ordinal        = reader.GetInt32(9);

                if (!tableShells.ContainsKey(objectId)) continue;

                if (!columnsByObjectId.TryGetValue(objectId, out var list))
                {
                    list = new List<Column>();
                    columnsByObjectId[objectId] = list;
                }
                list.Add(new Column(
                    Name: columnName,
                    DataType: FormatDataType(typeName, maxLength, precision, scale),
                    IsNullable: isNullable,
                    Ordinal: ordinal,
                    DefaultExpression: defaultExpr,
                    IsIdentity: isIdentity));
            }
        }

        // 3. Build the table list
        var tables = new List<Table>(tableShells.Count);
        foreach (var kv in tableShells)
        {
            columnsByObjectId.TryGetValue(kv.Key, out var cols);
            tables.Add(new Table(kv.Value.Schema, kv.Value.Name, cols ?? new List<Column>()));
        }
        return tables;
    }

    private static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
    {
        return typeName switch
        {
            "nvarchar" or "nchar" => maxLength == -1
                ? $"{typeName}(max)"
                : $"{typeName}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1
                ? $"{typeName}(max)"
                : $"{typeName}({maxLength})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
            _ => typeName,
        };
    }
}
```

- [ ] **Step 2: Build now succeeds**

```bash
dotnet build src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj
```

Expected: build green.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/TableReader.cs
git commit -m "feat(providers/livedb): TableReader reads sys.tables + sys.columns + defaults in two queries"
```

---

### Task T1.6: Core script generator — TableScriptEmitter + golden tests

**Files:**
- Create: `src/DbDelta.Core/ScriptGen/IScriptEmitter.cs`
- Create: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`
- Create: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`
- Test: `tests/DbDelta.Core.UnitTests/ScriptGen/TableScriptEmitterTests.cs`
- Test: `tests/DbDelta.ScriptGen.GoldenTests/TableGoldenTests.cs`

- [ ] **Step 1: Write the failing unit test**

```csharp
using System.Linq;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

public class TableScriptEmitterTests
{
    [Fact]
    public void Emit_create_for_OnlyInA_table()
    {
        var table = new Table("dbo", "Customer", new[]
        {
            new Column("Id", "int", false, 1, IsIdentity: true),
            new Column("Name", "nvarchar(100)", false, 2),
            new Column("Email", "nvarchar(200)", true, 3),
        });
        var pair = new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null);

        var sql = new TableScriptEmitter().Emit(pair).Trim();

        sql.Should().StartWith("CREATE TABLE [dbo].[Customer]");
        sql.Should().Contain("[Id] int IDENTITY NOT NULL");
        sql.Should().Contain("[Name] nvarchar(100) NOT NULL");
        sql.Should().Contain("[Email] nvarchar(200) NULL");
    }

    [Fact]
    public void Emit_drop_for_OnlyInB_table()
    {
        var table = new Table("dbo", "Legacy", new[]
        {
            new Column("Id", "int", false, 1),
        });
        var pair = new DifferencePair(table.Identity, DifferenceStatus.OnlyInB, null, table);

        var sql = new TableScriptEmitter().Emit(pair).Trim();

        sql.Should().Be("DROP TABLE [dbo].[Legacy];");
    }

    [Fact]
    public void Emit_alter_add_column_when_only_columns_added()
    {
        var oldT = new Table("dbo", "Customer", new[]
        {
            new Column("Id", "int", false, 1)
        });
        var newT = new Table("dbo", "Customer", new[]
        {
            new Column("Id", "int", false, 1),
            new Column("Email", "nvarchar(200)", true, 2)
        });
        var pair = new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT);

        var sql = new TableScriptEmitter().Emit(pair).Trim();

        sql.Should().Contain("ALTER TABLE [dbo].[Customer] ADD [Email] nvarchar(200) NULL;");
        sql.Should().NotContain("DROP TABLE");
    }
}
```

- [ ] **Step 2: Run the unit test — should fail**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~TableScriptEmitterTests"
```

Expected: FAIL — types missing.

- [ ] **Step 3: Write the production code**

`src/DbDelta.Core/ScriptGen/IScriptEmitter.cs`:

```csharp
using DbDelta.Core.Diff;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits the T-SQL DDL required to transform side B into side A for one paired object.
/// </summary>
public interface IScriptEmitter
{
    string Emit(DifferencePair pair);
}
```

`src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP / ALTER (add-column only in M1) for tables.
/// </summary>
public sealed class TableScriptEmitter : IScriptEmitter
{
    public string Emit(DifferencePair pair) => pair.Status switch
    {
        DifferenceStatus.OnlyInA => EmitCreate((Table)pair.SideA!),
        DifferenceStatus.OnlyInB => EmitDrop((Table)pair.SideB!),
        DifferenceStatus.Different => EmitAlter((Table)pair.SideA!, (Table)pair.SideB!),
        _ => string.Empty,
    };

    private static string EmitCreate(Table table)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE [").Append(table.Schema).Append("].[").Append(table.Name).AppendLine("] (");
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var c = table.Columns[i];
            sb.Append("    ").Append(FormatColumn(c));
            sb.AppendLine(i == table.Columns.Count - 1 ? "" : ",");
        }
        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string EmitDrop(Table table) =>
        $"DROP TABLE [{table.Schema}].[{table.Name}];";

    private static string EmitAlter(Table newT, Table oldT)
    {
        var sb = new StringBuilder();
        var existingByName = oldT.Columns.ToDictionary(c => c.Name);

        foreach (var newCol in newT.Columns)
        {
            if (!existingByName.ContainsKey(newCol.Name))
            {
                sb.Append("ALTER TABLE [").Append(newT.Schema).Append("].[").Append(newT.Name)
                  .Append("] ADD ").Append(FormatColumn(newCol)).AppendLine(";");
            }
        }
        // Note: removed columns + altered types are deferred to M2.
        return sb.ToString();
    }

    private static string FormatColumn(Column c)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(c.Name).Append("] ").Append(c.DataType);
        if (c.IsIdentity) sb.Append(" IDENTITY");
        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        if (!string.IsNullOrEmpty(c.DefaultExpression))
        {
            sb.Append(" DEFAULT ").Append(c.DefaultExpression);
        }
        return sb.ToString();
    }
}
```

`src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:

```csharp
using System.Collections.Generic;
using System.Text;
using DbDelta.Core.Diff;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Orchestrates per-object emitters and wraps the output in a deployment-ready batch.
/// v1 only registers <see cref="TableScriptEmitter"/>.
/// </summary>
public sealed class ScriptGenerator
{
    private readonly TableScriptEmitter _tableEmitter = new();

    public string Generate(ComparisonResult result, IEnumerable<DifferencePair>? selection = null)
    {
        var pairs = selection ?? result.Differences;
        var sb = new StringBuilder();
        sb.AppendLine("-- Generated by DbDelta");
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRANSACTION;");
        sb.AppendLine("GO");

        foreach (var pair in pairs)
        {
            if (pair.Status == DifferenceStatus.Identical) continue;
            sb.AppendLine(_tableEmitter.Emit(pair));
            sb.AppendLine("GO");
        }

        sb.AppendLine("COMMIT TRANSACTION;");
        sb.AppendLine("GO");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run unit tests — should pass**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~TableScriptEmitterTests"
```

Expected: PASS — 3 tests green.

- [ ] **Step 5: Write the golden snapshot test**

`tests/DbDelta.ScriptGen.GoldenTests/TableGoldenTests.cs`:

```csharp
using System.Threading.Tasks;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

[UsesVerify]
public class TableGoldenTests
{
    [Fact]
    public Task Create_full_customer_table()
    {
        var table = new Table("dbo", "Customer", new[]
        {
            new Column("Id",    "int",           false, 1, IsIdentity: true),
            new Column("Name",  "nvarchar(100)", false, 2),
            new Column("Email", "nvarchar(200)", true,  3, DefaultExpression: "('unknown')"),
        });
        var pair = new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null);
        var sql = new TableScriptEmitter().Emit(pair);
        return Verifier.Verify(sql);
    }
}
```

- [ ] **Step 6: Run golden test once to accept the snapshot**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests
```

Expected: FAIL the first time (no snapshot yet). Inspect the generated `.received.sql` file, confirm it matches expectation, then `mv` (or `move` on Windows) `*.received.*` to `*.verified.*`:

```bash
git ls-files --others --exclude-standard tests/DbDelta.ScriptGen.GoldenTests
```

Move the `.received.sql` files manually to `.verified.sql` (or use Verify's CLI tool). Re-run:

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/DbDelta.Core/ScriptGen tests/DbDelta.Core.UnitTests/ScriptGen tests/DbDelta.ScriptGen.GoldenTests
git commit -m "feat(core): TableScriptEmitter (CREATE/DROP/ALTER ADD COLUMN) + golden snapshots"
```

---

### Task T1.7: Provider integration test — Testcontainers MS SQL fixture + TableReader test

**Files:**
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/LiveDbFixture.cs`
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/TableReaderTests.cs`

- [ ] **Step 1: Write the fixture**

```csharp
using System;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// Shared SQL Server container fixture used by all integration tests in this assembly.
/// </summary>
public sealed class LiveDbFixture : IAsyncLifetime
{
    public MsSqlContainer Container { get; } = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Y0urStrong!Pass")
        .Build();

    public string ConnectionString => Container.GetConnectionString() + ";TrustServerCertificate=True;";

    public async Task InitializeAsync() => await Container.StartAsync();

    public async Task DisposeAsync() => await Container.DisposeAsync();
}

[CollectionDefinition(nameof(LiveDbCollection))]
public sealed class LiveDbCollection : ICollectionFixture<LiveDbFixture> { }
```

- [ ] **Step 2: Write the failing integration test**

```csharp
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Providers.LiveDb;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class TableReaderTests
{
    private readonly LiveDbFixture _fixture;

    public TableReaderTests(LiveDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LiveDbSource_loads_a_table_with_columns_and_identity()
    {
        // Arrange — create a fresh DB and a known schema
        await using (var bootstrap = new SqlConnection(_fixture.ConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaTest') IS NULL CREATE DATABASE DbDeltaTest;");
        }

        var dbConn = new SqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaTest"
        }.ConnectionString;

        await using (var c = new SqlConnection(dbConn))
        {
            await c.OpenAsync();
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Customer') IS NULL
                    CREATE TABLE dbo.Customer (
                        Id    int IDENTITY(1,1) NOT NULL,
                        Name  nvarchar(100)     NOT NULL,
                        Email nvarchar(200)         NULL
                    );
                """);
        }

        // Act
        var source = new LiveDbSource(dbConn);
        var result = await source.LoadAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        var customer = result.Value!.Tables.Single(t => t.Name == "Customer");
        customer.Schema.Should().Be("dbo");
        customer.Columns.Should().HaveCount(3);
        customer.Columns[0].Name.Should().Be("Id");
        customer.Columns[0].IsIdentity.Should().BeTrue();
        customer.Columns[2].IsNullable.Should().BeTrue();
    }

    private static async Task ExecAsync(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: Run integration test — should pass (the actual SQL container does the work)**

```bash
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests
```

Expected: PASS — container spin-up takes 30-90s on first run, then test green.

- [ ] **Step 4: Commit**

```bash
git add tests/DbDelta.Providers.LiveDb.IntegrationTests
git commit -m "test(providers/livedb): Testcontainers MSSQL fixture + TableReader integration test"
```

---

### Task T1.8: CLI — wire System.CommandLine root + `compare` command stub

**Files:**
- Modify: `src/DbDelta.Cli/Program.cs`
- Create: `src/DbDelta.Cli/Commands/CompareCommand.cs`

- [ ] **Step 1: Replace `Program.cs`**

```csharp
using System.CommandLine;
using DbDelta.Cli;
using DbDelta.Cli.Commands;

var root = new RootCommand("DbDelta — open-source SQL Server schema compare and deployment tool")
{
    CompareCommand.Build()
};

return await root.InvokeAsync(args);
```

- [ ] **Step 2: Write `CompareCommand` (stub — wired to engine in T1.9)**

```csharp
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;

namespace DbDelta.Cli.Commands;

internal static class CompareCommand
{
    public static Command Build()
    {
        var source = new Option<string>("--source", "Source SQL Server connection string") { IsRequired = true };
        var target = new Option<string>("--target", "Target SQL Server connection string") { IsRequired = true };
        var format = new Option<string>("--format", () => "text", "Output format: text | json");

        var command = new Command("compare", "Compare two databases and print the differences")
        {
            source, target, format
        };

        command.SetHandler(async (string src, string tgt, string fmt) =>
        {
            // T1.9 wires this to the engine.
            await Task.CompletedTask;
        }, source, target, format);

        return command;
    }
}
```

- [ ] **Step 3: Build + smoke**

```bash
dotnet build src/DbDelta.Cli/DbDelta.Cli.csproj
dotnet run --project src/DbDelta.Cli/DbDelta.Cli.csproj -- compare --help
```

Expected: help text printed for `compare`.

- [ ] **Step 4: Commit**

```bash
git add src/DbDelta.Cli/Program.cs src/DbDelta.Cli/Commands
git commit -m "feat(cli): root command + compare command stub (System.CommandLine)"
```

---

### Task T1.9: CLI — wire `compare` to the engine

**Files:**
- Modify: `src/DbDelta.Cli/Commands/CompareCommand.cs`
- Create: `src/DbDelta.Cli/Output/JsonFormatter.cs`
- Create: `src/DbDelta.Cli/Output/TextFormatter.cs`

- [ ] **Step 1: Write `TextFormatter`**

`src/DbDelta.Cli/Output/TextFormatter.cs`:

```csharp
using System.Linq;
using System.Text;
using DbDelta.Core.Diff;

namespace DbDelta.Cli.Output;

internal static class TextFormatter
{
    public static string Format(ComparisonResult result)
    {
        var sb = new StringBuilder();
        var grouped = result.Differences
            .GroupBy(d => d.Status)
            .OrderBy(g => g.Key);

        foreach (var g in grouped)
        {
            sb.Append('=').Append(g.Key).Append(" (").Append(g.Count()).AppendLine(")");
            foreach (var pair in g.OrderBy(p => p.Identity.SchemaName).ThenBy(p => p.Identity.ObjectName))
            {
                sb.Append("  [").Append(pair.Identity.Kind).Append("] ")
                  .Append(pair.Identity.SchemaName).Append('.').AppendLine(pair.Identity.ObjectName);
            }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Write `JsonFormatter`**

`src/DbDelta.Cli/Output/JsonFormatter.cs`:

```csharp
using System.Linq;
using System.Text.Json;
using DbDelta.Core.Diff;

namespace DbDelta.Cli.Output;

internal static class JsonFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static string Format(ComparisonResult result)
    {
        var dto = new
        {
            differences = result.Differences.Select(d => new
            {
                kind = d.Identity.Kind,
                schema = d.Identity.SchemaName,
                name = d.Identity.ObjectName,
                status = d.Status.ToString(),
            }).ToArray()
        };
        return JsonSerializer.Serialize(dto, Options);
    }
}
```

- [ ] **Step 3: Rewrite `CompareCommand`**

```csharp
using System;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Cli.Output;
using DbDelta.Core.Diff;
using DbDelta.Core.Options;
using DbDelta.Providers.LiveDb;

namespace DbDelta.Cli.Commands;

internal static class CompareCommand
{
    public static Command Build()
    {
        var source = new Option<string>("--source", "Source SQL Server connection string") { IsRequired = true };
        var target = new Option<string>("--target", "Target SQL Server connection string") { IsRequired = true };
        var format = new Option<string>("--format", () => "text", "Output format: text | json");

        var command = new Command("compare", "Compare two databases and print the differences")
        {
            source, target, format
        };

        command.SetHandler(async (string srcConn, string tgtConn, string fmt) =>
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { cts.Cancel(); e.Cancel = true; };

            var srcSource = new LiveDbSource(srcConn, "source");
            var tgtSource = new LiveDbSource(tgtConn, "target");

            var srcResult = await srcSource.LoadAsync(cts.Token);
            if (!srcResult.IsSuccess) { WriteError(srcResult.Error!); Environment.ExitCode = MapErrorToExitCode(srcResult.Error!); return; }

            var tgtResult = await tgtSource.LoadAsync(cts.Token);
            if (!tgtResult.IsSuccess) { WriteError(tgtResult.Error!); Environment.ExitCode = MapErrorToExitCode(tgtResult.Error!); return; }

            var comparison = new ComparisonEngine().Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default);

            var output = fmt.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? JsonFormatter.Format(comparison)
                : TextFormatter.Format(comparison);

            Console.Out.WriteLine(output);

            var hasDifferences = comparison.Differences
                .Any(d => d.Status is DifferenceStatus.Different or DifferenceStatus.OnlyInA or DifferenceStatus.OnlyInB);
            Environment.ExitCode = hasDifferences
                ? ExitCodes.SuccessDifferencesFound
                : ExitCodes.SuccessNoDifferences;
        }, source, target, format);

        return command;
    }

    private static void WriteError(DbDelta.Core.Abstractions.Error error)
    {
        Console.Error.WriteLine($"{{\"code\":\"{error.Code}\",\"message\":\"{error.Message.Replace("\"", "\\\"")}\",\"remediation\":\"{error.Remediation?.Replace("\"", "\\\"")}\"}}");
    }

    private static int MapErrorToExitCode(DbDelta.Core.Abstractions.Error error) => error.Code switch
    {
        DbDelta.Core.Abstractions.ErrorCode.CannotConnect or DbDelta.Core.Abstractions.ErrorCode.AuthFailed
            => ExitCodes.ConnectionOrAuthError,
        DbDelta.Core.Abstractions.ErrorCode.InsufficientPermissions
            => ExitCodes.InsufficientPermissions,
        DbDelta.Core.Abstractions.ErrorCode.CatalogQueryFailed
            => ExitCodes.SchemaReadFailure,
        _ => ExitCodes.InternalError,
    };
}
```

- [ ] **Step 4: Build**

```bash
dotnet build src/DbDelta.Cli/DbDelta.Cli.csproj
```

Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Cli/Commands src/DbDelta.Cli/Output
git commit -m "feat(cli): wire compare command end-to-end via LiveDbSource + JSON/text output"
```

---

### Task T1.10: CLI acceptance test

**Files:**
- Create: `tests/DbDelta.Cli.AcceptanceTests/CompareCommandTests.cs`

- [ ] **Step 1: Write the acceptance test**

```csharp
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace DbDelta.Cli.AcceptanceTests;

/// <summary>
/// CLI acceptance tests own their own SQL container — they do NOT take a
/// cross-project reference to the integration test fixture, because xUnit
/// collections do not cross assembly boundaries.
/// </summary>
public sealed class CliFixture : IAsyncLifetime
{
    public MsSqlContainer Container { get; } = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Y0urStrong!Pass")
        .Build();

    public string ConnectionString => Container.GetConnectionString() + ";TrustServerCertificate=True;";

    public async Task InitializeAsync() => await Container.StartAsync();
    public async Task DisposeAsync() => await Container.DisposeAsync();
}

[CollectionDefinition(nameof(CliCollection))]
public sealed class CliCollection : ICollectionFixture<CliFixture> { }

[Collection(nameof(CliCollection))]
public class CompareCommandTests
{
    private readonly CliFixture _fixture;

    public CompareCommandTests(CliFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Returns_exit_code_1_when_source_has_an_extra_table()
    {
        var srcDb = "DbDeltaSrc";
        var tgtDb = "DbDeltaTgt";
        await CreateDb(srcDb);
        await CreateDb(tgtDb);
        await CreateCustomerTable(srcDb);

        var srcConn = ConnectionFor(srcDb);
        var tgtConn = ConnectionFor(tgtDb);

        var exitCode = await RunCli($"compare --source \"{srcConn}\" --target \"{tgtConn}\" --format json");

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Returns_exit_code_0_when_both_databases_are_empty()
    {
        var srcDb = "DbDeltaEmptySrc";
        var tgtDb = "DbDeltaEmptyTgt";
        await CreateDb(srcDb);
        await CreateDb(tgtDb);

        var srcConn = ConnectionFor(srcDb);
        var tgtConn = ConnectionFor(tgtDb);

        var exitCode = await RunCli($"compare --source \"{srcConn}\" --target \"{tgtConn}\" --format text");

        exitCode.Should().Be(0);
    }

    private string ConnectionFor(string db) =>
        new SqlConnectionStringBuilder(_fixture.ConnectionString) { InitialCatalog = db }.ConnectionString;

    private async Task CreateDb(string db)
    {
        await using var c = new SqlConnection(_fixture.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateCustomerTable(string db)
    {
        await using var c = new SqlConnection(ConnectionFor(db));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(
            "IF OBJECT_ID('dbo.Customer') IS NULL CREATE TABLE dbo.Customer(Id int);", c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> RunCli(string args)
    {
        var exe = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "DbDelta.Cli", "bin",
            "Debug", "net10.0", "dbdelta.exe");
        exe = Path.GetFullPath(exe);
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"{exe} {args}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
```

- [ ] **Step 2: Build the CLI before running the acceptance test**

```bash
dotnet build src/DbDelta.Cli/DbDelta.Cli.csproj
dotnet test tests/DbDelta.Cli.AcceptanceTests
```

Expected: PASS — both test cases green.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Cli.AcceptanceTests
git commit -m "test(cli): acceptance tests for compare command exit codes"
```

---

### Task T1.11: Shared DTOs for App ↔ Core boundary

**Files:**
- Create: `src/DbDelta.Shared/Dtos/ComparisonResultDto.cs`
- Create: `src/DbDelta.Shared/Dtos/DifferenceDto.cs`
- Create: `src/DbDelta.Shared/Dtos/Mapper.cs`

- [ ] **Step 1: Write the DTOs**

`src/DbDelta.Shared/Dtos/DifferenceDto.cs`:

```csharp
namespace DbDelta.Shared.Dtos;

public sealed record DifferenceDto(
    string Kind,
    string SchemaName,
    string ObjectName,
    string Status);
```

`src/DbDelta.Shared/Dtos/ComparisonResultDto.cs`:

```csharp
using System.Collections.Generic;

namespace DbDelta.Shared.Dtos;

public sealed record ComparisonResultDto(IReadOnlyList<DifferenceDto> Differences);
```

`src/DbDelta.Shared/Dtos/Mapper.cs`:

```csharp
using System.Linq;
using DbDelta.Core.Diff;

namespace DbDelta.Shared.Dtos;

public static class Mapper
{
    public static ComparisonResultDto ToDto(ComparisonResult result) =>
        new(result.Differences.Select(d => new DifferenceDto(
            Kind: d.Identity.Kind,
            SchemaName: d.Identity.SchemaName,
            ObjectName: d.Identity.ObjectName,
            Status: d.Status.ToString())).ToArray());
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/DbDelta.Shared/DbDelta.Shared.csproj
```

Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Shared/Dtos
git commit -m "feat(shared): add ComparisonResultDto + DifferenceDto + Mapper for App boundary"
```

---

### Task T1.11a: Import the DbDelta Design System assets into the App project

**Files:**
- Create: `src/DbDelta.App/wwwroot/assets/tokens.css` (copied from `docs/design-system/project/assets/tokens.css`)
- Create: `src/DbDelta.App/wwwroot/assets/base.css` (copied)
- Create: `src/DbDelta.App/wwwroot/assets/components-ui.css` (copied)
- Create: `src/DbDelta.App/wwwroot/assets/components-domain.css` (copied)
- Create: `src/DbDelta.App/wwwroot/assets/app.js` (copied)
- Create: `src/DbDelta.App/wwwroot/assets/logo.svg` (copied)
- Modify: `src/DbDelta.App/DbDelta.App.csproj` (mark assets as Content / CopyToOutputDirectory)

Rationale: the DbDelta Design System v1.0 lives canonically at `docs/design-system/` and is the source-of-truth for visual decisions. The App consumes a **copy** of the production assets — never the docs path — so the docs system can evolve independently and the App build is hermetic.

- [ ] **Step 1: Copy the six asset files**

PowerShell (run from repo root):

```powershell
$src = "docs/design-system/project/assets"
$dst = "src/DbDelta.App/wwwroot/assets"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item "$src/tokens.css"            "$dst/tokens.css"            -Force
Copy-Item "$src/base.css"              "$dst/base.css"              -Force
Copy-Item "$src/components-ui.css"     "$dst/components-ui.css"     -Force
Copy-Item "$src/components-domain.css" "$dst/components-domain.css" -Force
Copy-Item "$src/app.js"                "$dst/app.js"                -Force
Copy-Item "$src/logo.svg"              "$dst/logo.svg"              -Force
```

(bash equivalent: `cp docs/design-system/project/assets/{tokens,base,components-ui,components-domain}.css src/DbDelta.App/wwwroot/assets/ && cp docs/design-system/project/assets/{app.js,logo.svg} src/DbDelta.App/wwwroot/assets/`)

- [ ] **Step 2: Ensure assets are picked up by the build**

Open `src/DbDelta.App/DbDelta.App.csproj` and add this ItemGroup if not already present (Razor SDK includes `wwwroot/**` automatically, but make `Content / CopyToOutputDirectory` explicit so the assets land next to the .exe):

```xml
<ItemGroup>
  <Content Include="wwwroot\**">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

- [ ] **Step 3: Sanity build**

```bash
dotnet build src/DbDelta.App/DbDelta.App.csproj
```

Expected: build green. After build, verify the assets exist at `src/DbDelta.App/bin/Debug/net10.0-windows/wwwroot/assets/tokens.css`.

- [ ] **Step 4: Document the sync rule**

Append to `CONTRIBUTING.md`:

```markdown
### Design System Sync Rule

The App copies the design system from `docs/design-system/project/assets/` into `src/DbDelta.App/wwwroot/assets/`. When the design system changes:

1. Edit files under `docs/design-system/project/`.
2. Re-run the copy command (see Task T1.11a in plan M0/M1).
3. Commit both the docs change and the asset copy in a single commit so the App build stays in sync.

Do NOT hand-edit `src/DbDelta.App/wwwroot/assets/*` — they are generated. Edits will be overwritten on the next sync.
```

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.App/wwwroot/assets src/DbDelta.App/DbDelta.App.csproj CONTRIBUTING.md
git commit -m "feat(app): import DbDelta Design System v1.0 assets into wwwroot"
```

---

### Task T1.12: App — WinForms shell hosting BlazorWebView

**Files:**
- Create: `src/DbDelta.App/Program.cs`
- Create: `src/DbDelta.App/MainForm.cs`
- Create: `src/DbDelta.App/_Imports.razor`
- Create: `src/DbDelta.App/App.razor`
- Create: `src/DbDelta.App/wwwroot/index.html`

- [ ] **Step 1: Replace auto-generated `Form1.cs` and `Program.cs`**

Delete `src/DbDelta.App/Form1.cs`, `Form1.Designer.cs`, `Form1.resx`.

Write `src/DbDelta.App/Program.cs`:

```csharp
using System;
using System.Windows.Forms;

namespace DbDelta.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
```

- [ ] **Step 2: Write the host form**

`src/DbDelta.App/MainForm.cs`:

```csharp
using System.Windows.Forms;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;

namespace DbDelta.App;

public partial class MainForm : Form
{
    public MainForm()
    {
        Text = "DbDelta";
        Width = 1280;
        Height = 800;

        var services = new ServiceCollection();
        services.AddWindowsFormsBlazorWebView();
        services.AddSingleton<State.AppState>();

        var blazor = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot/index.html",
            Services = services.BuildServiceProvider(),
        };
        blazor.RootComponents.Add<App>("#app");
        Controls.Add(blazor);
    }
}
```

- [ ] **Step 3: Write the root Razor file (uses Design System app-shell)**

`src/DbDelta.App/App.razor`:

```razor
@namespace DbDelta.App
@using DbDelta.App.Components
@using DbDelta.App.State
@inject AppState AppState

@*
   The DbDelta Design System defines a 3-row app shell:
   .app-shell        — grid container
   .app-topbar       — brand + global actions
   .app-sidebar      — navigation (empty in M1, populated in later milestones)
   .app-main         — primary content
   .app-status       — footer status strip

   v1 of the walking skeleton fills .app-main with the comparison flow only.
*@

<div class="app-shell">

    <header class="app-topbar">
        <div class="brandmark">
            @* Inline brand mark — logo.svg is a standalone file, not a symbol library *@
            <svg class="brandmark-glyph" viewBox="0 0 64 64" fill="none" aria-hidden="true">
                <rect x="2" y="2" width="60" height="60" rx="14" fill="currentColor"></rect>
                <path d="M19 46 L32 18 L45 46 Z" fill="none" stroke="var(--logo-bg, white)" stroke-width="3.2" stroke-linejoin="round"></path>
                <path d="M25 46 L25 28" stroke="var(--logo-bg, white)" stroke-width="3.2" stroke-linecap="round"></path>
            </svg>
            <span class="brandmark-text">DbDelta</span>
            <span class="t-meta">v0.1 alpha</span>
        </div>

        <div class="row gap-2 flex-1 justify-end">
            <button class="btn btn--ghost btn--icon-only" data-theme-toggle title="Toggle theme">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41"/></svg>
            </button>
        </div>
    </header>

    <aside class="app-sidebar" aria-label="Primary navigation">
        @* M1: navigation will be populated by later milestones (snapshots, history, settings). *@
    </aside>

    <main class="app-main">
        <ConnectionPicker />
        <ResultsTree Result="@AppState.LastComparison" />
    </main>

    <footer class="app-status">
        <span>@(AppState.IsBusy ? "Working…" : "Ready")</span>
        <span class="sep">·</span>
        <span class="t-mono">@AppState.SourceConnectionString</span>
        <span class="sep">→</span>
        <span class="t-mono">@AppState.TargetConnectionString</span>
    </footer>

</div>
```

`src/DbDelta.App/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components
@using DbDelta.App.Components
@using DbDelta.App.State
@using DbDelta.Shared.Dtos
```

The Razor SDK compiles `App.razor` into a class named `App` in the `DbDelta.App` namespace, referenced from `MainForm.cs` as `<App>` in `RootComponents.Add<App>("#app")`.

- [ ] **Step 4: Write the host page (loads Design System assets)**

`src/DbDelta.App/wwwroot/index.html`:

```html
<!DOCTYPE html>
<html lang="en" data-theme="light" data-accent="cobalt">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>DbDelta</title>
    <base href="/" />

    <!-- Geist + Geist Mono — design-system fonts -->
    <link rel="preconnect" href="https://fonts.googleapis.com" />
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
    <link href="https://fonts.googleapis.com/css2?family=Geist:wght@400;500;600;700&family=Geist+Mono:wght@400;500;600&display=swap" rel="stylesheet" />

    <!-- Design System v1.0 — ordered: tokens first, then base, then components -->
    <link rel="stylesheet" href="assets/tokens.css" />
    <link rel="stylesheet" href="assets/base.css" />
    <link rel="stylesheet" href="assets/components-ui.css" />
    <link rel="stylesheet" href="assets/components-domain.css" />
</head>
<body>
    <div id="app"></div>
    <script src="_framework/blazor.webview.js" autostart="false"></script>
    <script src="assets/app.js" defer></script>
</body>
</html>
```

> **Note:** the `data-theme` attribute on `<html>` switches between light/dark; the design system's `app.js` wires the `[data-theme-toggle]` button in the topbar to flip it and persist the choice to `localStorage`. No additional JS is needed in M1.

- [ ] **Step 5: Build**

```bash
dotnet build src/DbDelta.App/DbDelta.App.csproj
```

Expected: build green. Skip running the app until T1.13 adds the components.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.App
git commit -m "feat(app): WinForms shell + BlazorWebView + Design System app-shell layout"
```

---

### Task T1.13: App — ConnectionPicker + ResultsTree + AppState

**Files:**
- Create: `src/DbDelta.App/State/AppState.cs`
- Create: `src/DbDelta.App/Components/ConnectionPicker.razor`
- Create: `src/DbDelta.App/Components/ResultsTree.razor`

- [ ] **Step 1: Write `AppState`**

`src/DbDelta.App/State/AppState.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Diff;
using DbDelta.Core.Options;
using DbDelta.Providers.LiveDb;
using DbDelta.Shared.Dtos;

namespace DbDelta.App.State;

public sealed class AppState
{
    public string SourceConnectionString { get; set; } = "";
    public string TargetConnectionString { get; set; } = "";
    public ComparisonResultDto? LastComparison { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy { get; private set; }

    public event Action? OnChange;

    public async Task RunComparisonAsync(CancellationToken ct)
    {
        IsBusy = true;
        LastError = null;
        OnChange?.Invoke();

        try
        {
            var src = new LiveDbSource(SourceConnectionString, "source");
            var tgt = new LiveDbSource(TargetConnectionString, "target");

            var srcRes = await src.LoadAsync(ct);
            if (!srcRes.IsSuccess) { LastError = srcRes.Error!.Message; return; }

            var tgtRes = await tgt.LoadAsync(ct);
            if (!tgtRes.IsSuccess) { LastError = tgtRes.Error!.Message; return; }

            var engine = new ComparisonEngine();
            var result = engine.Compare(srcRes.Value!, tgtRes.Value!, ComparisonOptions.Default);
            LastComparison = Mapper.ToDto(result);
        }
        finally
        {
            IsBusy = false;
            OnChange?.Invoke();
        }
    }
}
```

- [ ] **Step 2: Write `ConnectionPicker.razor` (uses Design System `connection-bar` + `btn`)**

`src/DbDelta.App/Components/ConnectionPicker.razor`:

```razor
@inject DbDelta.App.State.AppState AppState

@*
   Design System refs:
   .connection-bar              wraps the two endpoint cards
   .connection-endpoint--source violet-tinted left card
   .connection-endpoint--target cobalt-tinted right card
   .connection-tag              "SOURCE" / "TARGET" eyebrow label
   .connection-name             the editable connection string (rendered as input here)
   .input                       generic input styling
   .btn .btn--primary           primary action button
   .alert .alert--danger        error region
*@

<section class="stack gap-8" style="padding: var(--space-12) 0;">
    <h2 class="t-h3">Connections</h2>

    <div class="connection-bar">
        <div class="connection-endpoint connection-endpoint--source">
            <span class="connection-tag t-eyebrow">Source</span>
            <input
                class="input connection-name t-mono"
                type="text"
                @bind="AppState.SourceConnectionString"
                @bind:event="oninput"
                placeholder="Server=.;Database=Dev;Trusted_Connection=True;TrustServerCertificate=True" />
        </div>

        <button type="button" class="connection-swap btn btn--ghost btn--icon-only" title="Swap source and target" @onclick="Swap">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M16 4l4 4-4 4M20 8H8M8 20l-4-4 4-4M4 16h12"/></svg>
        </button>

        <div class="connection-endpoint connection-endpoint--target">
            <span class="connection-tag t-eyebrow">Target</span>
            <input
                class="input connection-name t-mono"
                type="text"
                @bind="AppState.TargetConnectionString"
                @bind:event="oninput"
                placeholder="Server=.;Database=Prod;Trusted_Connection=True;TrustServerCertificate=True" />
        </div>
    </div>

    <div class="row gap-4">
        <button class="btn btn--primary btn--lg" @onclick="CompareAsync" disabled="@AppState.IsBusy">
            @if (AppState.IsBusy)
            {
                <span class="spinner" aria-hidden="true"></span>
                <span>Comparing…</span>
            }
            else
            {
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14M13 6l6 6-6 6"/></svg>
                <span>Compare</span>
            }
        </button>
    </div>

    @if (AppState.LastError is not null)
    {
        <div class="alert alert--danger" role="alert">
            <svg class="alert-icon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v6M12 16h.01"/></svg>
            <div class="alert-body">
                <strong>Comparison failed.</strong>
                <p class="t-body-sm">@AppState.LastError</p>
            </div>
        </div>
    }
</section>

@code {
    private async Task CompareAsync() =>
        await AppState.RunComparisonAsync(System.Threading.CancellationToken.None);

    private void Swap()
    {
        (AppState.SourceConnectionString, AppState.TargetConnectionString) =
            (AppState.TargetConnectionString, AppState.SourceConnectionString);
    }

    protected override void OnInitialized() => AppState.OnChange += StateHasChanged;
}
```

> **Note:** `.connection-bar`, `.connection-endpoint`, `.connection-tag`, `.connection-swap`, `.connection-name`, `.alert--danger`, `.spinner`, and `.input` all come from the Design System CSS imported in T1.11a. If any of these class names are missing or behave unexpectedly, fix them in `docs/design-system/` and re-run T1.11a (do not hand-patch the App).

- [ ] **Step 3: Write `ResultsTree.razor` (uses Design System `dgrid` + `badge` + `surface-raised`)**

`src/DbDelta.App/Components/ResultsTree.razor`:

```razor
@using System.Linq
@using DbDelta.Shared.Dtos

@*
   Design System refs:
   .surface-raised   panel container
   .dgrid            Object Diff Grid (table)
   .dgrid-status-strip 4px left strip whose color is driven by data-diff attribute
   .badge .badge--*  pill labels (success/info/warning/danger soft variants)
   data-diff         row attribute drives status strip: modified|added|removed|only-source|only-target
*@

@if (Result is null)
{
    <div class="surface-subtle" style="padding: var(--space-16); text-align: center;">
        <p class="t-body fg-subtle"><em>No comparison run yet. Enter two connection strings and click Compare.</em></p>
    </div>
}
else if (Result.Differences.Count == 0)
{
    <div class="surface-subtle" style="padding: var(--space-16); text-align: center;">
        <p class="t-body fg-subtle">No tables found in either database.</p>
    </div>
}
else
{
    <section class="surface-raised" style="padding: var(--space-12); overflow: hidden;">
        <header class="row justify-between" style="margin-bottom: var(--space-8);">
            <h2 class="t-h3">Results</h2>
            <span class="badge badge--outline t-num">@Result.Differences.Count @(Result.Differences.Count == 1 ? "object" : "objects")</span>
        </header>

        <table class="dgrid">
            <thead>
                <tr>
                    <th style="width: 28px;"></th>
                    <th>Kind</th>
                    <th>Schema</th>
                    <th>Name</th>
                    <th>Status</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var d in Result.Differences
                                         .OrderBy(d => d.Status)
                                         .ThenBy(d => d.SchemaName)
                                         .ThenBy(d => d.ObjectName))
                {
                    <tr class="diff-row" data-diff="@MapDiff(d.Status)">
                        <td class="dgrid-status-strip" aria-hidden="true"></td>
                        <td class="t-mono">@d.Kind</td>
                        <td>@d.SchemaName</td>
                        <td><strong>@d.ObjectName</strong></td>
                        <td>
                            <span class="badge @BadgeClass(d.Status)">@d.Status</span>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </section>
}

@code {
    [Parameter] public ComparisonResultDto? Result { get; set; }

    private static string MapDiff(string status) => status switch
    {
        "Different"  => "modified",
        "OnlyInA"    => "only-source",
        "OnlyInB"    => "only-target",
        _            => "identical",
    };

    private static string BadgeClass(string status) => status switch
    {
        "Different"  => "badge--info",
        "OnlyInA"    => "badge--violet",
        "OnlyInB"    => "badge--warning",
        _            => "badge--outline",
    };
}
```

- [ ] **Step 4: Build the app**

```bash
dotnet build src/DbDelta.App/DbDelta.App.csproj
```

Expected: build green.

- [ ] **Step 5: Smoke run (manual)**

```bash
dotnet run --project src/DbDelta.App/DbDelta.App.csproj
```

Expected: window opens titled "DbDelta", with the connection inputs and a Compare button. Closing the window terminates the process.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.App/Components src/DbDelta.App/State
git commit -m "feat(app): ConnectionPicker + ResultsTree + AppState (Compare wired to LiveDb)"
```

---

### Task T1.14: App — bUnit component test for ResultsTree

**Files:**
- Create: `tests/DbDelta.App.ComponentTests/ResultsTreeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using DbDelta.App.Components;
using DbDelta.Shared.Dtos;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.ComponentTests;

public class ResultsTreeTests : TestContext
{
    [Fact]
    public void Renders_no_comparison_message_when_Result_null()
    {
        var cut = RenderComponent<ResultsTree>(p => p.Add(p2 => p2.Result, null));

        cut.Markup.Should().Contain("No comparison run yet");
    }

    [Fact]
    public void Renders_dgrid_rows_with_design_system_classes()
    {
        var dto = new ComparisonResultDto(new[]
        {
            new DifferenceDto("Table", "dbo", "Customer",   "OnlyInA"),
            new DifferenceDto("Table", "dbo", "Order",      "Different"),
            new DifferenceDto("Table", "dbo", "Legacy",     "OnlyInB"),
            new DifferenceDto("Table", "dbo", "Identical1", "Identical"),
        });

        var cut = RenderComponent<ResultsTree>(p => p.Add(p2 => p2.Result, dto));

        // Object names rendered
        cut.Markup.Should().Contain("Customer");
        cut.Markup.Should().Contain("Order");
        cut.Markup.Should().Contain("Legacy");
        cut.Markup.Should().Contain("Identical1");

        // Design System classes wired correctly
        cut.Markup.Should().Contain("class=\"dgrid\"");
        cut.Markup.Should().Contain("data-diff=\"only-source\"");
        cut.Markup.Should().Contain("data-diff=\"modified\"");
        cut.Markup.Should().Contain("data-diff=\"only-target\"");
        cut.Markup.Should().Contain("data-diff=\"identical\"");

        // Badge variants
        cut.Markup.Should().Contain("badge--info");
        cut.Markup.Should().Contain("badge--warning");
    }
}
```

- [ ] **Step 2: Run the test — should pass given T1.13 implementation**

```bash
dotnet test tests/DbDelta.App.ComponentTests
```

Expected: PASS — 2 tests green.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.App.ComponentTests
git commit -m "test(app): bUnit tests for ResultsTree empty + grouped rendering"
```

---

## Acceptance Criteria for M1

At the end of this plan, all of the following must be true:

- [ ] `dotnet build` produces 0 errors and 0 warnings (TreatWarningsAsErrors).
- [ ] `dotnet format --verify-no-changes` exits 0.
- [ ] `dotnet test` runs all 6 test projects and every test passes.
- [ ] `dotnet test tests/DbDelta.Architecture.Tests` confirms Core has no I/O dependencies.
- [ ] `dotnet run --project src/DbDelta.Cli -- compare --source <connA> --target <connB> --format text` prints a grouped diff and exits 0 (identical) or 1 (differences found).
- [ ] `dotnet run --project src/DbDelta.App` opens a Blazor window with a Compare button that populates the results tree from two real connections.
- [ ] App renders the DbDelta Design System v1.0: Cobalt-blue primary, Geist typography, light theme by default, `.app-shell` layout, `.connection-bar` for endpoints, `.dgrid` for results.
- [ ] Light/dark theme toggle in the topbar flips `<html data-theme>` and persists to localStorage (provided by design system's `app.js`).
- [ ] CI workflow on `windows-latest` is green on `main`.
- [ ] All commits land on `main` and `git push` succeeds.

If any criterion fails, do not declare M1 complete — open an issue or extend the plan with a follow-up task.

---

## Next Plan Preview

After M1 lands and is verified, the next plan (`docs/superpowers/plans/2026-MM-DD-m2-constraints-and-indexes.md`) will extend the engine to handle:

- Primary keys, foreign keys, unique constraints, check constraints, default constraints
- Indexes (clustered, non-clustered, filtered, included columns)
- Identity property changes (forcing table rebuild)
- Computed columns

That milestone will require the dependency resolver scaffolding (also extended in M2) because adding FKs introduces the first real ordering constraints between tables.
