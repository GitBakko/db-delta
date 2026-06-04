# Consultable Version History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish the CHANGELOG as a "Version history" page on the DocFX site with stable per-version anchors, and show a clickable version pill in the app status bar that deep-links to the running version's anchor.

**Architecture:** A pwsh script transforms `CHANGELOG.md` → `docfx/articles/version-history.md` (gitignored) at docs-build time, injecting `<a id="v<version>"></a>` anchors derived only from the version token. `release.yml` starts stamping `-p:Version` into both publishes; a new static `AppVersionInfo` reads the `AssemblyInformationalVersionAttribute` and composes the deep-link URL; `MainWindowViewModel` exposes it to a pill button in the status bar.

**Tech Stack:** .NET 10 / Avalonia 11 (CommunityToolkit.Mvvm source-gen), xunit.v3 + FluentAssertions (`tests/DbDelta.App.HeadlessTests`), DocFX (dotnet local tool), pwsh, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-06-04-version-history-design.md`

**Conventions that bind every task:**
- CI hard-gates on `dotnet format DbDelta.sln --verify-no-changes` — run `dotnet format DbDelta.sln --include <touched .cs files>` before every commit that touches C#.
- Files written by tooling must be CRLF (repo default); pwsh `Set-Content` on Windows produces CRLF — fine.
- UI text in the app is Italian; code, comments, commits are English.

---

### Task 1: `AppVersionInfo` (TDD)

**Files:**
- Create: `src/DbDelta.App.Avalonia/AppVersionInfo.cs`
- Test: `tests/DbDelta.App.HeadlessTests/AppVersionInfoTests.cs`

- [ ] **Step 1.1: Write the failing tests**

Create `tests/DbDelta.App.HeadlessTests/AppVersionInfoTests.cs`:

```csharp
using FluentAssertions;

// NOTE: no `using DbDelta.App;` — the namespace below already resolves it via
// parent-namespace traversal, and a redundant using trips IDE0005 in the
// format gate. `using Xunit;` is a project-wide implicit using from xunit.v3.

namespace DbDelta.App.HeadlessTests;

public class AppVersionInfoTests
{
    private const string PageUrl = "https://gitbakko.github.io/db-delta/articles/version-history.html";

    [Fact]
    public void Null_raw_version_falls_back_to_plain_dev_and_unanchored_url()
    {
        (string display, string url) = AppVersionInfo.FromRaw(null);
        display.Should().Be("dev");
        url.Should().Be(PageUrl);
    }

    [Fact]
    public void Whitespace_raw_version_falls_back_to_plain_dev()
    {
        (string display, string url) = AppVersionInfo.FromRaw("   ");
        display.Should().Be("dev");
        url.Should().Be(PageUrl);
    }

    [Fact]
    public void Build_metadata_suffix_is_stripped()
    {
        // The SDK appends "+<commit-sha>" when building inside a git repo.
        (string display, string url) = AppVersionInfo.FromRaw("1.0.0-rc1+abc1234");
        display.Should().Be("v1.0.0-rc1");
        url.Should().Be($"{PageUrl}#v1.0.0-rc1");
    }

    [Fact]
    public void Plain_semver_maps_to_prefixed_display_and_anchored_url()
    {
        (string display, string url) = AppVersionInfo.FromRaw("0.0.0-dev");
        display.Should().Be("v0.0.0-dev");
        url.Should().Be($"{PageUrl}#v0.0.0-dev");
    }

    [Fact]
    public void Static_properties_are_populated_and_consistent()
    {
        AppVersionInfo.Display.Should().NotBeNullOrWhiteSpace();
        AppVersionInfo.HistoryUrl.Should().StartWith(PageUrl);
    }
}
```

- [ ] **Step 1.2: Run tests to verify they fail**

Run: `dotnet test tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj -c Debug --filter "FullyQualifiedName~AppVersionInfoTests"`
Expected: compile FAILURE — `AppVersionInfo` does not exist yet.

- [ ] **Step 1.3: Implement `AppVersionInfo`**

Create `src/DbDelta.App.Avalonia/AppVersionInfo.cs`:

```csharp
using System.Reflection;

namespace DbDelta.App;

/// <summary>
/// Resolves the running app version (stamped at publish time via
/// <c>-p:Version</c> in <c>.github/workflows/release.yml</c>; local builds fall
/// back to <c>0.0.0-dev</c> from the csproj) and the deep-link to that
/// version's anchor on the online version-history page.
/// </summary>
public static class AppVersionInfo
{
    private const string HistoryPageUrl =
        "https://gitbakko.github.io/db-delta/articles/version-history.html";

    /// <summary><c>v1.0.0-rc1</c> — or plain <c>dev</c> when no version attribute is present.</summary>
    public static string Display { get; }

    /// <summary>Version-history page URL, anchored at the running version.</summary>
    public static string HistoryUrl { get; }

    static AppVersionInfo()
    {
        string? raw = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        (Display, HistoryUrl) = FromRaw(raw);
    }

    /// <summary>
    /// Pure mapping from a raw <c>InformationalVersion</c> to (display, url).
    /// The SDK appends <c>+&lt;commit-sha&gt;</c> build metadata when building
    /// inside a git repo — everything from the first <c>+</c> is stripped. The
    /// anchor id derives only from the version token and must stay in sync with
    /// <c>scripts/docs/build-version-history.ps1</c> (anchor = <c>v&lt;version&gt;</c>).
    /// </summary>
    public static (string Display, string HistoryUrl) FromRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("dev", HistoryPageUrl);
        }
        string version = raw.Split('+')[0];
        return ($"v{version}", $"{HistoryPageUrl}#v{version}");
    }
}
```

- [ ] **Step 1.4: Run tests to verify they pass**

Run: `dotnet test tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj -c Debug --filter "FullyQualifiedName~AppVersionInfoTests"`
Expected: 5 passed. (Note: `Static_properties_are_populated_and_consistent` sees the test-host's view of the app assembly — any non-empty display is fine; the assertion is deliberately loose.)

- [ ] **Step 1.5: Format + commit**

```bash
dotnet format DbDelta.sln --include src/DbDelta.App.Avalonia/AppVersionInfo.cs tests/DbDelta.App.HeadlessTests/AppVersionInfoTests.cs
git add src/DbDelta.App.Avalonia/AppVersionInfo.cs tests/DbDelta.App.HeadlessTests/AppVersionInfoTests.cs
git commit -m "feat(app): AppVersionInfo resolves stamped version + history deep-link"
```

---

### Task 2: Version stamping (release.yml + csproj dev fallback)

**Files:**
- Modify: `src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj` (PropertyGroup)
- Modify: `.github/workflows/release.yml:38-42` (the two publish steps)

- [ ] **Step 2.1: Add the dev fallback to the app csproj**

In `src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj`, inside the first `<PropertyGroup>` (after the `<ApplicationIcon>` line), add:

```xml
    <!-- Tag-driven versioning: CI passes -p:Version=<tag> at publish; local
         builds get an honest dev marker instead of the SDK's default 1.0.0. -->
    <Version Condition="'$(Version)' == ''">0.0.0-dev</Version>
```

- [ ] **Step 2.2: Stamp the version in both release publishes**

In `.github/workflows/release.yml`, change the two publish steps:

```yaml
      - name: Publish desktop app (self-contained win-x64)
        run: dotnet publish src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:Version=${{ steps.ver.outputs.version }} -o installer/staging/app

      - name: Publish CLI (self-contained win-x64)
        run: dotnet publish src/DbDelta.Cli/DbDelta.Cli.csproj -c Release -r win-x64 --self-contained true -p:Version=${{ steps.ver.outputs.version }} -o installer/staging/cli
```

(`steps.ver.outputs.version` is the full semver including any `-rc` label — exactly what the app should display. The MSI keeps using the separate numeric `msiversion`.)

- [ ] **Step 2.3: Verify the stamping locally**

```bash
dotnet build src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj -c Debug -p:Version=9.9.9-test
```

Then:

```powershell
$asm = [Reflection.Assembly]::LoadFile("D:\Develop\AI\_ClaudeCode\SQL Compare\src\DbDelta.App.Avalonia\bin\Debug\net10.0\DbDelta.App.dll")
[Reflection.CustomAttributeExtensions]::GetCustomAttribute($asm, [Reflection.AssemblyInformationalVersionAttribute]).InformationalVersion
```

Expected: starts with `9.9.9-test` (a `+<sha>` suffix may follow — that is what `FromRaw` strips).

Then rebuild WITHOUT `-p:Version` and re-run the same reflection check:

```bash
dotnet build src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj -c Debug
```

Expected: starts with `0.0.0-dev` (the csproj fallback).

- [ ] **Step 2.4: Commit**

```bash
git add src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj .github/workflows/release.yml
git commit -m "feat(release): stamp tag-driven -p:Version into app + CLI publishes"
```

---

### Task 3: Status-bar version pill (VM + XAML + style)

**Files:**
- Modify: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs` (add property + command)
- Modify: `src/DbDelta.App.Avalonia/Views/MainWindow.axaml:444-469` (status-bar row)
- Modify: `src/DbDelta.App.Avalonia/Styles/AppStyles.axaml` (new `status-pill` style)
- Modify: `docs/superpowers/specs/2026-06-04-version-history-design.md` (error-handling amendment)
- Test: `tests/DbDelta.App.HeadlessTests/ViewModels/MainWindowViewModelTests.cs`

- [ ] **Step 3.1: Write the failing VM test**

Append to `tests/DbDelta.App.HeadlessTests/ViewModels/MainWindowViewModelTests.cs` (inside the existing class, reusing its `BuildVm()` helper):

```csharp
    // ── Version pill ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void AppVersion_exposes_the_assembly_version_display()
    {
        MainWindowViewModel vm = BuildVm();
        vm.AppVersion.Should().Be(AppVersionInfo.Display);
        vm.AppVersion.Should().NotBeNullOrWhiteSpace();
    }

    [AvaloniaFact]
    public void OpenVersionHistory_command_exists_and_can_execute()
    {
        MainWindowViewModel vm = BuildVm();
        vm.OpenVersionHistoryCommand.Should().NotBeNull();
        vm.OpenVersionHistoryCommand.CanExecute(null).Should().BeTrue();
        // Deliberately NOT executed — it would open a real browser.
    }
```

- [ ] **Step 3.2: Run tests to verify they fail**

Run: `dotnet test tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj -c Debug --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: compile FAILURE — `AppVersion` / `OpenVersionHistoryCommand` do not exist.

- [ ] **Step 3.3: Add property + command to `MainWindowViewModel`**

In `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs`:

Add to the usings (if not present):

```csharp
using System.Diagnostics;
```

Add inside the class (near the other simple get-only properties; CommunityToolkit generates `OpenVersionHistoryCommand` from the annotated method):

```csharp
    /// <summary>Display version for the status-bar pill (e.g. "v1.0.0-rc1").</summary>
    public string AppVersion => AppVersionInfo.Display;

    [RelayCommand]
    private void OpenVersionHistory()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppVersionInfo.HistoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Browser launch failure is rare and non-actionable in-app; the
            // status bar has no writable message channel (StatusText is
            // computed from IsBusy), so log and move on.
            Debug.WriteLine($"Failed to open version history: {ex.Message}");
        }
    }
```

- [ ] **Step 3.4: Run tests to verify they pass**

Run: `dotnet test tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj -c Debug --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: all pass (existing + 2 new).

- [ ] **Step 3.5: Add the `status-pill` style**

In `src/DbDelta.App.Avalonia/Styles/AppStyles.axaml`, append with the other `Button` styles:

```xml
  <!-- Status-bar version pill — the only interactive control in the 24px
       status strip. Deliberately 18px: the 32px monoline rule applies to the
       app-shell/dialog surfaces, not the status bar (its own surface). Always
       visibly bordered (no naked buttons). -->
  <Style Selector="Button.status-pill">
    <Setter Property="Height" Value="18" />
    <Setter Property="MinHeight" Value="18" />
    <Setter Property="Padding" Value="8,0" />
    <Setter Property="FontSize" Value="11" />
    <Setter Property="CornerRadius" Value="9" />
    <Setter Property="Background" Value="{StaticResource BgRaisedBrush}" />
    <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Foreground" Value="{StaticResource FgSubtleBrush}" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="VerticalContentAlignment" Value="Center" />
  </Style>
  <Style Selector="Button.status-pill:pointerover /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="{StaticResource BgRaisedBrush}" />
    <Setter Property="BorderBrush" Value="{StaticResource BorderStrongBrush}" />
  </Style>
```

(Check how the existing button styles in this file override `:pointerover` — mirror the exact selector form used there if it differs from the above.)

- [ ] **Step 3.6: Restructure the status-bar row**

In `src/DbDelta.App.Avalonia/Views/MainWindow.axaml`, replace the row-3 Border content (currently a single left-aligned `StackPanel` with `Margin="12,0"`) with a two-column grid — left side byte-identical except the `Margin` moves to the Grid:

```xml
    <Border Grid.Row="3"
            Background="{StaticResource BgMutedBrush}"
            BorderBrush="{StaticResource BorderBrush}"
            BorderThickness="0,1,0,0"
            ClipToBounds="True">
      <Grid ColumnDefinitions="*,Auto" Margin="12,0">
        <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center"
                    HorizontalAlignment="Left">
          <TextBlock Text="{Binding AppState.StatusText}"
                     FontSize="11"
                     Foreground="{StaticResource FgSubtleBrush}" />
          <TextBlock Text="·" Foreground="{StaticResource BorderStrongBrush}" FontSize="11" />
          <TextBlock Text="{Binding AppState.SourceConnectionString, Converter={x:Static v:Converters.RedactConnectionString}}"
                     FontSize="11"
                     FontFamily="Cascadia Mono,Consolas,monospace"
                     Foreground="{StaticResource FgSubtleBrush}"
                     TextTrimming="CharacterEllipsis"
                     MaxWidth="500" />
          <TextBlock Text="→" Foreground="{StaticResource FgFaintBrush}" FontSize="11" />
          <TextBlock Text="{Binding AppState.TargetConnectionString, Converter={x:Static v:Converters.RedactConnectionString}}"
                     FontSize="11"
                     FontFamily="Cascadia Mono,Consolas,monospace"
                     Foreground="{StaticResource FgSubtleBrush}"
                     TextTrimming="CharacterEllipsis"
                     MaxWidth="500" />
        </StackPanel>
        <Button Grid.Column="1"
                Classes="status-pill"
                Content="{Binding AppVersion}"
                Command="{Binding OpenVersionHistoryCommand}"
                ToolTip.Tip="Apri la version history nel browser"
                VerticalAlignment="Center" />
      </Grid>
    </Border>
```

- [ ] **Step 3.7: Build + run the full headless suite**

```bash
dotnet build src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj -c Debug
dotnet test tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj -c Debug
```

Expected: build 0 errors, all headless tests pass.

- [ ] **Step 3.8: Amend the spec's error-handling line**

In `docs/superpowers/specs/2026-06-04-version-history-design.md`, replace:

```
- App: browser launch failure → exception swallowed, message surfaced in
  `AppState.StatusText`; missing version attribute → `dev` fallback.
```

with:

```
- App: browser launch failure → exception swallowed + `Debug.WriteLine`
  (amended 2026-06-04: `StatusText` turned out to be computed from `IsBusy`,
  not writable); missing version attribute → `dev` fallback.
```

- [ ] **Step 3.9: Format + commit**

```bash
dotnet format DbDelta.sln --include src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs tests/DbDelta.App.HeadlessTests/ViewModels/MainWindowViewModelTests.cs
git add src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs src/DbDelta.App.Avalonia/Views/MainWindow.axaml src/DbDelta.App.Avalonia/Styles/AppStyles.axaml tests/DbDelta.App.HeadlessTests/ViewModels/MainWindowViewModelTests.cs docs/superpowers/specs/2026-06-04-version-history-design.md
git commit -m "feat(app): clickable version pill in status bar deep-links to version history"
```

---

### Task 4: Docs pipeline (script + toc + gitignore + docs.yml)

**Files:**
- Create: `scripts/docs/build-version-history.ps1`
- Modify: `docfx/articles/toc.yml`
- Modify: `.gitignore` (after the existing docfx block at lines 75-77)
- Modify: `.github/workflows/docs.yml` (new step between "Restore tools" and "Build documentation")

- [ ] **Step 4.1: Write the generator script**

Create `scripts/docs/build-version-history.ps1`:

```powershell
#!/usr/bin/env pwsh
# Generates docfx/articles/version-history.md from the repo-root CHANGELOG.md,
# injecting a stable HTML anchor (<a id="v<version>"></a>) above every version
# heading "## [<version>]". The anchor id derives ONLY from the version token,
# so heading-format changes never break deep links. The app composes the same
# anchor in AppVersionInfo (v<version>) — keep the two in sync.
#
# Run from anywhere BEFORE building the docs:
#   pwsh scripts/docs/build-version-history.ps1
#   dotnet docfx docfx/docfx.json
# The generated article is gitignored; docfx's toc references it, so a docs
# build without this script fails fast on the missing href (intended).
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$changelog = Join-Path $repoRoot 'CHANGELOG.md'
$outFile = Join-Path $repoRoot 'docfx/articles/version-history.md'

if (-not (Test-Path $changelog)) {
    Write-Error "CHANGELOG.md not found at $changelog"
    exit 1
}

$anchored = 0
$out = foreach ($line in Get-Content $changelog) {
    if ($line -match '^##\s+\[(?<ver>[^\]]+)\]' -and $Matches['ver'] -ne 'Unreleased') {
        $anchored++
        "<a id=`"v$($Matches['ver'])`"></a>"
    }
    $line
}

if ($anchored -eq 0) {
    Write-Error 'No version headings (## [x.y.z]) found in CHANGELOG.md — malformed changelog?'
    exit 1
}

Set-Content -Path $outFile -Value $out -Encoding utf8
Write-Host "version-history.md generated ($anchored version anchors)."
```

- [ ] **Step 4.2: Run the script and inspect the output**

Run: `pwsh scripts/docs/build-version-history.ps1`
Expected: `version-history.md generated (21 version anchors).` (one per released version, 1.0.0-rc1 down to 0.0.0 — `[Unreleased]` is intentionally NOT anchored).

Spot-check: `docfx/articles/version-history.md` must contain `<a id="v1.0.0-rc1"></a>` on the line above `## [1.0.0-rc1] — 2026-05-28 — first release candidate`.

- [ ] **Step 4.3: Wire toc + gitignore**

Append to `docfx/articles/toc.yml`:

```yaml
- name: Version history
  href: version-history.md
```

Append to `.gitignore` (after the existing `docfx/_site/` line):

```
docfx/articles/version-history.md
```

- [ ] **Step 4.4: Verify with a local docs build**

```bash
dotnet tool restore
dotnet docfx docfx/docfx.json
```

Expected: build succeeds (it runs with warnings-as-errors per the existing config — a missing href would fail). Then confirm the page landed:

```bash
ls docfx/_site/articles/version-history.html
grep -c "id=\"v1.0.0-rc1\"" docfx/_site/articles/version-history.html
```

Expected: file exists, grep count ≥ 1.

- [ ] **Step 4.5: Add the docs.yml step**

In `.github/workflows/docs.yml`, between the "Restore tools" and "Build documentation" steps, insert:

```yaml
      - name: Generate version-history article
        shell: pwsh
        run: |
          ./scripts/docs/build-version-history.ps1
          $anchors = (Select-String -Path docfx/articles/version-history.md -Pattern '<a id="v').Count
          if ($anchors -lt 1) { throw "version-history.md has no anchors" }
          Write-Host "sanity ok: $anchors anchors"
```

- [ ] **Step 4.6: Commit**

```bash
git add scripts/docs/build-version-history.ps1 docfx/articles/toc.yml .gitignore .github/workflows/docs.yml
git commit -m "feat(docs): version-history article generated from CHANGELOG with stable anchors"
```

(`docfx/articles/version-history.md` must NOT be in the commit — verify `git status` shows it ignored.)

---

### Task 5: Final verification

- [ ] **Step 5.1: Full non-Docker test sweep**

```bash
dotnet test tests/DbDelta.Core.UnitTests/DbDelta.Core.UnitTests.csproj -c Debug
dotnet test tests/DbDelta.ScriptGen.GoldenTests/DbDelta.ScriptGen.GoldenTests.csproj -c Debug
dotnet test tests/DbDelta.App.HeadlessTests/DbDelta.App.HeadlessTests.csproj -c Debug
dotnet test tests/DbDelta.Property.Tests/DbDelta.Property.Tests.csproj -c Debug
```

Expected: all green.

- [ ] **Step 5.2: Format gate on everything touched**

```bash
dotnet format DbDelta.sln --verify-no-changes --include src/DbDelta.App.Avalonia/AppVersionInfo.cs src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs tests/DbDelta.App.HeadlessTests/AppVersionInfoTests.cs tests/DbDelta.App.HeadlessTests/ViewModels/MainWindowViewModelTests.cs
```

Expected: exit 0, no errors.

- [ ] **Step 5.3: Visual smoke (manual, optional but recommended)**

Run the app (`dotnet run --project src/DbDelta.App.Avalonia`): status bar bottom-right shows the `v0.0.0-dev` pill; hover shows border highlight + tooltip; click opens the browser on the version-history page (the `#v0.0.0-dev` anchor won't exist → lands at top, accepted degradation per spec).
