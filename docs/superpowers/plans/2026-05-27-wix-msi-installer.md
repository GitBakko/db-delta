# WiX MSI Installer + Release Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a per-machine Windows MSI that installs the DbDelta desktop app (Start-Menu shortcut) and the `dbdelta` CLI (on system PATH), self-contained `win-x64`, built and attached to a GitHub Release on tag.

**Architecture:** Two `dotnet publish --self-contained` outputs are staged under `installer/staging/{app,cli}`. A WiX v5 `.wixproj` (using the `WixToolset.Sdk` MSBuild SDK, pinned via NuGet) harvests those folders into a per-machine MSI. A new `.github/workflows/release.yml` triggered on `v*` tags publishes, builds the MSI, and attaches it to a GitHub Release. `ci.yml` and `docs.yml` are untouched.

**Tech Stack:** WiX Toolset v5 (`WixToolset.Sdk` 5.x + `WixToolset.UI.wixext`); .NET 10 self-contained publish (`win-x64`); GitHub Actions (`softprops/action-gh-release`).

---

## Background facts (verified in codebase)

- Desktop app: `src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj` — `OutputType=WinExe`, `TargetFramework=net10.0`, `AssemblyName=DbDelta.App` ⇒ published executable is **`DbDelta.App.exe`**. Has `app.manifest`. No `.ico` (shortcut uses the exe's default icon).
- CLI: `src/DbDelta.Cli/DbDelta.Cli.csproj` — `OutputType=Exe`, `AssemblyName=dbdelta` ⇒ **`dbdelta.exe`**.
- `.gitignore` already has (line ~49) a "# WiX / installers" section with `*.wixobj` + `*.wixpdb`, and `bin/` + `obj/`. It does NOT yet ignore `*.msi` or `installer/staging/`.
- This is a Windows host; `dotnet` + the WiX SDK build locally. WiX v5 `wxs` files use the namespace `http://wixtoolset.org/schemas/v4/wxs` (shared by v4/v5).
- All commands run from the repo root: `D:\Develop\AI\_ClaudeCode\SQL Compare`.
- Do NOT add `installer/` to `DbDelta.sln` (keeps the CI `dotnet format --verify-no-changes` gate — which scans solution C# — unaffected; the wixproj has no C#).

---

## Task 1: Self-contained publish + staging git-ignores

**Files:**
- Modify: `.gitignore`

- [ ] **Step 1: Add staging + msi git-ignores**

In `.gitignore`, under the existing `# WiX / installers` section (which has `*.wixobj` and `*.wixpdb`), add two lines so the section reads:
```
# WiX / installers
*.wixobj
*.wixpdb
*.msi
installer/staging/
```

- [ ] **Step 2: Publish the desktop app self-contained**

Run:
```bash
dotnet publish src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj -c Release -r win-x64 --self-contained true -o installer/staging/app
```
Expected: exit 0; `installer/staging/app/DbDelta.App.exe` exists alongside hundreds of runtime files.

- [ ] **Step 3: Publish the CLI self-contained**

Run:
```bash
dotnet publish src/DbDelta.Cli/DbDelta.Cli.csproj -c Release -r win-x64 --self-contained true -o installer/staging/cli
```
Expected: exit 0; `installer/staging/cli/dbdelta.exe` exists.

- [ ] **Step 4: Verify the staged executables**

Run: `ls installer/staging/app/DbDelta.App.exe installer/staging/cli/dbdelta.exe`
Expected: both paths print (both exes present).

- [ ] **Step 5: Confirm staging is NOT tracked, then commit only the gitignore**

Run: `git status --porcelain` — expect ONLY ` M .gitignore` (the `installer/staging/` tree must be ignored, not listed).
```bash
git add .gitignore
git commit -m "build(installer): ignore MSI + installer staging output

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 2: WiX v5 project + Package.wxs → local MSI build

**Files:**
- Create: `installer/DbDelta.Installer.wixproj`
- Create: `installer/Package.wxs`

- [ ] **Step 1: Generate two stable GUIDs (used once, then immutable)**

Run: `pwsh -NoProfile -Command "[guid]::NewGuid().ToString().ToUpper(); [guid]::NewGuid().ToString().ToUpper()"`
Expected: prints two GUIDs. Call them `<UPGRADE_CODE>` (first) and `<PATH_COMPONENT_GUID>` (second). Paste them into `Package.wxs` below. **These must never change after first release** — the UpgradeCode is what lets future MSIs recognize and upgrade this product.

- [ ] **Step 2: Write `installer/DbDelta.Installer.wixproj`**

```xml
<Project Sdk="WixToolset.Sdk/5.0.2">
  <PropertyGroup>
    <OutputType>Package</OutputType>
    <OutputName>DbDelta-$(Version)-win-x64</OutputName>
    <!-- Default so a local `dotnet build` works without -p:Version. -->
    <Version Condition="'$(Version)' == ''">0.0.0</Version>
    <DefineConstants>Version=$(Version)</DefineConstants>
    <InstallerPlatform>x64</InstallerPlatform>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="WixToolset.UI.wixext" Version="5.0.2" />
  </ItemGroup>
</Project>
```
(If a newer WiX 5.x is current, bump both `5.0.2` values together. Keep them equal.)

- [ ] **Step 3: Write `installer/Package.wxs`**

Replace `PUT-UPGRADE-CODE-HERE` and `PUT-PATH-COMPONENT-GUID-HERE` with the two GUIDs from Step 1.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="DbDelta"
           Manufacturer="DbDelta"
           Version="$(var.Version)"
           UpgradeCode="PUT-UPGRADE-CODE-HERE"
           Scope="perMachine">

    <MajorUpgrade DowngradeErrorMessage="A newer version of DbDelta is already installed." />
    <MediaTemplate EmbedCab="yes" />

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="DbDelta">
        <Directory Id="CLIFOLDER" Name="cli" />
      </Directory>
    </StandardDirectory>
    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="AppShortcutFolder" Name="DbDelta" />
    </StandardDirectory>

    <Feature Id="Main" Title="DbDelta" Level="1">
      <ComponentGroupRef Id="AppFiles" />
      <ComponentGroupRef Id="CliFiles" />
      <ComponentRef Id="AppShortcut" />
      <ComponentRef Id="CliPathEntry" />
    </Feature>

    <!-- Start-Menu shortcut to the desktop app -->
    <Component Id="AppShortcut" Directory="AppShortcutFolder" Guid="*">
      <Shortcut Id="AppStartMenu"
                Name="DbDelta"
                Target="[INSTALLFOLDER]DbDelta.App.exe"
                WorkingDirectory="INSTALLFOLDER" />
      <RemoveFolder Id="RemoveAppShortcutFolder" Directory="AppShortcutFolder" On="uninstall" />
      <RegistryValue Root="HKLM" Key="Software\DbDelta" Name="installed"
                     Type="integer" Value="1" KeyPath="yes" />
    </Component>

    <!-- Append the CLI folder to the system PATH -->
    <Component Id="CliPathEntry" Directory="CLIFOLDER" Guid="PUT-PATH-COMPONENT-GUID-HERE">
      <Environment Id="PathCli" Name="PATH" Value="[CLIFOLDER]"
                   Permanent="no" Part="last" Action="set" System="yes" />
      <RegistryValue Root="HKLM" Key="Software\DbDelta" Name="cliPath"
                     Type="string" Value="[CLIFOLDER]" KeyPath="yes" />
    </Component>
  </Package>

  <Fragment>
    <!-- Harvest the self-contained publish output (WiX v5 Files element auto-generates components). -->
    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">
      <Files Include="staging\app\**" />
    </ComponentGroup>
    <ComponentGroup Id="CliFiles" Directory="CLIFOLDER">
      <Files Include="staging\cli\**" />
    </ComponentGroup>
  </Fragment>
</Wix>
```

- [ ] **Step 4: Build the MSI locally (staging from Task 1 must exist)**

Run:
```bash
dotnet build installer/DbDelta.Installer.wixproj -c Release -p:Version=0.16.0
```
Expected: exit 0; `installer/bin/Release/DbDelta-0.16.0-win-x64.msi` is produced with no ICE-validation errors.

If `Files Include="staging\app\**"` fails to harvest (path resolution / no files), the fallback is to add `BindPath` or make the glob absolute via `$(MSBuildProjectDirectory)`; resolve the exact harvest syntax here and note it. Do NOT skip harvesting — the MSI must contain `DbDelta.App.exe` + `dbdelta.exe` + their runtimes.

- [ ] **Step 5: Sanity-check the MSI contents**

Run: `pwsh -NoProfile -Command "(Get-Item installer/bin/Release/DbDelta-0.16.0-win-x64.msi).Length"`
Expected: a large size (tens-to-hundreds of MB — two self-contained runtimes), confirming files were harvested (a few-KB MSI means harvest produced nothing → fix Step 3/4).

- [ ] **Step 6: Confirm only the wixproj + wxs are tracked, then commit**

Run: `git status --porcelain` — expect only `installer/DbDelta.Installer.wixproj` + `installer/Package.wxs` as new (the `installer/bin/`, `installer/obj/`, `installer/staging/`, `*.msi` are git-ignored).
```bash
git add installer/DbDelta.Installer.wixproj installer/Package.wxs
git commit -m "feat(installer): WiX v5 per-machine MSI (app shortcut + CLI on PATH)

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 3: Release workflow

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Write the workflow**

```yaml
name: release

on:
  push:
    tags: [ 'v*' ]
  workflow_dispatch:
    inputs:
      version:
        description: 'Version x.y.z (no leading v)'
        required: true

permissions:
  contents: write

jobs:
  build-msi:
    name: build MSI + attach to release
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Resolve version
        id: ver
        shell: pwsh
        run: |
          $v = "${{ github.event_name == 'workflow_dispatch' && inputs.version || github.ref_name }}"
          $v = $v -replace '^v',''
          "version=$v" >> $env:GITHUB_OUTPUT

      - name: Publish desktop app (self-contained win-x64)
        run: dotnet publish src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj -c Release -r win-x64 --self-contained true -o installer/staging/app

      - name: Publish CLI (self-contained win-x64)
        run: dotnet publish src/DbDelta.Cli/DbDelta.Cli.csproj -c Release -r win-x64 --self-contained true -o installer/staging/cli

      - name: Build MSI
        run: dotnet build installer/DbDelta.Installer.wixproj -c Release -p:Version=${{ steps.ver.outputs.version }}

      - name: Attach MSI to GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          files: installer/bin/Release/DbDelta-${{ steps.ver.outputs.version }}-win-x64.msi
          fail_on_unmatched_files: true
```

- [ ] **Step 2: Structural sanity-check**

Confirm by eye against `.github/workflows/ci.yml` / `docs.yml`: top-level keys `name`, `on`, `permissions`, `jobs`; the job's steps at consistent indentation; no tabs. Confirm the `version` resolution strips a leading `v` so tag `v0.16.0` → MSI `0.16.0`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci(release): build WiX MSI + attach to GitHub Release on v* tags

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

## Task 4: Verification + close-out

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `README.md` (if a Download/Install section fits; otherwise skip — see Step 3)
- Modify: `docs/superpowers/specs/2026-05-27-wix-msi-installer-design.md` (status)

- [ ] **Step 1: Re-confirm a clean MSI build from a fresh publish**

Run (rebuilds staging then the MSI, mirroring CI):
```bash
dotnet publish src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj -c Release -r win-x64 --self-contained true -o installer/staging/app
dotnet publish src/DbDelta.Cli/DbDelta.Cli.csproj -c Release -r win-x64 --self-contained true -o installer/staging/cli
dotnet build installer/DbDelta.Installer.wixproj -c Release -p:Version=0.16.0
```
Expected: MSI produced, 0 errors. Report the MSI size.

- [ ] **Step 2: (Optional but recommended) smoke install/uninstall**

ONLY if you can run elevated and are willing to mutate this machine — otherwise skip and rely on the CI runner. To smoke locally (admin shell):
```
msiexec /i installer\bin\Release\DbDelta-0.16.0-win-x64.msi /quiet /qn
```
then check `"%ProgramFiles%\DbDelta\DbDelta.App.exe"` exists and a new shell resolves `dbdelta`, then uninstall:
```
msiexec /x installer\bin\Release\DbDelta-0.16.0-win-x64.msi /quiet
```
**Default: SKIP local install** (don't mutate the dev machine). Note that the real end-to-end install gate runs on the release workflow's `windows-latest` runner when a tag is pushed.

- [ ] **Step 3: Update CHANGELOG (+ unsigned note)**

In `CHANGELOG.md`, under `[Unreleased]`, remove `_Pending: WiX MSI installer._` and add an `### Added` bullet:
> - **Windows MSI installer (WiX v5)** — a per-machine MSI installs the DbDelta desktop app (Start-Menu shortcut) and the `dbdelta` CLI (added to the system `PATH`), self-contained `win-x64`. Built and attached to the GitHub Release on `v*` tags (`.github/workflows/release.yml`). The MSI is **unsigned** (no code-signing certificate yet), so Windows SmartScreen / UAC shows an "unknown publisher" prompt on first run.

- [ ] **Step 4: Mark the spec Done**

In `docs/superpowers/specs/2026-05-27-wix-msi-installer-design.md`, change the `**Status:**` line to `implemented 2026-05-27 · tag v0.16.0 (pending)`.

- [ ] **Step 5: Commit (do NOT tag, do NOT push)**

```bash
git add CHANGELOG.md docs/superpowers/specs/2026-05-27-wix-msi-installer-design.md
git commit -m "docs: close out WiX MSI installer (CHANGELOG + spec status)

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

> Tagging `v0.16.0` and pushing (which triggers the release workflow → builds + publishes the MSI) are **manual**, per the collaboration pattern. Surface them as the final decision after all tasks pass.

---

## Self-review notes for the implementer

- **No solution coupling.** Do NOT add `installer/` or the wixproj to `DbDelta.sln`. The CI `dotnet format --verify-no-changes` gate only inspects solution C#; the wixproj/wxs are not C#.
- **GUID immutability.** The `UpgradeCode` and the `CliPathEntry` component GUID are generated once (Task 2 Step 1) and must never change — they are how upgrades and PATH management stay stable across releases.
- **Generated artifacts stay out of git.** Only `.gitignore`, the wixproj, the wxs, the workflow, and the doc edits are tracked. `installer/staging/`, `installer/bin/`, `installer/obj/`, and `*.msi` are ignored.
- **Two external/manual dependencies:** (1) the smoke install is best done on the CI runner, not the dev machine; (2) the actual release (tag push → MSI build → GitHub Release asset) is a manual final step.
- **WiX version drift:** if `WixToolset.Sdk` 5.0.2 isn't resolvable, use the current 5.x and keep the SDK + `WixToolset.UI.wixext` versions equal.
