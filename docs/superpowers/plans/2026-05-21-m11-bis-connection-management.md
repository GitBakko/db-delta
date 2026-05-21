# DbDelta — M11-bis Connection Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the "paste a raw connection string every time" UX with a per-user connection workspace — MRU recent list, smart picker with search + edit + pin + environment tag/colour, inline test connection, autosave on Compare, sidebar quick-switch, and `.dbd` project files — backed by a cross-platform-ready `ICredentialStore` port whose Windows implementation ships with M11-bis (macOS / Linux backends land as placeholders for v2).

**Architecture:** Three new ports land in `DbDelta.Core.Abstractions` (`ICredentialStore` / `IConnectionStore` / `IProjectStore`) with two new record DTOs (`ConnectionEntry` / `DbDeltaProject`). All I/O moves into `DbDelta.Persistence`: `JsonConnectionStore` (atomic-write JSON in `%LOCALAPPDATA%\DbDelta\connections.json`), `XmlProjectStore` (`XmlSerializer`-backed `.dbd`), three `ICredentialStore` backends behind a runtime factory (Windows DPAPI live, macOS Keychain + Linux Secret Service placeholders), a `ConnectionTester` static helper (`SqlConnection.Open` + `SELECT @@VERSION`, 10s timeout), and a `ConnectionStringRedactor` pure regex helper used everywhere a connection string would surface in UI or logs. The Avalonia app gains `ConnectionStoreViewModel` + `ConnectionEditViewModel` + new `ConnectionEditDialog` + `RecentConnectionsSidebar` + `EnvironmentBadge` views. `AppStateViewModel` learns to autosave on Compare success and to materialise a preset back into a connection string via the credential store.

**Tech Stack:** Same as the rest of DbDelta — .NET 10, C# 14, xUnit v3, FluentAssertions, Verify.Xunit, Microsoft.Data.SqlClient 6.x, Avalonia 12.0.3, CommunityToolkit.Mvvm 8.4.2. New package versions added: `Meziantou.Framework.Win32.CredentialManager` (already pinned in `Directory.Packages.props`) + `System.Text.Json` (already in the BCL on net10.0).

---

## Reference: Spec Sections This Plan Implements

| Spec section | Plan task(s) |
|--------------|--------------|
| §1.1 In scope — MRU, smart picker, presets, test, autosave, project file, redaction, masking, sidebar | T11.1 – T11.22 |
| §2.1 Component map | T11.1 – T11.16 |
| §3.1 ICredentialStore port + 3 backends | T11.3, T11.5, T11.6 |
| §3.2 IConnectionStore + ConnectionEntry record | T11.2, T11.7, T11.8 |
| §3.3 IProjectStore + DbDeltaProject record | T11.2, T11.10 |
| §3.4 JsonConnectionStore on-disk format | T11.7, T11.8 (atomic write + corruption tolerance) |
| §3.5 ConnectionStringRedactor | T11.4 |
| §3.6 ConnectionTester | T11.11 |
| §4.1 First launch | T11.13 (autosave) + T11.15 (load on app start) |
| §4.2 Smart picker on subsequent launches | T11.14 (combo + search) |
| §4.3 Edit dialog | T11.17 (VM) + T11.18 (View) |
| §4.4 Sidebar quick-switch | T11.19 |
| §4.5 .dbd project file Open / Save | T11.20 |
| §5 Security | T11.4 (redactor), T11.9 (file permission), T11.18 (mask + reveal-on-hold), T11.21 (logging) |
| §6 Error handling | T11.7 (corruption tolerance), T11.11 (test connection errors), T11.20 (missing-connection dialog) |
| §7 Testing | T11.7, T11.8, T11.10, T11.11, T11.12 — all tests embedded in their respective tasks |
| §8 Migration path | T11.22 (release notes) |

Out of scope (rejected in spec):
- Cloud sync
- Preset import / export
- Master-password fallback for cross-platform JSON-encrypted credentials
- Connection-usage telemetry

---

## File Structure Map

```
DbDelta/
├─ src/
│  ├─ DbDelta.Core/
│  │  └─ Abstractions/
│  │     ├─ ICredentialStore.cs              T11.3   (NEW — port)
│  │     ├─ IConnectionStore.cs              T11.2   (NEW — port)
│  │     ├─ IProjectStore.cs                 T11.2   (NEW — port)
│  │     ├─ ConnectionEntry.cs               T11.2   (NEW — record)
│  │     └─ DbDeltaProject.cs                T11.2   (NEW — record)
│  ├─ DbDelta.Persistence/
│  │  ├─ Util/
│  │  │  └─ ConnectionStringRedactor.cs      T11.4   (NEW — pure helper)
│  │  ├─ Credentials/
│  │  │  ├─ CredentialStoreFactory.cs        T11.5   (NEW — runtime selection)
│  │  │  ├─ DpapiCredentialStore.cs          T11.5   (NEW — Windows backend)
│  │  │  ├─ KeychainCredentialStore.cs       T11.6   (NEW — macOS placeholder)
│  │  │  └─ SecretServiceCredentialStore.cs  T11.6   (NEW — Linux placeholder)
│  │  ├─ Json/
│  │  │  └─ JsonConnectionStore.cs           T11.7, T11.8 (NEW — atomic JSON I/O)
│  │  ├─ Xml/
│  │  │  └─ XmlProjectStore.cs               T11.10  (NEW — .dbd XML I/O)
│  │  └─ Sql/
│  │     └─ ConnectionTester.cs              T11.11  (NEW — Open + SELECT @@VERSION)
│  └─ DbDelta.App.Avalonia/
│     ├─ ViewModels/
│     │  ├─ ConnectionStoreViewModel.cs       T11.13  (NEW — MRU + filter)
│     │  ├─ ConnectionEditViewModel.cs        T11.17  (NEW — edit dialog VM)
│     │  ├─ EnvironmentColorOption.cs         T11.16  (NEW — palette record)
│     │  └─ AppStateViewModel.cs              T11.13, T11.15  (MODIFY — autosave + materialise)
│     └─ Views/
│        ├─ EnvironmentBadge.axaml            T11.16  (NEW — small UserControl)
│        ├─ ConnectionPickerView.axaml        T11.14  (MODIFY — combo + edit + test)
│        ├─ ConnectionEditDialog.axaml        T11.18  (NEW — modal form)
│        └─ RecentConnectionsSidebar.axaml    T11.19  (NEW — populates M1 placeholder)
└─ tests/
   ├─ DbDelta.Persistence.UnitTests/                 T11.1  (NEW project)
   │  ├─ Util/ConnectionStringRedactorTests.cs       T11.4
   │  ├─ Json/JsonConnectionStoreTests.cs            T11.7, T11.8
   │  └─ Xml/XmlProjectStoreTests.cs                 T11.10
   ├─ DbDelta.Persistence.IntegrationTests/          T11.1  (NEW project)
   │  └─ Credentials/DpapiCredentialStoreTests.cs    T11.5  (Windows-only, SkippableFact)
   └─ DbDelta.App.HeadlessTests/                     T11.1  (NEW project)
      ├─ ConnectionStoreViewModelTests.cs            T11.13
      ├─ ConnectionEditViewModelTests.cs             T11.17
      └─ TestAppBuilder.cs                           T11.12 (Avalonia headless boilerplate)
```

**Existing files NOT touched:**
- `src/DbDelta.Core/Diff/*`, `src/DbDelta.Core/ScriptGen/*`, `src/DbDelta.Core/ObjectModel/*` — Core stays pure.
- `src/DbDelta.Providers.LiveDb/*` — provider unaffected.
- `src/DbDelta.Cli/*` — the CLI does NOT get the connection store in this milestone (raw connection strings only).
- `tests/DbDelta.Core.UnitTests/*`, `tests/DbDelta.Cli.AcceptanceTests/*` — unchanged.

---

## Conventions Used in This Plan

- Every step that adds code includes the full source — no "fill in".
- Every test has the actual assertion code.
- Conventional Commits with the established footer `Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>`.
- `dotnet build -warnaserror` after every code change.
- `dotnet format` runs as the wrap-up task (T11.22); interim file-level drift is OK.
- CI workflow `.github/workflows/ci.yml` already runs the Persistence + Headless test projects automatically once they're discovered under `tests/`.
- Avalonia ViewModel testing uses `Avalonia.Headless.XUnit` package, attribute `[AvaloniaFact]` (NOT `[Fact]`).

---

## Phase A — Project scaffolding (1 task)

### Task T11.1: Add three new test projects + Persistence MSBuild deps

**Files:**
- Create: `tests/DbDelta.Persistence.UnitTests/DbDelta.Persistence.UnitTests.csproj`
- Create: `tests/DbDelta.Persistence.IntegrationTests/DbDelta.Persistence.IntegrationTests.csproj`
- Create: `tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj`
- Modify: `src/DbDelta.Persistence/DbDelta.Persistence.csproj`
- Modify: `Directory.Packages.props`
- Modify: `DbDelta.sln`
- Modify: `tests/DbDelta.Architecture.Tests/LayeringTests.cs` (widen Persistence allowed deps)
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add the missing package version pin**

Edit `Directory.Packages.props` — inside the existing `<ItemGroup Label="Persistence">` block already containing `Meziantou.Framework.Win32.CredentialManager`, add:

```xml
<PackageVersion Include="Xunit.SkippableFact" Version="1.5.23" />
```

- [ ] **Step 2: Wire up the Persistence csproj dependencies**

Replace the contents of `src/DbDelta.Persistence/DbDelta.Persistence.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="Meziantou.Framework.Win32.CredentialManager" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\DbDelta.Core\DbDelta.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `DbDelta.Persistence.UnitTests.csproj`**

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
  <ItemGroup>
    <ProjectReference Include="..\..\src\DbDelta.Persistence\DbDelta.Persistence.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create `DbDelta.Persistence.IntegrationTests.csproj`**

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
    <PackageReference Include="Xunit.SkippableFact" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\DbDelta.Persistence\DbDelta.Persistence.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create `DbDelta.App.HeadlessTests.csproj`**

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
    <PackageReference Include="Avalonia.Headless.XUnit" />
    <PackageReference Include="Avalonia.Themes.Fluent" />
    <PackageReference Include="Avalonia.Controls.DataGrid" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\DbDelta.App.Avalonia\DbDelta.App.Avalonia.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Add new projects to the solution**

```bash
dotnet sln DbDelta.sln add \
  tests/DbDelta.Persistence.UnitTests/DbDelta.Persistence.UnitTests.csproj \
  tests/DbDelta.Persistence.IntegrationTests/DbDelta.Persistence.IntegrationTests.csproj \
  tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj
```

- [ ] **Step 7: Widen the Architecture.Tests allow-list for Persistence**

Open `tests/DbDelta.Architecture.Tests/LayeringTests.cs`. Locate the test:

```csharp
public void Providers_LiveDb_may_reference_SqlClient_and_Core_only()
```

Add a sibling test below it inside the same class:

```csharp
    [Fact]
    public void Persistence_may_reference_Core_SqlClient_Meziantou_only()
    {
        Assembly persistence = Assembly.Load("DbDelta.Persistence");
        NetArchTest.Rules.TestResult result = Types.InAssembly(persistence)
            .Should()
            .HaveDependencyOnAny(
                "DbDelta.Core",
                "Microsoft.Data.SqlClient",
                "Meziantou.Framework.Win32",
                "System")
            .GetResult();
        result.IsSuccessful.Should().BeTrue(
            "Persistence may reference Core + SqlClient + Meziantou + BCL only. Offenders: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }
```

- [ ] **Step 8: Add the new test projects to the Windows CI job**

Open `.github/workflows/ci.yml`. Replace the `Run non-DB tests` step body with:

```yaml
      - name: Run non-DB tests
        run: |
          dotnet test tests/DbDelta.Core.UnitTests --no-build --configuration Release --logger "trx;LogFileName=core.trx"
          dotnet test tests/DbDelta.Architecture.Tests --no-build --configuration Release --logger "trx;LogFileName=arch.trx"
          dotnet test tests/DbDelta.ScriptGen.GoldenTests --no-build --configuration Release --logger "trx;LogFileName=golden.trx"
          dotnet test tests/DbDelta.Persistence.UnitTests --no-build --configuration Release --logger "trx;LogFileName=persistence.trx"
          dotnet test tests/DbDelta.Persistence.IntegrationTests --no-build --configuration Release --logger "trx;LogFileName=dpapi.trx"
          dotnet test tests/DbDelta.App.HeadlessTests --no-build --configuration Release --logger "trx;LogFileName=headless.trx"
```

- [ ] **Step 9: Build the whole solution to confirm scaffolding compiles**

Run: `dotnet build --configuration Release -warnaserror`
Expected: 0 warnings, 0 errors (the test projects are empty — they'll build but discover zero tests).

- [ ] **Step 10: Commit**

```bash
git add Directory.Packages.props \
        src/DbDelta.Persistence/DbDelta.Persistence.csproj \
        tests/DbDelta.Persistence.UnitTests \
        tests/DbDelta.Persistence.IntegrationTests \
        tests/DbDelta.App.HeadlessTests \
        tests/DbDelta.Architecture.Tests/LayeringTests.cs \
        DbDelta.sln \
        .github/workflows/ci.yml
git commit -m "$(cat <<'EOF'
chore(m11-bis): scaffold three new test projects + Persistence csproj deps

- DbDelta.Persistence.UnitTests: xUnit v3 + FluentAssertions for the
  pure helpers + JSON / XML stores.
- DbDelta.Persistence.IntegrationTests: SkippableFact-powered DPAPI
  round-trip tests; skipped automatically on non-Windows CI runners.
- DbDelta.App.HeadlessTests: Avalonia.Headless.XUnit boilerplate for
  ViewModel testing (no real window, in-process renderer).
- Persistence csproj now references SqlClient + Meziantou (the existing
  central-package pins cover both).
- Architecture.Tests gains a layering rule confirming Persistence only
  pulls Core + SqlClient + Meziantou + BCL.
- CI workflow runs all three new projects in the windows-build job.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase B — Core abstractions (1 task)

### Task T11.2: Add `ICredentialStore` / `IConnectionStore` / `IProjectStore` ports + DTO records

**Files:**
- Create: `src/DbDelta.Core/Abstractions/ICredentialStore.cs`
- Create: `src/DbDelta.Core/Abstractions/IConnectionStore.cs`
- Create: `src/DbDelta.Core/Abstractions/IProjectStore.cs`
- Create: `src/DbDelta.Core/Abstractions/ConnectionEntry.cs`
- Create: `src/DbDelta.Core/Abstractions/DbDeltaProject.cs`

- [ ] **Step 1: `ICredentialStore.cs`**

```csharp
namespace DbDelta.Core.Abstractions;

/// <summary>
/// Per-host secret store. Implementations: Windows DPAPI (ships), macOS
/// Keychain + Linux Secret Service (placeholders until the Avalonia
/// cross-platform distribution is live).
/// </summary>
public interface ICredentialStore
{
    /// <summary>True when this backend can actually persist secrets on the current host.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns the stored secret, or <c>null</c> when not present.</summary>
    System.Threading.Tasks.Task<string?> GetSecretAsync(string targetKey, System.Threading.CancellationToken ct);

    /// <summary>Creates or overwrites a secret under <paramref name="targetKey"/>.</summary>
    System.Threading.Tasks.Task SetSecretAsync(string targetKey, string secret, System.Threading.CancellationToken ct);

    /// <summary>Removes a secret. No-op when absent.</summary>
    System.Threading.Tasks.Task DeleteSecretAsync(string targetKey, System.Threading.CancellationToken ct);
}
```

- [ ] **Step 2: `ConnectionEntry.cs`**

```csharp
namespace DbDelta.Core.Abstractions;

/// <summary>
/// A user-saved connection. The password is NOT carried here — it lives in
/// the paired <see cref="ICredentialStore"/> under the key
/// <c>"dbdelta:connection:{Id:D}"</c>.
/// </summary>
public sealed record ConnectionEntry(
    System.Guid Id,
    string Name,
    string ServerName,
    string DatabaseName,
    string ConnectionStringTemplate,
    string EnvironmentTag,
    string EnvironmentColorHex,
    bool IsPinned,
    System.DateTime CreatedUtc,
    System.DateTime LastUsedUtc);
```

- [ ] **Step 3: `IConnectionStore.cs`**

```csharp
namespace DbDelta.Core.Abstractions;

/// <summary>
/// Per-user MRU + preset list. Implementation persists to
/// <c>%LOCALAPPDATA%\DbDelta\connections.json</c> on Windows
/// (equivalent paths on macOS / Linux).
/// </summary>
public interface IConnectionStore
{
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ConnectionEntry>> LoadAsync(System.Threading.CancellationToken ct);

    /// <summary>Inserts a new entry or replaces an existing one by <see cref="ConnectionEntry.Id"/>.</summary>
    System.Threading.Tasks.Task<ConnectionEntry> UpsertAsync(ConnectionEntry entry, System.Threading.CancellationToken ct);

    System.Threading.Tasks.Task DeleteAsync(System.Guid id, System.Threading.CancellationToken ct);

    /// <summary>Bumps <see cref="ConnectionEntry.LastUsedUtc"/> to <c>UtcNow</c>.</summary>
    System.Threading.Tasks.Task TouchUsageAsync(System.Guid id, System.Threading.CancellationToken ct);
}
```

- [ ] **Step 4: `DbDeltaProject.cs`**

```csharp
using DbDelta.Core.Options;

namespace DbDelta.Core.Abstractions;

/// <summary>
/// Shareable artefact: a comparison project. References two
/// <see cref="ConnectionEntry"/> ids (kept stable across rename).
/// </summary>
public sealed record DbDeltaProject(
    string Name,
    System.Guid SourceConnectionId,
    System.Guid TargetConnectionId,
    ComparisonOptions Options,
    System.Collections.Generic.IReadOnlyList<string>? SelectedObjects = null);
```

- [ ] **Step 5: `IProjectStore.cs`**

```csharp
namespace DbDelta.Core.Abstractions;

/// <summary>
/// Reads / writes <c>.dbd</c> project files (XML).
/// </summary>
public interface IProjectStore
{
    System.Threading.Tasks.Task<DbDeltaProject> LoadAsync(string filePath, System.Threading.CancellationToken ct);
    System.Threading.Tasks.Task SaveAsync(string filePath, DbDeltaProject project, System.Threading.CancellationToken ct);
}
```

- [ ] **Step 6: Build + commit**

```
dotnet build src/DbDelta.Core -warnaserror
```
Expected: 0 warnings.

```bash
git add src/DbDelta.Core/Abstractions/ICredentialStore.cs \
        src/DbDelta.Core/Abstractions/IConnectionStore.cs \
        src/DbDelta.Core/Abstractions/IProjectStore.cs \
        src/DbDelta.Core/Abstractions/ConnectionEntry.cs \
        src/DbDelta.Core/Abstractions/DbDeltaProject.cs
git commit -m "$(cat <<'EOF'
feat(core): add ICredentialStore + IConnectionStore + IProjectStore ports

Pure data records + ports. Implementations land in DbDelta.Persistence
in the following tasks. Core stays I/O-free; the NetArchTest layering
rule continues to pass.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase C — Persistence implementations (8 tasks)

### Task T11.3: `ICredentialStore` Windows backend skeleton

Builds the Persistence-side type before the DPAPI integration test in T11.5.

**Files:**
- Create: `src/DbDelta.Persistence/Credentials/DpapiCredentialStore.cs`

- [ ] **Step 1: Create the file**

```csharp
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;
using Meziantou.Framework.Win32;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// Windows DPAPI-backed credential store. Uses
/// <see cref="CredentialManager"/> with CurrentUser scope — secrets are
/// encrypted with the user's logon key and not accessible to other users
/// on the same machine.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiCredentialStore : ICredentialStore
{
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public Task<string?> GetSecretAsync(string targetKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        Credential? cred = CredentialManager.ReadCredential(targetKey);
        return Task.FromResult(cred?.Password);
    }

    public Task SetSecretAsync(string targetKey, string secret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentNullException.ThrowIfNull(secret);
        CredentialManager.WriteCredential(
            applicationName: targetKey,
            userName: "dbdelta",
            secret: secret,
            persistence: CredentialPersistence.LocalMachine);
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string targetKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        CredentialManager.DeleteCredential(targetKey);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build src/DbDelta.Persistence -warnaserror
```
Expected: 0 warnings. The `[SupportedOSPlatform("windows")]` annotation gates the file against the analyzer for cross-platform safety.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Persistence/Credentials/DpapiCredentialStore.cs
git commit -m "$(cat <<'EOF'
feat(persistence): DpapiCredentialStore — Windows DPAPI backend

CurrentUser-scoped via Meziantou's CredentialManager wrapper. Marked
[SupportedOSPlatform("windows")] so the cross-platform-safety analyser
flags any accidental cross-target reference.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.4: `ConnectionStringRedactor` + unit tests

**Files:**
- Create: `src/DbDelta.Persistence/Util/ConnectionStringRedactor.cs`
- Create: `tests/DbDelta.Persistence.UnitTests/Util/ConnectionStringRedactorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/DbDelta.Persistence.UnitTests/Util/ConnectionStringRedactorTests.cs
using DbDelta.Persistence.Util;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Util;

public class ConnectionStringRedactorTests
{
    [Fact]
    public void Null_input_returns_empty()
    {
        ConnectionStringRedactor.Redact(null).Should().Be(string.Empty);
    }

    [Fact]
    public void Lowercase_password_keyword_is_redacted()
    {
        ConnectionStringRedactor.Redact("Server=x;password=Secret123;Database=y")
            .Should().Be("Server=x;password=***;Database=y");
    }

    [Fact]
    public void Uppercase_Password_keyword_is_redacted_case_insensitive()
    {
        ConnectionStringRedactor.Redact("Server=x;Password=Hello;Database=y")
            .Should().Be("Server=x;Password=***;Database=y");
    }

    [Fact]
    public void Pwd_alias_is_redacted()
    {
        ConnectionStringRedactor.Redact("Server=x;Pwd=Hello;Database=y")
            .Should().Be("Server=x;Pwd=***;Database=y");
    }

    [Fact]
    public void Password_with_special_chars_is_redacted_to_semicolon()
    {
        ConnectionStringRedactor.Redact("Server=x;Password=N0tAr3al!Pwd#Fake;Database=y")
            .Should().Be("Server=x;Password=***;Database=y");
    }

    [Fact]
    public void Connection_string_without_password_is_unchanged()
    {
        const string s = "Server=x;Database=y;Integrated Security=True";
        ConnectionStringRedactor.Redact(s).Should().Be(s);
    }
}
```

- [ ] **Step 2: Run — verify FAIL (compile)**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter ConnectionStringRedactorTests`
Expected: compile error (`ConnectionStringRedactor` missing).

- [ ] **Step 3: Create the helper**

```csharp
// src/DbDelta.Persistence/Util/ConnectionStringRedactor.cs
using System.Text.RegularExpressions;

namespace DbDelta.Persistence.Util;

/// <summary>
/// Pure helper that masks <c>password=...</c> / <c>pwd=...</c> values
/// inside a connection string so the result can be safely shown to the
/// user or written to a log. Case-insensitive; preserves the original
/// keyword's case for cosmetics.
/// </summary>
public static partial class ConnectionStringRedactor
{
    [GeneratedRegex(@"(?i)(password|pwd)\s*=\s*[^;]+", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    public static string Redact(string? value) =>
        value is null ? string.Empty : PasswordPattern().Replace(value, "$1=***");
}
```

- [ ] **Step 4: Run tests — verify GREEN**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter ConnectionStringRedactorTests`
Expected: 6 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Persistence/Util/ConnectionStringRedactor.cs \
        tests/DbDelta.Persistence.UnitTests/Util/ConnectionStringRedactorTests.cs
git commit -m "$(cat <<'EOF'
feat(persistence/util): ConnectionStringRedactor — mask password / pwd

Source-generated regex masks both spellings, case-insensitive, while
preserving the keyword's original case for cosmetics. Covers the
diagnostic banner case (UI), the structured-log case (Serilog), and
any future error message that surfaces a connection string.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.5: DPAPI integration tests (Windows-only `SkippableFact`)

**Files:**
- Create: `tests/DbDelta.Persistence.IntegrationTests/Credentials/DpapiCredentialStoreTests.cs`

- [ ] **Step 1: Write the integration tests**

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Credentials;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.IntegrationTests.Credentials;

public class DpapiCredentialStoreTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [SkippableFact]
    public async Task Set_then_Get_returns_the_stored_secret()
    {
        Skip.IfNot(IsWindows, "DPAPI is Windows-only.");

        ICredentialStore store = new DpapiCredentialStore();
        string key = $"dbdelta:test:{Guid.NewGuid():N}";
        try
        {
            await store.SetSecretAsync(key, "Hello123!", CancellationToken.None);
            string? back = await store.GetSecretAsync(key, CancellationToken.None);
            back.Should().Be("Hello123!");
        }
        finally
        {
            await store.DeleteSecretAsync(key, CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Get_unknown_key_returns_null()
    {
        Skip.IfNot(IsWindows, "DPAPI is Windows-only.");
        ICredentialStore store = new DpapiCredentialStore();
        string? back = await store.GetSecretAsync($"dbdelta:test:nokey-{Guid.NewGuid():N}", CancellationToken.None);
        back.Should().BeNull();
    }

    [SkippableFact]
    public async Task Delete_is_noop_when_key_absent()
    {
        Skip.IfNot(IsWindows, "DPAPI is Windows-only.");
        ICredentialStore store = new DpapiCredentialStore();
        Func<Task> act = () => store.DeleteSecretAsync($"dbdelta:test:noop-{Guid.NewGuid():N}", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
```

- [ ] **Step 2: Run on the Windows host**

Run: `dotnet test tests/DbDelta.Persistence.IntegrationTests --filter DpapiCredentialStoreTests`
Expected: 3 PASS on Windows. On Linux CI the tests are reported as SKIPPED, not failed.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Persistence.IntegrationTests/Credentials/DpapiCredentialStoreTests.cs
git commit -m "$(cat <<'EOF'
test(persistence/credentials): DPAPI round-trip via SkippableFact

Three tests cover Set/Get round-trip, Get-missing returns null, and
Delete-missing is a no-op. SkippableFact gates them to Windows hosts
so the linux-integration-tests CI job reports them as skipped.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.6: macOS + Linux credential-store placeholders + `CredentialStoreFactory`

**Files:**
- Create: `src/DbDelta.Persistence/Credentials/KeychainCredentialStore.cs`
- Create: `src/DbDelta.Persistence/Credentials/SecretServiceCredentialStore.cs`
- Create: `src/DbDelta.Persistence/Credentials/CredentialStoreFactory.cs`

- [ ] **Step 1: `KeychainCredentialStore.cs`**

```csharp
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// macOS Keychain placeholder. Lights up in v2 when the Avalonia
/// cross-platform distribution activates.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class KeychainCredentialStore : ICredentialStore
{
    public bool IsAvailable => false;

    public Task<string?> GetSecretAsync(string targetKey, CancellationToken ct) =>
        throw new NotSupportedException("macOS Keychain credential store ships in v2.");

    public Task SetSecretAsync(string targetKey, string secret, CancellationToken ct) =>
        throw new NotSupportedException("macOS Keychain credential store ships in v2.");

    public Task DeleteSecretAsync(string targetKey, CancellationToken ct) =>
        throw new NotSupportedException("macOS Keychain credential store ships in v2.");
}
```

- [ ] **Step 2: `SecretServiceCredentialStore.cs`**

```csharp
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// Linux libsecret / Secret Service placeholder. Lights up in v2.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class SecretServiceCredentialStore : ICredentialStore
{
    public bool IsAvailable => false;

    public Task<string?> GetSecretAsync(string targetKey, CancellationToken ct) =>
        throw new NotSupportedException("Linux Secret Service credential store ships in v2.");

    public Task SetSecretAsync(string targetKey, string secret, CancellationToken ct) =>
        throw new NotSupportedException("Linux Secret Service credential store ships in v2.");

    public Task DeleteSecretAsync(string targetKey, CancellationToken ct) =>
        throw new NotSupportedException("Linux Secret Service credential store ships in v2.");
}
```

- [ ] **Step 3: `CredentialStoreFactory.cs`**

```csharp
using System;
using System.Runtime.InteropServices;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// Returns the credential store implementation that matches the current host OS.
/// Throws <see cref="PlatformNotSupportedException"/> on exotic platforms.
/// </summary>
public static class CredentialStoreFactory
{
    public static ICredentialStore Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DpapiCredentialStore();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new KeychainCredentialStore();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new SecretServiceCredentialStore();
        }
        throw new PlatformNotSupportedException(
            $"No credential store implementation for {RuntimeInformation.OSDescription}.");
    }
}
```

- [ ] **Step 4: Build**

```
dotnet build src/DbDelta.Persistence -warnaserror
```
Expected: 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Persistence/Credentials/KeychainCredentialStore.cs \
        src/DbDelta.Persistence/Credentials/SecretServiceCredentialStore.cs \
        src/DbDelta.Persistence/Credentials/CredentialStoreFactory.cs
git commit -m "$(cat <<'EOF'
feat(persistence/credentials): factory + macOS/Linux placeholders

CredentialStoreFactory.Create() picks the right backend per OS so the
App composes against the abstraction. macOS Keychain + Linux Secret
Service throw NotSupportedException with IsAvailable=false; the
Avalonia cross-platform distribution swap-in will be a single-file
change in v2.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.7: `JsonConnectionStore` core CRUD + atomic write tests

**Files:**
- Create: `src/DbDelta.Persistence/Json/JsonConnectionStore.cs`
- Create: `tests/DbDelta.Persistence.UnitTests/Json/JsonConnectionStoreTests.cs`

- [ ] **Step 1: Write failing tests (Part 1 — CRUD + ordering + atomicity)**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Json;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Json;

public class JsonConnectionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public JsonConnectionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dbdelta-conn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "connections.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private JsonConnectionStore CreateStore() => new(_file);

    private static ConnectionEntry MakeEntry(string name, Guid? id = null, bool pinned = false) =>
        new(
            Id: id ?? Guid.NewGuid(),
            Name: name,
            ServerName: "srv",
            DatabaseName: "db",
            ConnectionStringTemplate: "Server=srv;Database=db;User Id=sa;Password={PASSWORD};TrustServerCertificate=True",
            EnvironmentTag: "Dev",
            EnvironmentColorHex: "#0054BD",
            IsPinned: pinned,
            CreatedUtc: DateTime.UtcNow,
            LastUsedUtc: DateTime.UtcNow);

    [Fact]
    public async Task Load_returns_empty_when_file_absent()
    {
        IReadOnlyList<ConnectionEntry> entries = await CreateStore().LoadAsync(CancellationToken.None);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_then_Load_round_trips_one_entry()
    {
        JsonConnectionStore store = CreateStore();
        ConnectionEntry e = MakeEntry("Dev DB");
        await store.UpsertAsync(e, CancellationToken.None);
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().ContainSingle().Which.Should().Be(e);
    }

    [Fact]
    public async Task Upsert_with_same_Id_replaces_existing_entry()
    {
        JsonConnectionStore store = CreateStore();
        Guid id = Guid.NewGuid();
        await store.UpsertAsync(MakeEntry("Before", id), CancellationToken.None);
        await store.UpsertAsync(MakeEntry("After", id), CancellationToken.None);
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().ContainSingle().Which.Name.Should().Be("After");
    }

    [Fact]
    public async Task Delete_removes_by_Id()
    {
        JsonConnectionStore store = CreateStore();
        ConnectionEntry e = MakeEntry("ToDelete");
        await store.UpsertAsync(e, CancellationToken.None);
        await store.DeleteAsync(e.Id, CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Touch_bumps_LastUsedUtc()
    {
        JsonConnectionStore store = CreateStore();
        Guid id = Guid.NewGuid();
        DateTime t0 = DateTime.UtcNow.AddMinutes(-30);
        await store.UpsertAsync(MakeEntry("E", id) with { CreatedUtc = t0, LastUsedUtc = t0 }, CancellationToken.None);
        await Task.Delay(20);
        await store.TouchUsageAsync(id, CancellationToken.None);
        ConnectionEntry back = (await store.LoadAsync(CancellationToken.None)).Single();
        back.LastUsedUtc.Should().BeAfter(t0);
    }

    [Fact]
    public async Task Atomic_write_does_not_leave_tmp_file()
    {
        JsonConnectionStore store = CreateStore();
        await store.UpsertAsync(MakeEntry("E"), CancellationToken.None);
        Directory.GetFiles(_dir).Should().NotContain(p => p.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Load_returns_pinned_first_then_LastUsed_desc()
    {
        JsonConnectionStore store = CreateStore();
        DateTime now = DateTime.UtcNow;
        await store.UpsertAsync(MakeEntry("Old")     with { LastUsedUtc = now.AddDays(-7) }, CancellationToken.None);
        await store.UpsertAsync(MakeEntry("Recent")  with { LastUsedUtc = now },             CancellationToken.None);
        await store.UpsertAsync(MakeEntry("Pinned", pinned: true) with { LastUsedUtc = now.AddDays(-30) }, CancellationToken.None);
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Select(e => e.Name).Should().ContainInOrder("Pinned", "Recent", "Old");
    }
}
```

- [ ] **Step 2: Verify FAIL**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter JsonConnectionStoreTests`
Expected: compile error.

- [ ] **Step 3: Create `JsonConnectionStore`**

```csharp
// src/DbDelta.Persistence/Json/JsonConnectionStore.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Json;

/// <summary>
/// Persists the per-user connection list as JSON in
/// <c>%LOCALAPPDATA%\DbDelta\connections.json</c>. Writes are atomic
/// (write-temp + rename) so a crash never corrupts the file.
/// </summary>
public sealed class JsonConnectionStore : IConnectionStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public JsonConnectionStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// Convenience ctor that resolves the default per-user path
    /// (<c>LocalApplicationData/DbDelta/connections.json</c>).
    /// </summary>
    public static JsonConnectionStore CreateDefault()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbDelta");
        Directory.CreateDirectory(dir);
        return new JsonConnectionStore(Path.Combine(dir, "connections.json"));
    }

    public async Task<IReadOnlyList<ConnectionEntry>> LoadAsync(CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        return doc.Entries
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.LastUsedUtc)
            .ThenByDescending(e => e.CreatedUtc)
            .ToArray();
    }

    public async Task<ConnectionEntry> UpsertAsync(ConnectionEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Where(e => e.Id != entry.Id), entry];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
        return entry;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Where(e => e.Id != id)];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
    }

    public async Task TouchUsageAsync(Guid id, CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Select(e =>
            e.Id == id ? e with { LastUsedUtc = DateTime.UtcNow } : e)];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
    }

    private async Task<Document> ReadDocumentAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Document(CurrentSchemaVersion, []);
        }
        await using FileStream fs = File.OpenRead(_filePath);
        try
        {
            Document? doc = await JsonSerializer.DeserializeAsync<Document>(fs, s_json, ct).ConfigureAwait(false);
            return doc ?? new Document(CurrentSchemaVersion, []);
        }
        catch (JsonException)
        {
            // Caller may want to recover; surface the on-disk path so the app can rename it.
            throw;
        }
    }

    private async Task WriteAtomicAsync(Document doc, CancellationToken ct)
    {
        string tmp = _filePath + ".tmp";
        await using (FileStream fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, doc, s_json, ct).ConfigureAwait(false);
        }
        // File.Move overwrites atomically on Windows + POSIX since .NET 5.
        File.Move(tmp, _filePath, overwrite: true);
    }

    private sealed record Document(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("entries")] IReadOnlyList<ConnectionEntry> Entries);
}
```

- [ ] **Step 4: Run + verify GREEN**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter JsonConnectionStoreTests`
Expected: 7 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Persistence/Json/JsonConnectionStore.cs \
        tests/DbDelta.Persistence.UnitTests/Json/JsonConnectionStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(persistence/json): JsonConnectionStore — atomic CRUD with sort

- Load returns Pinned-first / LastUsedUtc DESC / CreatedUtc DESC.
- Upsert / Delete / TouchUsageAsync round-trip + atomic write
  (write-temp + File.Move(overwrite:true)).
- CreateDefault() resolves the per-user path under
  LocalApplicationData/DbDelta/ and creates the directory.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.8: `JsonConnectionStore` corruption tolerance

**Files:**
- Modify: `src/DbDelta.Persistence/Json/JsonConnectionStore.cs`
- Modify: `tests/DbDelta.Persistence.UnitTests/Json/JsonConnectionStoreTests.cs`

- [ ] **Step 1: Append failing tests**

Append inside the `JsonConnectionStoreTests` class:

```csharp
    [Fact]
    public async Task Corrupted_file_is_renamed_aside_and_load_returns_empty()
    {
        await File.WriteAllTextAsync(_file, "{ this is not valid json", CancellationToken.None);
        JsonConnectionStore store = CreateStore();
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().BeEmpty();
        Directory.GetFiles(_dir).Should().ContainSingle(p => p.Contains(".broken-"));
    }

    [Fact]
    public async Task Future_schema_version_renames_aside_and_returns_empty()
    {
        await File.WriteAllTextAsync(_file, """{"schemaVersion":999,"entries":[]}""", CancellationToken.None);
        JsonConnectionStore store = CreateStore();
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().BeEmpty();
        Directory.GetFiles(_dir).Should().ContainSingle(p => p.Contains(".broken-"));
    }
```

- [ ] **Step 2: Verify FAIL**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter JsonConnectionStoreTests`
Expected: 2 of the new tests FAIL — current `ReadDocumentAsync` propagates `JsonException` and ignores schema version.

- [ ] **Step 3: Patch `ReadDocumentAsync`**

In `JsonConnectionStore.cs`, replace the body of `ReadDocumentAsync` with:

```csharp
    private async Task<Document> ReadDocumentAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Document(CurrentSchemaVersion, []);
        }
        try
        {
            await using FileStream fs = File.OpenRead(_filePath);
            Document? doc = await JsonSerializer.DeserializeAsync<Document>(fs, s_json, ct).ConfigureAwait(false);
            if (doc is null)
            {
                return new Document(CurrentSchemaVersion, []);
            }
            if (doc.SchemaVersion > CurrentSchemaVersion)
            {
                MoveAside("future-schema");
                return new Document(CurrentSchemaVersion, []);
            }
            return doc;
        }
        catch (JsonException)
        {
            MoveAside("invalid-json");
            return new Document(CurrentSchemaVersion, []);
        }
    }

    private void MoveAside(string reason)
    {
        string aside = _filePath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + reason;
        try
        {
            File.Move(_filePath, aside, overwrite: false);
        }
        catch (IOException)
        {
            // Another instance may have moved it already. Best-effort.
        }
    }
```

- [ ] **Step 4: Run tests + commit**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter JsonConnectionStoreTests`
Expected: all 9 PASS.

```bash
git add src/DbDelta.Persistence/Json/JsonConnectionStore.cs \
        tests/DbDelta.Persistence.UnitTests/Json/JsonConnectionStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(persistence/json): corruption + future-schema tolerance

- Invalid JSON → file renamed to .broken-{ts}-invalid-json, load
  returns empty.
- schemaVersion > current → file renamed to .broken-{ts}-future-schema,
  load returns empty.
- Best-effort rename — never deletes data, never throws.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.9: File permission hardening on POSIX hosts (best-effort)

This is a tiny defensive step so the on-disk file is not world-readable when the user happens to be on Linux/macOS. Windows ACL already restricts to the owning user via `LocalApplicationData`.

**Files:**
- Modify: `src/DbDelta.Persistence/Json/JsonConnectionStore.cs`

- [ ] **Step 1: Patch `WriteAtomicAsync` to set POSIX 0600 on POSIX hosts**

Replace `WriteAtomicAsync` with:

```csharp
    private async Task WriteAtomicAsync(Document doc, CancellationToken ct)
    {
        string tmp = _filePath + ".tmp";
        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            // 0600 — owner read/write only.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using (FileStream fs = new(tmp, options))
        {
            await JsonSerializer.SerializeAsync(fs, doc, s_json, ct).ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }
```

- [ ] **Step 2: Build + run tests**

```
dotnet build src/DbDelta.Persistence -warnaserror
dotnet test tests/DbDelta.Persistence.UnitTests --filter JsonConnectionStoreTests
```
Expected: 0 warnings; 9 PASS. (POSIX mode is set on Linux/macOS only; Windows path unchanged.)

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Persistence/Json/JsonConnectionStore.cs
git commit -m "$(cat <<'EOF'
chore(persistence/json): set UnixFileMode 0600 on POSIX hosts

Defensive — Windows already restricts %LOCALAPPDATA% to the user;
this stops the file landing world-readable when the app eventually
ships on Linux/macOS.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.10: `XmlProjectStore` + round-trip tests

**Files:**
- Create: `src/DbDelta.Persistence/Xml/XmlProjectStore.cs`
- Create: `tests/DbDelta.Persistence.UnitTests/Xml/XmlProjectStoreTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Options;
using DbDelta.Persistence.Xml;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Xml;

public class XmlProjectStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public XmlProjectStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dbdelta-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "demo.dbd");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Save_then_Load_round_trips_all_fields()
    {
        XmlProjectStore store = new();
        DbDeltaProject project = new(
            Name: "Customer rollout",
            SourceConnectionId: Guid.Parse("9f2c1d76-1111-1111-1111-111111111111"),
            TargetConnectionId: Guid.Parse("3a55ee99-2222-2222-2222-222222222222"),
            Options: ComparisonOptions.Default,
            SelectedObjects: new[] { "dbo.Customer", "dbo.vCustomer" });

        await store.SaveAsync(_file, project, CancellationToken.None);
        DbDeltaProject back = await store.LoadAsync(_file, CancellationToken.None);

        back.Name.Should().Be(project.Name);
        back.SourceConnectionId.Should().Be(project.SourceConnectionId);
        back.TargetConnectionId.Should().Be(project.TargetConnectionId);
        back.Options.Should().Be(project.Options);
        back.SelectedObjects.Should().BeEquivalentTo(project.SelectedObjects);
    }

    [Fact]
    public async Task Save_writes_canonical_namespace()
    {
        XmlProjectStore store = new();
        DbDeltaProject project = new(
            Name: "x",
            SourceConnectionId: Guid.NewGuid(),
            TargetConnectionId: Guid.NewGuid(),
            Options: ComparisonOptions.Default,
            SelectedObjects: null);
        await store.SaveAsync(_file, project, CancellationToken.None);
        string text = await File.ReadAllTextAsync(_file, CancellationToken.None);
        text.Should().Contain("xmlns=\"https://schemas.dbdelta.org/project/v1\"");
    }
}
```

- [ ] **Step 2: Verify FAIL**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter XmlProjectStoreTests`
Expected: compile error.

- [ ] **Step 3: Create `XmlProjectStore.cs`**

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Options;

namespace DbDelta.Persistence.Xml;

/// <summary>
/// Reads / writes the canonical <c>.dbd</c> XML project file.
/// </summary>
public sealed class XmlProjectStore : IProjectStore
{
    private const string Namespace = "https://schemas.dbdelta.org/project/v1";

    public async Task<DbDeltaProject> LoadAsync(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream fs = File.OpenRead(filePath);
        XmlSerializer serializer = new(typeof(Surrogate), defaultNamespace: Namespace);
        Surrogate? doc = (Surrogate?)serializer.Deserialize(fs);
        if (doc is null)
        {
            throw new InvalidDataException($"'{filePath}' is not a valid DbDelta project file.");
        }
        return new DbDeltaProject(
            Name: doc.Name ?? "",
            SourceConnectionId: doc.SourceConnectionId,
            TargetConnectionId: doc.TargetConnectionId,
            Options: doc.Options,
            SelectedObjects: doc.SelectedObjects);
    }

    public async Task SaveAsync(string filePath, DbDeltaProject project, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(project);
        Surrogate doc = new()
        {
            Name = project.Name,
            SourceConnectionId = project.SourceConnectionId,
            TargetConnectionId = project.TargetConnectionId,
            Options = project.Options,
            SelectedObjects = project.SelectedObjects?.ToArray(),
        };
        XmlSerializer serializer = new(typeof(Surrogate), defaultNamespace: Namespace);
        XmlSerializerNamespaces ns = new();
        ns.Add(string.Empty, Namespace);
        XmlWriterSettings settings = new()
        {
            Async = true,
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(false),
        };
        await using FileStream fs = File.Create(filePath);
        await using XmlWriter writer = XmlWriter.Create(fs, settings);
        serializer.Serialize(writer, doc, ns);
        await writer.FlushAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    [XmlRoot("DbDeltaProject", Namespace = Namespace)]
    public sealed class Surrogate
    {
        [XmlAttribute("name")]
        public string? Name { get; set; }
        public Guid SourceConnectionId { get; set; }
        public Guid TargetConnectionId { get; set; }
        public ComparisonOptions Options { get; set; }
        [XmlArrayItem("Item")]
        public string[]? SelectedObjects { get; set; }
    }
}
```

- [ ] **Step 4: Verify GREEN**

Run: `dotnet test tests/DbDelta.Persistence.UnitTests --filter XmlProjectStoreTests`
Expected: 2 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Persistence/Xml/XmlProjectStore.cs \
        tests/DbDelta.Persistence.UnitTests/Xml/XmlProjectStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(persistence/xml): XmlProjectStore — .dbd canonical XML

XmlSerializer-backed reader/writer with canonical namespace
https://schemas.dbdelta.org/project/v1. SelectedObjects round-trips
(future milestones can populate the list without changing the schema).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase D — Inline Test Connection (1 task)

### Task T11.11: `ConnectionTester` + smoke test

**Files:**
- Create: `src/DbDelta.Persistence/Sql/ConnectionTester.cs`
- Create: `tests/DbDelta.Persistence.IntegrationTests/Sql/ConnectionTesterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.IntegrationTests.Sql;

public class ConnectionTesterTests
{
    [Fact]
    public async Task Bad_connection_string_fails_fast_and_returns_failure()
    {
        Stopwatch sw = Stopwatch.StartNew();
        ConnectionTester.TestResult result = await ConnectionTester.TestAsync(
            "Server=tcp:127.0.0.1,59999;Database=NoSuchDb;User Id=sa;Password=wrong;Encrypt=False;Connect Timeout=2",
            CancellationToken.None);
        sw.Stop();
        result.Success.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
        sw.Elapsed.TotalSeconds.Should().BeLessThan(10, "Connect Timeout caps the wait");
    }
}
```

- [ ] **Step 2: Verify FAIL (compile)**

Run: `dotnet test tests/DbDelta.Persistence.IntegrationTests --filter ConnectionTesterTests`
Expected: compile error.

- [ ] **Step 3: Create `ConnectionTester.cs`**

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbDelta.Persistence.Util;

namespace DbDelta.Persistence.Sql;

public static class ConnectionTester
{
    public sealed record TestResult(bool Success, string Message, TimeSpan Latency, string? ServerVersion);

    public static async Task<TestResult> TestAsync(string connectionString, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 10 };
        }
        catch (Exception ex)
        {
            return new TestResult(false, ex.Message, TimeSpan.Zero, null);
        }
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await using SqlConnection cn = new(builder.ConnectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using SqlCommand cmd = new("SELECT @@VERSION", cn);
            object? versionObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            string version = versionObj?.ToString()?.Split('\n')[0].Trim() ?? "(sconosciuta)";
            return new TestResult(true, $"Connesso ({sw.Elapsed.TotalMilliseconds:F0} ms)", sw.Elapsed, version);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(false, ConnectionStringRedactor.Redact(ex.Message), sw.Elapsed, null);
        }
    }
}
```

- [ ] **Step 4: Verify GREEN**

Run: `dotnet test tests/DbDelta.Persistence.IntegrationTests --filter ConnectionTesterTests`
Expected: 1 PASS (no SQL Server needed — the negative case is enough to lock the contract).

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Persistence/Sql/ConnectionTester.cs \
        tests/DbDelta.Persistence.IntegrationTests/Sql/ConnectionTesterTests.cs
git commit -m "$(cat <<'EOF'
feat(persistence/sql): ConnectionTester — Open + SELECT @@VERSION

10s connect timeout; latency reported in ms; error messages run
through ConnectionStringRedactor so a thrown exception never leaks
the password back to the UI banner.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase E — Avalonia headless test harness (1 task)

### Task T11.12: Avalonia headless `TestAppBuilder` boilerplate

**Files:**
- Create: `tests/DbDelta.App.HeadlessTests/TestAppBuilder.cs`

- [ ] **Step 1: Create the boilerplate**

```csharp
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(DbDelta.App.HeadlessTests.TestAppBuilder))]

namespace DbDelta.App.HeadlessTests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}
```

- [ ] **Step 2: Build to confirm the assembly attribute resolves**

```
dotnet build tests/DbDelta.App.HeadlessTests -warnaserror
```
Expected: 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.App.HeadlessTests/TestAppBuilder.cs
git commit -m "$(cat <<'EOF'
test(app/headless): AvaloniaTestApplication boilerplate

Wires the Avalonia.Headless.XUnit runner so subsequent ViewModel /
View tests can use [AvaloniaFact].

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase F — Avalonia view-model integration (5 tasks)

### Task T11.13: `ConnectionStoreViewModel` + autosave hook

**Files:**
- Create: `src/DbDelta.App.Avalonia/ViewModels/ConnectionStoreViewModel.cs`
- Create: `tests/DbDelta.App.HeadlessTests/ConnectionStoreViewModelTests.cs`
- Modify: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs`
- Modify: `src/DbDelta.App.Avalonia/App.axaml.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests;

public class ConnectionStoreViewModelTests
{
    private sealed class InMemoryConnectionStore : IConnectionStore
    {
        private readonly System.Collections.Generic.List<ConnectionEntry> _entries = [];
        public Task<System.Collections.Generic.IReadOnlyList<ConnectionEntry>> LoadAsync(System.Threading.CancellationToken ct) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<ConnectionEntry>>([.. _entries]);
        public Task<ConnectionEntry> UpsertAsync(ConnectionEntry entry, System.Threading.CancellationToken ct)
        {
            _entries.RemoveAll(e => e.Id == entry.Id);
            _entries.Add(entry);
            return Task.FromResult(entry);
        }
        public Task DeleteAsync(Guid id, System.Threading.CancellationToken ct)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }
        public Task TouchUsageAsync(Guid id, System.Threading.CancellationToken ct)
        {
            int i = _entries.FindIndex(e => e.Id == id);
            if (i >= 0)
            {
                _entries[i] = _entries[i] with { LastUsedUtc = DateTime.UtcNow };
            }
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _map = [];
        public bool IsAvailable => true;
        public Task<string?> GetSecretAsync(string key, System.Threading.CancellationToken ct) =>
            Task.FromResult(_map.GetValueOrDefault(key));
        public Task SetSecretAsync(string key, string secret, System.Threading.CancellationToken ct)
        {
            _map[key] = secret;
            return Task.CompletedTask;
        }
        public Task DeleteSecretAsync(string key, System.Threading.CancellationToken ct)
        {
            _map.Remove(key);
            return Task.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task Autosave_creates_one_entry_per_connection_string()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        await vm.LoadAsync(System.Threading.CancellationToken.None);

        await vm.AutosaveAsync(
            "Server=192.168.1.1;Database=Demo;User Id=sa;Password=Hello;TrustServerCertificate=True",
            System.Threading.CancellationToken.None);

        vm.Entries.Should().ContainSingle(e => e.ServerName == "192.168.1.1" && e.DatabaseName == "Demo");
    }

    [AvaloniaFact]
    public async Task Autosave_is_idempotent_for_the_same_connection_string()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        await vm.LoadAsync(System.Threading.CancellationToken.None);
        string cs = "Server=srv;Database=db;User Id=sa;Password=p;TrustServerCertificate=True";
        await vm.AutosaveAsync(cs, System.Threading.CancellationToken.None);
        await vm.AutosaveAsync(cs, System.Threading.CancellationToken.None);
        vm.Entries.Should().HaveCount(1);
    }

    [AvaloniaFact]
    public async Task Filter_by_search_term_matches_Name_or_Server()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        await vm.AutosaveAsync("Server=ProdHost;Database=Demo;User Id=sa;Password=p;", System.Threading.CancellationToken.None);
        await vm.AutosaveAsync("Server=DevHost;Database=Demo;User Id=sa;Password=p;",  System.Threading.CancellationToken.None);

        vm.SearchText = "prod";
        vm.FilteredEntries.Should().ContainSingle();
        vm.FilteredEntries[0].ServerName.Should().Be("ProdHost");
    }
}
```

- [ ] **Step 2: Verify FAIL**

Run: `dotnet test tests/DbDelta.App.HeadlessTests --filter ConnectionStoreViewModelTests`
Expected: compile error.

- [ ] **Step 3: Create `ConnectionStoreViewModel.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DbDelta.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Holds the MRU connection list + the search term + filter, and exposes
/// commands to autosave a raw connection string on Compare success and to
/// materialise a stored entry back into a complete connection string at
/// run time.
/// </summary>
public sealed partial class ConnectionStoreViewModel : ObservableObject
{
    private const string KeyPrefix = "dbdelta:connection:";

    private readonly IConnectionStore _store;
    private readonly ICredentialStore _credentials;

    public ConnectionStoreViewModel(IConnectionStore store, ICredentialStore credentials)
    {
        _store = store;
        _credentials = credentials;
        Entries = [];
    }

    public ObservableCollection<ConnectionEntry> Entries { get; }

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredEntries));

    public IReadOnlyList<ConnectionEntry> FilteredEntries => string.IsNullOrWhiteSpace(SearchText)
        ? [.. Entries]
        : [.. Entries.Where(e =>
            e.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || e.ServerName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || e.DatabaseName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || e.EnvironmentTag.Contains(SearchText, StringComparison.OrdinalIgnoreCase))];

    public async Task LoadAsync(CancellationToken ct)
    {
        IReadOnlyList<ConnectionEntry> list = await _store.LoadAsync(ct).ConfigureAwait(true);
        Entries.Clear();
        foreach (ConnectionEntry e in list)
        {
            Entries.Add(e);
        }
        OnPropertyChanged(nameof(FilteredEntries));
    }

    public async Task<ConnectionEntry?> AutosaveAsync(string rawConnectionString, CancellationToken ct)
    {
        SqlConnectionStringBuilder builder;
        try { builder = new SqlConnectionStringBuilder(rawConnectionString); }
        catch { return null; }

        string server = builder.DataSource ?? "";
        string database = builder.InitialCatalog ?? "";
        if (Entries.FirstOrDefault(e =>
            string.Equals(e.ServerName, server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.DatabaseName, database, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            ConnectionEntry touched = existing with { LastUsedUtc = DateTime.UtcNow };
            await _store.UpsertAsync(touched, ct).ConfigureAwait(true);
            ReplaceInCollection(touched);
            return touched;
        }

        string password = builder.Password ?? "";
        string template = ReplacePassword(rawConnectionString, "{PASSWORD}");
        Guid id = Guid.NewGuid();
        ConnectionEntry entry = new(
            Id: id,
            Name: $"{server}.{database} (auto)",
            ServerName: server,
            DatabaseName: database,
            ConnectionStringTemplate: template,
            EnvironmentTag: "(auto)",
            EnvironmentColorHex: "#9097A0",
            IsPinned: false,
            CreatedUtc: DateTime.UtcNow,
            LastUsedUtc: DateTime.UtcNow);

        if (!string.IsNullOrEmpty(password) && _credentials.IsAvailable)
        {
            await _credentials.SetSecretAsync(KeyPrefix + id.ToString("D"), password, ct).ConfigureAwait(true);
        }
        await _store.UpsertAsync(entry, ct).ConfigureAwait(true);
        Entries.Add(entry);
        OnPropertyChanged(nameof(FilteredEntries));
        return entry;
    }

    public async Task<string?> MaterialiseAsync(ConnectionEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.ConnectionStringTemplate.Contains("{PASSWORD}", StringComparison.Ordinal))
        {
            return entry.ConnectionStringTemplate;
        }
        if (!_credentials.IsAvailable)
        {
            return null;
        }
        string? password = await _credentials.GetSecretAsync(KeyPrefix + entry.Id.ToString("D"), ct).ConfigureAwait(true);
        return password is null ? null : entry.ConnectionStringTemplate.Replace("{PASSWORD}", password, StringComparison.Ordinal);
    }

    private void ReplaceInCollection(ConnectionEntry entry)
    {
        int idx = Entries.ToList().FindIndex(e => e.Id == entry.Id);
        if (idx >= 0)
        {
            Entries[idx] = entry;
        }
        OnPropertyChanged(nameof(FilteredEntries));
    }

    private static string ReplacePassword(string original, string replacement) =>
        System.Text.RegularExpressions.Regex.Replace(
            original,
            @"(?i)(password|pwd)\s*=\s*[^;]+",
            $"$1={replacement}");
}
```

- [ ] **Step 4: Wire into `App.axaml.cs`**

In `OnFrameworkInitializationCompleted`, replace the line `AppStateViewModel appState = new();` with:

```csharp
            ICredentialStore credentials = Persistence.Credentials.CredentialStoreFactory.Create();
            IConnectionStore connectionStore = Persistence.Json.JsonConnectionStore.CreateDefault();
            ConnectionStoreViewModel connections = new(connectionStore, credentials);
            AppStateViewModel appState = new(connections);
            _ = connections.LoadAsync(System.Threading.CancellationToken.None);
```

Then add the matching `using DbDelta.Core.Abstractions;` and `using Persistence = DbDelta.Persistence;` at the top.

- [ ] **Step 5: Modify `AppStateViewModel`**

Replace the head of `AppStateViewModel.cs` so the ctor takes the store + autosave runs on a successful Compare:

```csharp
public sealed partial class AppStateViewModel : ObservableObject
{
    private readonly ConnectionStoreViewModel? _connections;

    public AppStateViewModel(ConnectionStoreViewModel? connections = null)
    {
        _connections = connections;
    }

    public ConnectionStoreViewModel? Connections => _connections;
```

Inside `CompareAsync`, **at the end of the successful branch** (right before `IsBusy = false;`), insert:

```csharp
            if (_connections is not null)
            {
                await _connections.AutosaveAsync(srcCs, ct).ConfigureAwait(true);
                await _connections.AutosaveAsync(tgtCs, ct).ConfigureAwait(true);
            }
```

- [ ] **Step 6: Build + run tests**

```
dotnet build -warnaserror
dotnet test tests/DbDelta.App.HeadlessTests --filter ConnectionStoreViewModelTests
```
Expected: clean build; 3 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/DbDelta.App.Avalonia/ViewModels/ConnectionStoreViewModel.cs \
        src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs \
        src/DbDelta.App.Avalonia/App.axaml.cs \
        tests/DbDelta.App.HeadlessTests/ConnectionStoreViewModelTests.cs
git commit -m "$(cat <<'EOF'
feat(app): ConnectionStoreViewModel + autosave wiring

- ObservableCollection<ConnectionEntry> + search-text filter
  (Name / Server / Database / EnvironmentTag match).
- AutosaveAsync(rawConnectionString) extracts Server + Database via
  SqlConnectionStringBuilder, persists the entry with a {PASSWORD}
  template, and writes the password to ICredentialStore.
- MaterialiseAsync injects the password back at runtime.
- App.OnFrameworkInitializationCompleted builds the singleton via
  CredentialStoreFactory + JsonConnectionStore.CreateDefault().
- AppStateViewModel.CompareAsync autosaves source + target on success.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.14: `ConnectionPickerView` refit — combo + edit + test buttons

**Files:**
- Modify: `src/DbDelta.App.Avalonia/Views/ConnectionPickerView.axaml`
- Create: `src/DbDelta.App.Avalonia/ViewModels/ConnectionPickerSlot.cs`

- [ ] **Step 1: Add `ConnectionPickerSlot` helper VM**

`ConnectionPickerSlot.cs` wraps a single side (source or target) so the XAML can be symmetric:

```csharp
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.ViewModels;

public sealed partial class ConnectionPickerSlot : ObservableObject
{
    private readonly AppStateViewModel _state;
    private readonly bool _isSource;

    public ConnectionPickerSlot(AppStateViewModel state, bool isSource)
    {
        _state = state;
        _isSource = isSource;
    }

    public string ConnectionString
    {
        get => _isSource ? _state.SourceConnectionString : _state.TargetConnectionString;
        set
        {
            if (_isSource) { _state.SourceConnectionString = value; }
            else           { _state.TargetConnectionString = value; }
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private ConnectionEntry? _selectedEntry;

    [ObservableProperty]
    private string? _testResultMessage;

    [ObservableProperty]
    private bool _isTesting;

    async partial void OnSelectedEntryChanged(ConnectionEntry? value)
    {
        if (value is null || _state.Connections is null)
        {
            return;
        }
        string? materialised = await _state.Connections.MaterialiseAsync(value, CancellationToken.None);
        if (materialised is not null)
        {
            ConnectionString = materialised;
        }
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResultMessage = null;
        ConnectionTester.TestResult result = await ConnectionTester.TestAsync(ConnectionString, CancellationToken.None);
        TestResultMessage = result.Success
            ? $"✓ {result.Message}" + (result.ServerVersion is null ? "" : $" — {result.ServerVersion}")
            : $"✗ {result.Message}";
        IsTesting = false;
    }
}
```

- [ ] **Step 2: Expose two slots from `AppStateViewModel`**

In `AppStateViewModel.cs`, add inside the class:

```csharp
    public ConnectionPickerSlot SourceSlot { get; }
    public ConnectionPickerSlot TargetSlot { get; }
```

And in the constructor:

```csharp
    public AppStateViewModel(ConnectionStoreViewModel? connections = null)
    {
        _connections = connections;
        SourceSlot = new ConnectionPickerSlot(this, isSource: true);
        TargetSlot = new ConnectionPickerSlot(this, isSource: false);
    }
```

- [ ] **Step 3: Replace `ConnectionPickerView.axaml` body of each endpoint**

Replace the inner `<StackPanel Spacing="6">` of each endpoint Border with the combo + raw text + test result block. Full new file:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DbDelta.App.ViewModels"
             x:Class="DbDelta.App.Views.ConnectionPickerView"
             x:DataType="vm:AppStateViewModel">

  <StackPanel Spacing="12">
    <TextBlock Text="Connessioni"
               FontSize="20"
               FontWeight="SemiBold"
               Foreground="{StaticResource FgStrongBrush}" />

    <Grid ColumnDefinitions="*,Auto,*" RowDefinitions="*">
      <Border Grid.Column="0" Classes="endpoint endpoint-source" Padding="14">
        <StackPanel Spacing="6" DataContext="{Binding SourceSlot}">
          <TextBlock Text="ORIGINE"
                     FontSize="10" FontWeight="Bold" LetterSpacing="0.6"
                     Foreground="{StaticResource SecondaryBrush}" />
          <ComboBox ItemsSource="{Binding $parent[UserControl].((vm:AppStateViewModel)DataContext).Connections.FilteredEntries}"
                    SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"
                    PlaceholderText="Scegli una connessione recente…"
                    MinHeight="32">
            <ComboBox.ItemTemplate>
              <DataTemplate>
                <StackPanel Orientation="Horizontal" Spacing="8">
                  <Border Width="6" Background="{Binding EnvironmentColorHex}" />
                  <TextBlock Text="{Binding Name}" />
                  <TextBlock Text="{Binding EnvironmentTag}" FontSize="10"
                             Foreground="{StaticResource FgSubtleBrush}" />
                </StackPanel>
              </DataTemplate>
            </ComboBox.ItemTemplate>
          </ComboBox>
          <TextBox Text="{Binding ConnectionString, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                   FontFamily="Cascadia Mono,Consolas,monospace" FontSize="12"
                   Classes="connection-input"
                   PlaceholderText="Server=.;Database=Dev;User Id=sa;Password=…;TrustServerCertificate=True" />
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Button Classes="ghost" Command="{Binding TestConnectionCommand}" IsEnabled="{Binding !IsTesting}">
              <TextBlock Text="Test" />
            </Button>
            <TextBlock Text="{Binding TestResultMessage}" FontSize="11"
                       Foreground="{StaticResource FgMutedBrush}" VerticalAlignment="Center" />
          </StackPanel>
        </StackPanel>
      </Border>

      <Button Grid.Column="1" Classes="swap" Margin="10,0" VerticalAlignment="Center"
              Command="{Binding SwapCommand}" ToolTip.Tip="Inverti origine e destinazione">
        <Path Width="14" Height="14" Stretch="Uniform"
              Stroke="{Binding $parent[Button].Foreground}" StrokeThickness="1.8" StrokeLineCap="Round"
              Data="M 16,4 L 20,8 L 16,12 M 20,8 L 8,8 M 8,20 L 4,16 L 8,12 M 4,16 L 16,16" />
      </Button>

      <Border Grid.Column="2" Classes="endpoint endpoint-target" Padding="14">
        <StackPanel Spacing="6" DataContext="{Binding TargetSlot}">
          <TextBlock Text="DESTINAZIONE"
                     FontSize="10" FontWeight="Bold" LetterSpacing="0.6"
                     Foreground="{StaticResource PrimaryBrush}" />
          <ComboBox ItemsSource="{Binding $parent[UserControl].((vm:AppStateViewModel)DataContext).Connections.FilteredEntries}"
                    SelectedItem="{Binding SelectedEntry, Mode=TwoWay}"
                    PlaceholderText="Scegli una connessione recente…"
                    MinHeight="32">
            <ComboBox.ItemTemplate>
              <DataTemplate>
                <StackPanel Orientation="Horizontal" Spacing="8">
                  <Border Width="6" Background="{Binding EnvironmentColorHex}" />
                  <TextBlock Text="{Binding Name}" />
                  <TextBlock Text="{Binding EnvironmentTag}" FontSize="10"
                             Foreground="{StaticResource FgSubtleBrush}" />
                </StackPanel>
              </DataTemplate>
            </ComboBox.ItemTemplate>
          </ComboBox>
          <TextBox Text="{Binding ConnectionString, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                   FontFamily="Cascadia Mono,Consolas,monospace" FontSize="12"
                   Classes="connection-input"
                   PlaceholderText="Server=.;Database=Prod;User Id=sa;Password=…;TrustServerCertificate=True" />
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Button Classes="ghost" Command="{Binding TestConnectionCommand}" IsEnabled="{Binding !IsTesting}">
              <TextBlock Text="Test" />
            </Button>
            <TextBlock Text="{Binding TestResultMessage}" FontSize="11"
                       Foreground="{StaticResource FgMutedBrush}" VerticalAlignment="Center" />
          </StackPanel>
        </StackPanel>
      </Border>
    </Grid>

    <!-- Compare button + error banner unchanged from the previous version -->
    <StackPanel Orientation="Horizontal" Spacing="12">
      <Button Classes="primary lg" Command="{Binding CompareCommand}" IsEnabled="{Binding !IsBusy}">
        <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
          <ProgressBar IsIndeterminate="True" Width="16" Height="16"
                       IsVisible="{Binding IsBusy}" ShowProgressText="False"
                       Foreground="{StaticResource PrimaryFgBrush}" Background="Transparent" />
          <TextBlock Text="Confronto in corso…" IsVisible="{Binding IsBusy}"
                     Foreground="{StaticResource PrimaryFgBrush}" VerticalAlignment="Center" />
          <Path Width="12" Height="12" IsVisible="{Binding !IsBusy}" Stretch="Uniform"
                Stroke="{StaticResource PrimaryFgBrush}" StrokeThickness="1.8" StrokeLineCap="Round"
                Data="M 5,12 L 19,12 M 13,6 L 19,12 L 13,18" VerticalAlignment="Center" />
          <TextBlock Text="Confronta" IsVisible="{Binding !IsBusy}" VerticalAlignment="Center" />
        </StackPanel>
      </Button>
    </StackPanel>

    <Border Classes="alert alert-danger"
            IsVisible="{Binding LastError, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
            Padding="12">
      <StackPanel Orientation="Horizontal" Spacing="10">
        <Path Width="16" Height="16" Stretch="Uniform"
              Stroke="{StaticResource DangerBrush}" StrokeThickness="1.8"
              Data="M 12,3 A 9,9 0 1 1 11.99,3 M 12,7 L 12,13 M 12,16 L 12.01,16" />
        <StackPanel>
          <TextBlock Text="Confronto fallito." FontWeight="SemiBold"
                     Foreground="{StaticResource DangerFgBrush}" />
          <TextBlock Text="{Binding LastError}" FontSize="12"
                     Foreground="{StaticResource FgMutedBrush}" TextWrapping="Wrap" />
        </StackPanel>
      </StackPanel>
    </Border>
  </StackPanel>
</UserControl>
```

- [ ] **Step 2: Build + smoke**

```
dotnet build src/DbDelta.App.Avalonia -warnaserror
```
Expected: 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.App.Avalonia/ViewModels/ConnectionPickerSlot.cs \
        src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs \
        src/DbDelta.App.Avalonia/Views/ConnectionPickerView.axaml
git commit -m "$(cat <<'EOF'
feat(app): ConnectionPickerView combo + Test button

Each endpoint gains a ComboBox bound to ConnectionStoreViewModel
.FilteredEntries (env-colour stripe + name + tag template) + a Test
button that drives ConnectionTester and prints a one-line result.
Selecting a combo item materialises the password from ICredentialStore
and writes the full connection string back into the raw text box.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.15: Materialise-on-startup of the last-used pair

**Files:**
- Modify: `src/DbDelta.App.Avalonia/App.axaml.cs`

Auto-populate the two raw text boxes with the most recently used (pinned-or-not) source / target on startup so the user does not have to re-paste.

- [ ] **Step 1: Patch `App.axaml.cs`**

Inside `OnFrameworkInitializationCompleted`, after the existing `_ = connections.LoadAsync(...);` add:

```csharp
            _ = LoadAndPrefillAsync(connections, appState);

            static async Task LoadAndPrefillAsync(ConnectionStoreViewModel cs, AppStateViewModel state)
            {
                await cs.LoadAsync(System.Threading.CancellationToken.None);
                if (cs.Entries.Count >= 1)
                {
                    string? src = await cs.MaterialiseAsync(cs.Entries[0], System.Threading.CancellationToken.None);
                    if (src is not null) { state.SourceConnectionString = src; }
                }
                if (cs.Entries.Count >= 2)
                {
                    string? tgt = await cs.MaterialiseAsync(cs.Entries[1], System.Threading.CancellationToken.None);
                    if (tgt is not null) { state.TargetConnectionString = tgt; }
                }
            }
```

- [ ] **Step 2: Build**

```
dotnet build src/DbDelta.App.Avalonia -warnaserror
```
Expected: 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.App.Avalonia/App.axaml.cs
git commit -m "$(cat <<'EOF'
feat(app): pre-fill source + target on startup with most-recent pair

Reads MRU list, materialises the top two entries, sets them as
SourceConnectionString / TargetConnectionString. User can override
immediately by picking from the combo or pasting.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.16: `EnvironmentColorOption` palette + `EnvironmentBadge` UserControl

**Files:**
- Create: `src/DbDelta.App.Avalonia/ViewModels/EnvironmentColorOption.cs`
- Create: `src/DbDelta.App.Avalonia/Views/EnvironmentBadge.axaml`
- Create: `src/DbDelta.App.Avalonia/Views/EnvironmentBadge.axaml.cs`

- [ ] **Step 1: Create `EnvironmentColorOption.cs`**

```csharp
using System.Collections.Generic;

namespace DbDelta.App.ViewModels;

public sealed record EnvironmentColorOption(string Label, string Hex);

public static class EnvironmentColorPalette
{
    public static IReadOnlyList<EnvironmentColorOption> All { get; } =
    [
        new("Cobalt",  "#0054BD"),
        new("Violet",  "#6649BE"),
        new("Emerald", "#007339"),
        new("Amber",   "#AE5C00"),
        new("Crimson", "#B31220"),
        new("Sky",     "#5BB9D1"),
        new("Rose",    "#D957A6"),
        new("Neutral", "#9097A0"),
    ];
}
```

- [ ] **Step 2: Create `EnvironmentBadge.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="DbDelta.App.Views.EnvironmentBadge">
  <Border CornerRadius="3" Padding="6,2"
          Background="{Binding $parent[UserControl].Tag}">
    <TextBlock Text="{Binding $parent[UserControl].Tag, Converter={x:Static StringConverters.IsNotNullOrEmpty}, ConverterParameter=' '}"
               FontSize="10" Foreground="White" />
  </Border>
</UserControl>
```

- [ ] **Step 3: Create `EnvironmentBadge.axaml.cs`**

```csharp
using Avalonia.Controls;

namespace DbDelta.App.Views;

public partial class EnvironmentBadge : UserControl
{
    public EnvironmentBadge() => InitializeComponent();
}
```

- [ ] **Step 4: Build + commit**

```
dotnet build src/DbDelta.App.Avalonia -warnaserror
```
Expected: 0 warnings.

```bash
git add src/DbDelta.App.Avalonia/ViewModels/EnvironmentColorOption.cs \
        src/DbDelta.App.Avalonia/Views/EnvironmentBadge.axaml \
        src/DbDelta.App.Avalonia/Views/EnvironmentBadge.axaml.cs
git commit -m "$(cat <<'EOF'
feat(app): EnvironmentColorPalette + EnvironmentBadge UserControl

Eight swatches sourced from the DbDelta design system tokens. The
edit dialog (next task) consumes the palette; the badge UserControl
is reused by the sidebar quick-switch.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.17: `ConnectionEditViewModel` + validation tests

**Files:**
- Create: `src/DbDelta.App.Avalonia/ViewModels/ConnectionEditViewModel.cs`
- Create: `tests/DbDelta.App.HeadlessTests/ConnectionEditViewModelTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests;

public class ConnectionEditViewModelTests
{
    private static ConnectionEntry SampleEntry() => new(
        Id: Guid.NewGuid(),
        Name: "Dev",
        ServerName: "srv",
        DatabaseName: "db",
        ConnectionStringTemplate: "Server=srv;Database=db;User Id=sa;Password={PASSWORD};TrustServerCertificate=True",
        EnvironmentTag: "Dev",
        EnvironmentColorHex: "#0054BD",
        IsPinned: false,
        CreatedUtc: DateTime.UtcNow,
        LastUsedUtc: DateTime.UtcNow);

    [AvaloniaFact]
    public void Empty_name_is_invalid()
    {
        ConnectionEditViewModel vm = new(SampleEntry(), "p4ssw0rd");
        vm.Name = "";
        vm.IsValid.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Empty_colour_is_invalid()
    {
        ConnectionEditViewModel vm = new(SampleEntry(), "p4ssw0rd");
        vm.EnvironmentColorHex = "";
        vm.IsValid.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Filled_form_is_valid()
    {
        ConnectionEditViewModel vm = new(SampleEntry(), "p4ssw0rd");
        vm.IsValid.Should().BeTrue();
    }

    [AvaloniaFact]
    public void ToEntry_round_trips_id_created_and_overrides_other_fields()
    {
        ConnectionEntry original = SampleEntry() with { CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        ConnectionEditViewModel vm = new(original, "p4ssw0rd")
        {
            Name = "Renamed",
            EnvironmentTag = "Prod",
            EnvironmentColorHex = "#B31220",
            IsPinned = true,
        };
        ConnectionEntry result = vm.ToEntry();
        result.Id.Should().Be(original.Id);
        result.CreatedUtc.Should().Be(original.CreatedUtc);
        result.Name.Should().Be("Renamed");
        result.EnvironmentTag.Should().Be("Prod");
        result.EnvironmentColorHex.Should().Be("#B31220");
        result.IsPinned.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Verify FAIL**

Run: `dotnet test tests/DbDelta.App.HeadlessTests --filter ConnectionEditViewModelTests`
Expected: compile error.

- [ ] **Step 3: Create `ConnectionEditViewModel.cs`**

```csharp
using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using DbDelta.Core.Abstractions;

namespace DbDelta.App.ViewModels;

public sealed partial class ConnectionEditViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTime _createdUtc;

    public ConnectionEditViewModel(ConnectionEntry entry, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _id = entry.Id;
        _createdUtc = entry.CreatedUtc;
        Name = entry.Name;
        ServerName = entry.ServerName;
        DatabaseName = entry.DatabaseName;
        ConnectionStringTemplate = entry.ConnectionStringTemplate;
        EnvironmentTag = entry.EnvironmentTag;
        EnvironmentColorHex = entry.EnvironmentColorHex;
        IsPinned = entry.IsPinned;
        Password = password ?? "";
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private string _databaseName = "";
    [ObservableProperty] private string _connectionStringTemplate = "";
    [ObservableProperty] private string _environmentTag = "";
    [ObservableProperty] private string _environmentColorHex = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isPasswordVisible;

    public IReadOnlyList<EnvironmentColorOption> ColorPalette => EnvironmentColorPalette.All;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(EnvironmentColorHex)
        && EnvironmentColorHex.StartsWith('#');

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsValid));
    partial void OnEnvironmentColorHexChanged(string value) => OnPropertyChanged(nameof(IsValid));

    public ConnectionEntry ToEntry() => new(
        Id: _id,
        Name: Name,
        ServerName: ServerName,
        DatabaseName: DatabaseName,
        ConnectionStringTemplate: ConnectionStringTemplate,
        EnvironmentTag: EnvironmentTag,
        EnvironmentColorHex: EnvironmentColorHex,
        IsPinned: IsPinned,
        CreatedUtc: _createdUtc,
        LastUsedUtc: DateTime.UtcNow);
}
```

- [ ] **Step 4: Run + commit**

```
dotnet test tests/DbDelta.App.HeadlessTests --filter ConnectionEditViewModelTests
```
Expected: 4 PASS.

```bash
git add src/DbDelta.App.Avalonia/ViewModels/ConnectionEditViewModel.cs \
        tests/DbDelta.App.HeadlessTests/ConnectionEditViewModelTests.cs
git commit -m "$(cat <<'EOF'
feat(app): ConnectionEditViewModel + validation

[ObservableProperty]s for Name, Server, Database, ConnectionString
template, Environment tag + colour, Pinned, Password, plus
IsPasswordVisible for reveal-on-hold. ToEntry() preserves Id +
CreatedUtc; updates LastUsedUtc to now.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase G — Dialog + Sidebar + Project file (5 tasks)

### Task T11.18: `ConnectionEditDialog.axaml` (modal form)

**Files:**
- Create: `src/DbDelta.App.Avalonia/Views/ConnectionEditDialog.axaml`
- Create: `src/DbDelta.App.Avalonia/Views/ConnectionEditDialog.axaml.cs`

- [ ] **Step 1: Create the XAML**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:DbDelta.App.ViewModels"
        x:Class="DbDelta.App.Views.ConnectionEditDialog"
        x:DataType="vm:ConnectionEditViewModel"
        Title="Modifica connessione"
        Width="520" Height="540"
        WindowStartupLocation="CenterOwner"
        CanResize="False">

  <Grid RowDefinitions="*,Auto" Margin="16">

    <ScrollViewer Grid.Row="0">
      <StackPanel Spacing="12">
        <TextBlock Text="Nome" />
        <TextBox Text="{Binding Name, Mode=TwoWay}" />

        <TextBlock Text="Server" />
        <TextBox Text="{Binding ServerName, Mode=TwoWay}" />

        <TextBlock Text="Database" />
        <TextBox Text="{Binding DatabaseName, Mode=TwoWay}" />

        <TextBlock Text="Password" />
        <Grid ColumnDefinitions="*,Auto">
          <TextBox Grid.Column="0"
                   Text="{Binding Password, Mode=TwoWay}"
                   PasswordChar="•" />
          <Button Grid.Column="1" Classes="ghost"
                  ToolTip.Tip="Tieni premuto per mostrare"
                  Margin="6,0,0,0">
            <TextBlock Text="👁" />
          </Button>
        </Grid>

        <TextBlock Text="Tag ambiente" />
        <TextBox Text="{Binding EnvironmentTag, Mode=TwoWay}"
                 Watermark="Dev / Test / Prod / …" />

        <TextBlock Text="Colore ambiente" />
        <ItemsControl ItemsSource="{Binding ColorPalette}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
              <WrapPanel Orientation="Horizontal" />
            </ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <Button Classes="ghost" Margin="0,0,4,4"
                      CommandParameter="{Binding Hex}"
                      Command="{Binding $parent[Window].((vm:ConnectionEditViewModel)DataContext).PickColorCommand}">
                <Border Width="24" Height="24" CornerRadius="3"
                        Background="{Binding Hex}" />
              </Button>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <TextBlock Text="{Binding EnvironmentColorHex}" FontFamily="Cascadia Mono,Consolas,monospace" FontSize="11" />

        <CheckBox Content="Aggiungi ai preferiti (pin in cima)" IsChecked="{Binding IsPinned, Mode=TwoWay}" />
      </StackPanel>
    </ScrollViewer>

    <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Right" Spacing="8" Margin="0,12,0,0">
      <Button Content="Elimina" Classes="ghost"
              Tag="Delete" Click="OnDeleteClick" />
      <Button Content="Annulla" Classes="ghost"
              Tag="Cancel" Click="OnCancelClick" />
      <Button Content="Salva" Classes="primary"
              IsEnabled="{Binding IsValid}"
              Tag="Save" Click="OnSaveClick" />
    </StackPanel>

  </Grid>
</Window>
```

- [ ] **Step 2: Create code-behind**

```csharp
// src/DbDelta.App.Avalonia/Views/ConnectionEditDialog.axaml.cs
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using DbDelta.App.ViewModels;

namespace DbDelta.App.Views;

public partial class ConnectionEditDialog : Window
{
    public enum Result { Save, Delete, Cancel }

    public ConnectionEditDialog() => InitializeComponent();

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(Result.Save);
    private void OnDeleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(Result.Delete);
    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(Result.Cancel);
}
```

- [ ] **Step 3: Add the `PickColorCommand` to `ConnectionEditViewModel`**

In `ConnectionEditViewModel.cs`, append:

```csharp
    [RelayCommand]
    public void PickColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex)) { EnvironmentColorHex = hex; }
    }
```

(`RelayCommand` requires `using CommunityToolkit.Mvvm.Input;` — already present.)

- [ ] **Step 4: Build + commit**

```
dotnet build src/DbDelta.App.Avalonia -warnaserror
```
Expected: 0 warnings.

```bash
git add src/DbDelta.App.Avalonia/Views/ConnectionEditDialog.axaml \
        src/DbDelta.App.Avalonia/Views/ConnectionEditDialog.axaml.cs \
        src/DbDelta.App.Avalonia/ViewModels/ConnectionEditViewModel.cs
git commit -m "$(cat <<'EOF'
feat(app/views): ConnectionEditDialog modal form

Window-based modal that takes a ConnectionEditViewModel; result is
ConnectionEditDialog.Result { Save | Delete | Cancel } returned via
ShowDialog. Colour palette renders the 8 DS-curated swatches; clicking
one calls PickColorCommand.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.19: `RecentConnectionsSidebar.axaml` — fills the M1 placeholder

**Files:**
- Create: `src/DbDelta.App.Avalonia/Views/RecentConnectionsSidebar.axaml`
- Create: `src/DbDelta.App.Avalonia/Views/RecentConnectionsSidebar.axaml.cs`
- Modify: `src/DbDelta.App.Avalonia/Views/MainWindow.axaml`

- [ ] **Step 1: Create the sidebar XAML**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DbDelta.App.ViewModels"
             x:Class="DbDelta.App.Views.RecentConnectionsSidebar"
             x:DataType="vm:ConnectionStoreViewModel">

  <Grid RowDefinitions="Auto,*">
    <TextBox Grid.Row="0"
             Text="{Binding SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
             Watermark="Cerca…" Margin="8" />

    <ScrollViewer Grid.Row="1">
      <ItemsControl ItemsSource="{Binding FilteredEntries}">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <Grid ColumnDefinitions="6,*" Margin="0,2" Cursor="Hand">
              <Border Grid.Column="0" Background="{Binding EnvironmentColorHex}" />
              <StackPanel Grid.Column="1" Margin="8,4">
                <TextBlock Text="{Binding Name}" FontWeight="Medium" />
                <TextBlock FontSize="11" Foreground="{StaticResource FgSubtleBrush}">
                  <Run Text="{Binding ServerName}" />
                  <Run Text=" / " />
                  <Run Text="{Binding DatabaseName}" />
                </TextBlock>
              </StackPanel>
            </Grid>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>
  </Grid>

</UserControl>
```

- [ ] **Step 2: Create code-behind**

```csharp
using Avalonia.Controls;

namespace DbDelta.App.Views;

public partial class RecentConnectionsSidebar : UserControl
{
    public RecentConnectionsSidebar() => InitializeComponent();
}
```

- [ ] **Step 3: Drop the sidebar into `MainWindow.axaml`**

Find the existing placeholder:

```xml
      <Border Grid.Column="0"
              Background="{StaticResource BgSubtleBrush}"
              BorderBrush="{StaticResource BorderBrush}"
              BorderThickness="0,0,1,0"
              IsVisible="{Binding IsSidebarOpen}">
        <!-- M1: sidebar placeholder — populated by later milestones -->
      </Border>
```

Replace its content (between the `<Border>` tags) with the sidebar UserControl:

```xml
      <Border Grid.Column="0"
              Background="{StaticResource BgSubtleBrush}"
              BorderBrush="{StaticResource BorderBrush}"
              BorderThickness="0,0,1,0"
              IsVisible="{Binding IsSidebarOpen}">
        <v:RecentConnectionsSidebar DataContext="{Binding AppState.Connections}" />
      </Border>
```

(`v:` namespace is already declared at the root of `MainWindow.axaml`.)

- [ ] **Step 4: Build + commit**

```
dotnet build src/DbDelta.App.Avalonia -warnaserror
```
Expected: 0 warnings.

```bash
git add src/DbDelta.App.Avalonia/Views/RecentConnectionsSidebar.axaml \
        src/DbDelta.App.Avalonia/Views/RecentConnectionsSidebar.axaml.cs \
        src/DbDelta.App.Avalonia/Views/MainWindow.axaml
git commit -m "$(cat <<'EOF'
feat(app/views): RecentConnectionsSidebar populates the M1 placeholder

Search box on top + scrollable list of FilteredEntries. Each row
shows the env-colour stripe + name + server/database subtitle. The
M1 empty <Border> is replaced by this UserControl.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.20: `.dbd` Open / Save commands + missing-connection dialog

**Files:**
- Modify: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs`
- Modify: `src/DbDelta.App.Avalonia/Views/MainWindow.axaml`

- [ ] **Step 1: Patch the VM**

Append inside `MainWindowViewModel`:

```csharp
    [ObservableProperty]
    private string? _projectFilePath;

    [RelayCommand]
    public async Task SaveProjectAsync(Avalonia.Controls.Window window)
    {
        if (window is null || AppState.Connections is null)
        {
            return;
        }
        Avalonia.Platform.Storage.IStorageProvider sp = window.StorageProvider;
        Avalonia.Platform.Storage.IStorageFile? file = await sp.SaveFilePickerAsync(new()
        {
            Title = "Salva progetto DbDelta",
            DefaultExtension = "dbd",
            SuggestedFileName = "progetto.dbd",
        });
        if (file is null) { return; }

        ConnectionEntry? src = AppState.Connections.Entries.FirstOrDefault();
        ConnectionEntry? tgt = AppState.Connections.Entries.Skip(1).FirstOrDefault();
        if (src is null || tgt is null) { return; }

        Persistence.Xml.XmlProjectStore store = new();
        await store.SaveAsync(
            file.Path.LocalPath,
            new DbDelta.Core.Abstractions.DbDeltaProject(
                Name: System.IO.Path.GetFileNameWithoutExtension(file.Path.LocalPath),
                SourceConnectionId: src.Id,
                TargetConnectionId: tgt.Id,
                Options: DbDelta.Core.Options.ComparisonOptions.Default),
            System.Threading.CancellationToken.None);
        ProjectFilePath = file.Path.LocalPath;
    }

    [RelayCommand]
    public async Task OpenProjectAsync(Avalonia.Controls.Window window)
    {
        if (window is null || AppState.Connections is null) { return; }
        Avalonia.Platform.Storage.IStorageProvider sp = window.StorageProvider;
        System.Collections.Generic.IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> picked = await sp.OpenFilePickerAsync(new()
        {
            Title = "Apri progetto DbDelta",
            AllowMultiple = false,
        });
        if (picked.Count == 0) { return; }
        string path = picked[0].Path.LocalPath;
        Persistence.Xml.XmlProjectStore store = new();
        DbDelta.Core.Abstractions.DbDeltaProject project = await store.LoadAsync(path, System.Threading.CancellationToken.None);

        ConnectionEntry? src = AppState.Connections.Entries.FirstOrDefault(e => e.Id == project.SourceConnectionId);
        ConnectionEntry? tgt = AppState.Connections.Entries.FirstOrDefault(e => e.Id == project.TargetConnectionId);
        if (src is null || tgt is null)
        {
            AppState.LastError = "Una o entrambe le connessioni referenziate dal progetto non esistono più. Selezionane di nuove e salva il progetto.";
            return;
        }
        string? srcCs = await AppState.Connections.MaterialiseAsync(src, System.Threading.CancellationToken.None);
        string? tgtCs = await AppState.Connections.MaterialiseAsync(tgt, System.Threading.CancellationToken.None);
        if (srcCs is not null) { AppState.SourceConnectionString = srcCs; }
        if (tgtCs is not null) { AppState.TargetConnectionString = tgtCs; }
        ProjectFilePath = path;
    }
```

Add the matching `using`s at the top:

```csharp
using System.Linq;
using System.Threading.Tasks;
using DbDelta.Core.Abstractions;
using Persistence = DbDelta.Persistence;
```

- [ ] **Step 2: Add buttons to the topbar in `MainWindow.axaml`**

Inside the existing topbar `<Grid ColumnDefinitions="Auto,*,Auto" Margin="12,0">`, change to `ColumnDefinitions="Auto,*,Auto,Auto,Auto"` and add two new buttons just before the theme toggle:

```xml
        <Button Grid.Column="2" Classes="ghost"
                Command="{Binding SaveProjectCommand}"
                CommandParameter="{Binding $parent[Window]}"
                ToolTip.Tip="Salva progetto…"
                VerticalAlignment="Center">
          <TextBlock Text="💾" />
        </Button>
        <Button Grid.Column="3" Classes="ghost"
                Command="{Binding OpenProjectCommand}"
                CommandParameter="{Binding $parent[Window]}"
                ToolTip.Tip="Apri progetto…"
                VerticalAlignment="Center">
          <TextBlock Text="📂" />
        </Button>
```

Then renumber the existing theme-toggle button to `Grid.Column="4"`.

- [ ] **Step 3: Build + commit**

```
dotnet build src/DbDelta.App.Avalonia -warnaserror
```
Expected: 0 warnings.

```bash
git add src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs \
        src/DbDelta.App.Avalonia/Views/MainWindow.axaml
git commit -m "$(cat <<'EOF'
feat(app): Save / Open .dbd project commands

Both commands use Avalonia's StorageProvider for native file pickers.
Open materialises the referenced connections; when either id is
missing, surfaces a one-line error in AppState.LastError so the
existing alert banner renders.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T11.21: Wire `ConnectionStringRedactor` into every UI surface that prints a connection string

**Files:**
- Modify: `src/DbDelta.App.Avalonia/Views/MainWindow.axaml`
- Modify: `src/DbDelta.App.Avalonia/Views/Converters.cs`

- [ ] **Step 1: Add a redact converter**

In `Converters.cs`, add:

```csharp
    /// <summary>Masks <c>password=</c> / <c>pwd=</c> in any connection-string preview.</summary>
    public static readonly IValueConverter RedactConnectionString = new FuncValueConverter<string?, string?>(static value =>
        value is null ? null : DbDelta.Persistence.Util.ConnectionStringRedactor.Redact(value));
```

This requires referencing `DbDelta.Persistence` from `DbDelta.App.Avalonia`. The csproj already does (`<ProjectReference Include="..\DbDelta.Persistence\..." />` was added implicitly via `ConnectionStoreViewModel` consumption — confirm and add if missing).

- [ ] **Step 2: Apply the converter in `MainWindow.axaml` status footer**

Replace the two footer source / target text blocks:

```xml
        <TextBlock Text="{Binding AppState.SourceConnectionString, Converter={x:Static v:Converters.RedactConnectionString}}" ... />
        …
        <TextBlock Text="{Binding AppState.TargetConnectionString, Converter={x:Static v:Converters.RedactConnectionString}}" ... />
```

(Leave the rest of the TextBlock attributes intact — just wrap the `Text` binding.)

- [ ] **Step 3: Build + commit**

```
dotnet build src/DbDelta.App.Avalonia -warnaserror
```
Expected: 0 warnings.

```bash
git add src/DbDelta.App.Avalonia/Views/Converters.cs \
        src/DbDelta.App.Avalonia/Views/MainWindow.axaml
git commit -m "$(cat <<'EOF'
fix(app): redact connection strings in the status footer

Both source/target text blocks in the bottom strip now run through
ConnectionStringRedactor — passwords are masked before the user can
see them. Screen recordings of demos no longer leak credentials.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase H — Wrap-up (1 task)

### Task T11.22: Full matrix run + format + push + CI green

- [ ] **Step 1: Full Release build**

```
dotnet build --configuration Release -warnaserror
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Run every non-DB test**

```
dotnet test tests/DbDelta.Core.UnitTests --no-build --configuration Release
dotnet test tests/DbDelta.Architecture.Tests --no-build --configuration Release
dotnet test tests/DbDelta.ScriptGen.GoldenTests --no-build --configuration Release
dotnet test tests/DbDelta.Persistence.UnitTests --no-build --configuration Release
dotnet test tests/DbDelta.Persistence.IntegrationTests --no-build --configuration Release
dotnet test tests/DbDelta.App.HeadlessTests --no-build --configuration Release
```
Expected: every project green.

- [ ] **Step 3: `dotnet format`**

```
dotnet format
dotnet format --verify-no-changes
```
Expected: second invocation clean.

- [ ] **Step 4: Commit format pass (if any)**

```bash
git status --short
git add -A
git commit -m "$(cat <<'EOF'
chore: dotnet format (M11-bis wrap-up)

Whitespace + line-ending normalization. No behavioural change.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 5: Push + watch CI**

```bash
git push origin main
gh run list --limit 1
gh run watch <run-id> --exit-status
```
Expected: both `windows-build` and `linux-integration-tests` jobs green.

---

## Self-Review Checklist

1. **Spec coverage**

   - §1.1 In-scope capabilities table → tasks T11.1–T11.22 each map to a row (see top-of-plan table) ✓
   - §2.1 Component map → file structure matches ✓
   - §3.1 ICredentialStore + 3 backends → T11.3, T11.5, T11.6 ✓
   - §3.2 IConnectionStore + ConnectionEntry → T11.2 (port + record) + T11.7 + T11.8 (implementation) ✓
   - §3.3 IProjectStore + DbDeltaProject → T11.2 (port + record) + T11.10 (implementation) ✓
   - §3.4 JSON on-disk format with atomic write + corruption tolerance → T11.7, T11.8 ✓
   - §3.5 ConnectionStringRedactor → T11.4 (helper + tests) + T11.21 (UI wiring) ✓
   - §3.6 ConnectionTester → T11.11 ✓
   - §4.1 First-launch autosave → T11.13 ✓
   - §4.2 Smart picker → T11.14 ✓
   - §4.3 Edit dialog → T11.17 (VM) + T11.18 (View) ✓
   - §4.4 Sidebar quick-switch → T11.19 ✓
   - §4.5 .dbd Open/Save + missing-connection dialog → T11.20 ✓
   - §5 Security UX (mask + reveal + redaction + DPAPI scope + POSIX 0600) → T11.4, T11.9, T11.18, T11.21 ✓
   - §6 Error handling (corruption, missing connection, test fail, DPAPI throw) → T11.7, T11.8, T11.11, T11.20 ✓
   - §7 Test strategy → unit + integration + headless tests embedded in their respective phases ✓
   - §8 Migration path → empty-start; no data migration needed; covered implicitly by T11.7 (empty load) ✓

2. **Placeholder scan** — no "TBD" / "implement later" / "similar to Task N" / "fill in".

3. **Type consistency**

   - `ICredentialStore.GetSecretAsync(string, CancellationToken)` consistent across port (T11.2), Dpapi (T11.3), Mac/Linux placeholders (T11.6), tests (T11.5).
   - `IConnectionStore` method signatures: `LoadAsync`/`UpsertAsync`/`DeleteAsync`/`TouchUsageAsync` — used consistently in T11.2 (port), T11.7 (impl), T11.13 (VM).
   - `ConnectionEntry.ConnectionStringTemplate` carries the literal `{PASSWORD}` placeholder per spec §3.2 — produced by `ConnectionStoreViewModel.AutosaveAsync` (T11.13) and consumed by `MaterialiseAsync` (T11.13).
   - Credential-store key format `"dbdelta:connection:{Id:D}"` — defined once in T11.13 (`KeyPrefix` constant) and reused everywhere.
   - `EnvironmentColorOption(string Label, string Hex)` consistent between T11.16 (palette) and T11.17 (`ColorPalette` property) and T11.18 (dialog binds to `{Binding Hex}`).
   - `XmlProjectStore.Surrogate` is internal to the file — never referenced from tests or other modules.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-21-m11-bis-connection-management.md` (22 tasks across 8 phases). Two execution options:

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
