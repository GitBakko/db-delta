# WiX MSI installer + release automation (v0.16.0 / v1.0 RC)

**Status:** released 2026-05-27 · v0.16.0 (MSI smoke-tested on the runner, attached to the GitHub Release)
**Driver:** the last remaining v1.0-RC code item. Spec §1 mandates distribution
as a self-contained Windows artifact, and the tech-stack table mandates **WiX
Toolset v5** for MSI authoring. Today there is **no packaging at all** (no RID /
PublishSingleFile / SelfContained in any csproj, no `.wxs`). This task delivers a
per-machine MSI that installs the desktop app + CLI, built and attached to a
GitHub Release on tag.

## Scope

In scope: self-contained `win-x64` publish of the Avalonia desktop app and the
CLI; a WiX v5 `.wixproj` producing a per-machine MSI (Start-Menu shortcut for
the app, system `PATH` entry for the CLI); a tag-triggered release workflow that
builds the MSI and attaches it to a GitHub Release.

Out of scope (documented, not done): code signing (no certificate — unsigned
MSI triggers a SmartScreen warning); per-user install mode; `win-arm64`;
auto-update; shared-runtime size optimization between the two published apps.

## Decisions (from brainstorming)

- **Deliverable:** one MSI installing BOTH the Avalonia desktop app and the
  `dbdelta` CLI, self-contained `win-x64`.
- **Install scope:** per-machine (`ProgramFiles64Folder`), system `PATH`,
  requires admin/UAC.
- **Build/release:** a new `.github/workflows/release.yml` triggered on `v*`
  tags (and `workflow_dispatch`) builds the MSI on `windows-latest` and attaches
  it to a GitHub Release. `ci.yml` and `docs.yml` are untouched.

## Approach (chosen: `.wixproj` with `WixToolset.Sdk` v5)

A new `installer/DbDelta.Installer.wixproj` uses the `WixToolset.Sdk` MSBuild
SDK (a pinned NuGet package, restored by `dotnet build` — no separate global
tool). This integrates with the existing dotnet/CI flow and pins the WiX version
via the SDK reference. Rejected: WiX as a hand-run `dotnet tool` (less
MSBuild-integrated); non-WiX installers (violate the spec's WiX v5 mandate).

## Components

### 1. Self-contained publish (staging)
`dotnet publish` with `-r win-x64 --self-contained true` (NOT single-file — the
MSI bundles the file tree):
- `src/DbDelta.App.Avalonia` → `installer/staging/app/` (the desktop app + bundled .NET runtime).
- `src/DbDelta.Cli` → `installer/staging/cli/` (`dbdelta.exe` + bundled runtime).

Two runtime copies result (~acceptable for a desktop tool). The `installer/staging/`
tree is build output → git-ignored.

### 2. WiX project — `installer/DbDelta.Installer.wixproj`
- SDK-style project referencing `WixToolset.Sdk` (5.x, pinned), plus
  `WixToolset.UI.wixext` if a minimal UI is wanted (else a silent/basic UI).
- `Package.wxs` defines:
  - `Package` with `Scope="perMachine"`, `ProductVersion` taken from the
    MSBuild `Version` property (passed from the tag), a **fixed `UpgradeCode`
    GUID** (generated once at implementation, immutable thereafter so upgrades
    chain correctly), and `MajorUpgrade` (disallow downgrade, friendly message).
  - Directory layout: `ProgramFiles64Folder\DbDelta\` (app files) and
    `ProgramFiles64Folder\DbDelta\cli\` (CLI files).
  - File harvest of the two `installer/staging/` folders (WiX v5 `Files`
    element / `HarvestDirectory`) so all runtime files are included recursively.
  - A **Start-Menu shortcut** to the app executable (with the app icon).
  - A **system `PATH`** entry appending the `cli` folder (`Environment` element,
    `System`, `action=set` / `part=last`).

### 3. Release workflow — `.github/workflows/release.yml`
```yaml
on:
  push: { tags: [ 'v*' ] }
  workflow_dispatch: { inputs: { version: { description: 'x.y.z', required: true } } }
permissions: { contents: write }
```
One job on `windows-latest`: checkout → setup-dotnet (global.json) →
`dotnet publish` app + cli (self-contained win-x64) into `installer/staging/` →
`dotnet build installer/DbDelta.Installer.wixproj -c Release -p:Version=<ver>` →
attach `DbDelta-<ver>-win-x64.msi` to a GitHub Release
(`softprops/action-gh-release`). `<ver>` = the tag with the leading `v` stripped
(tag flow) or the dispatch input. Independent of `ci.yml` / `docs.yml`.

### 4. Code signing
Out of scope — no certificate. The MSI and bundled executables are unsigned, so
first launch shows a Windows SmartScreen / UAC-unknown-publisher prompt. This is
documented in the CHANGELOG and the getting-started docs; signing is a future
item once a code-signing certificate is available.

## Verification

- **Local MSI build:** `dotnet build installer/DbDelta.Installer.wixproj -c
  Release -p:Version=0.16.0` produces `DbDelta-0.16.0-win-x64.msi` with WiX ICE
  validation clean (no errors). Done on the Windows dev machine; do NOT
  test-install locally (avoid mutating the dev machine's Program Files / PATH).
- **CI smoke install/uninstall (recommended, in the release workflow or a
  manual run):** on the `windows-latest` runner (has admin),
  `msiexec /i DbDelta-*.msi /quiet /qn` → assert the app files exist under
  Program Files and `dbdelta.exe` resolves on `PATH` → `msiexec /x DbDelta-*.msi
  /quiet` to uninstall cleanly. This is the end-to-end gate.
- No unit tests — this is packaging. The gate is: MSI builds, ICE-validates, and
  installs/uninstalls clean on the runner.
- `ci.yml` + `docs.yml` stay green (untouched).

## Risks / notes

- **WiX v5 harvest of a self-contained publish:** confirm the `Files` /
  `HarvestDirectory` element recursively captures every runtime file (the .NET
  self-contained output is ~hundreds of files). Resolve the exact harvest syntax
  during implementation; fall back to an explicit `ComponentGroup` if needed.
- **`ProductVersion` format:** MSI uses a 3-part `major.minor.build` (each ≤
  255). `v0.16.0` → `0.16.0` is fine.
- **Avalonia publish on `win-x64`:** confirm the app project's TFM
  (`net10.0-windows` vs `net10.0`) and `AssemblyName` so the shortcut target and
  publish paths are exact (resolve in the plan).
- **MSI size:** two self-contained runtimes make the MSI large (~150 MB+).
  Acceptable for v1; shared-runtime optimization is a future item.
