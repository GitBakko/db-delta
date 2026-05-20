# 05 — Setup, Installation, and Deployment

> Reference: Redgate SQL Compare 16 documentation (<https://documentation.red-gate.com/sc>)  
> Audience: developers building or distributing a SQL Compare clone  
> Last updated: 2026-05-20

---

## Table of Contents

1. [Target Audiences for Distribution](#1-target-audiences-for-distribution)
2. [System Requirements](#2-system-requirements)
3. [Installation Flows](#3-installation-flows)
4. [First-Run Configuration](#4-first-run-configuration)
5. [Licensing Model](#5-licensing-model)
6. [Connection and Credential Management](#6-connection-and-credential-management)
7. [Updating](#7-updating)
8. [Multi-Machine Patterns](#8-multi-machine-patterns)
9. [CI/CD Integration — Reference Architectures](#9-cicd-integration--reference-architectures)
10. [Security Hardening](#10-security-hardening)
11. [High Availability and Disaster Recovery](#11-high-availability-and-disaster-recovery)
12. [Backup and Restore of User Data](#12-backup-and-restore-of-user-data)
13. [Troubleshooting Common Install Issues](#13-troubleshooting-common-install-issues)
14. [Migration from Other Tools](#14-migration-from-other-tools)
15. [Our Clone's Deployment Plan](#15-our-clones-deployment-plan)

---

## 1. Target Audiences for Distribution

### 1.1 Individual Developer — Workstation Install

The most common case. A single developer installs the GUI application on their Windows workstation to compare schemas interactively, generate migration scripts, and synchronize databases directly. Key expectations:

- One-click installer with sensible defaults
- No elevated permissions required after install (per-user install path)
- SSMS right-click integration for quick access
- Settings and project files stored under the user profile
- License tied to the individual's Redgate account (or our clone's equivalent)

This user will use the GUI almost exclusively. The command line is a bonus. Auto-update should be enabled by default so they get bug fixes without IT involvement.

### 1.2 Build Agent — Silent Install for CI

A CI/CD pipeline runner (GitHub Actions, Azure DevOps, TeamCity, Jenkins) needs SQL Compare on the agent machine to generate and apply migration scripts as part of an automated release pipeline. Key expectations:

- Silent MSI installation (`/quiet`, no UI)
- License activation without interactive prompts (token-based or environment variable)
- Deterministic version — the agent must not self-update mid-pipeline
- Invocation via command line only (no GUI)
- Non-interactive exit codes that can be consumed by pipeline scripts
- Works under a service account with least-privilege permissions

Redgate's own Docker image (`redgate/sqlcompare`, ~53 MB) demonstrates the ideal build-agent packaging: all dependencies bundled, command line entry point, license accepted via a personal access token and email passed as arguments.

### 1.3 Embedding in Another Product — SDK Assemblies

ISVs and internal tooling teams want to consume schema-compare logic as a library rather than shelling out to a separate process. Redgate historically distributed a `RedGate.SQLCompare.Engine.dll` (and related assemblies) that could be referenced in .NET projects. Key expectations:

- NuGet package or assembly folder that can be dropped into a project
- Public API surface for loading schemas, computing differences, generating scripts
- No GUI dependencies (no WinForms or WPF in the hot path)
- License check must work headlessly (no license dialog popup at runtime)
- Versioned and semantically stable so downstream code does not break on minor updates

Our clone must decide early whether we offer an SDK tier. The comparison engine should be architecturally isolated from the GUI layer so that the same assemblies can be packaged independently.

---

## 2. System Requirements

### 2.1 Redgate SQL Compare 16 — Official Requirements

Source: <https://documentation.red-gate.com/sc/getting-started/requirements>

#### Operating System

| OS                    | Minimum Version       | Notes                                 |
|-----------------------|-----------------------|---------------------------------------|
| Windows Desktop       | Windows 10 (1903+)    | GUI fully supported                   |
| Windows Server        | Windows Server 2016+  | Headless / CLI usage                  |
| Linux (CLI only)      | Ubuntu 20.04 / RHEL 8 | Via Docker image; .NET 8 runtime req. |
| macOS                 | Not officially listed | Docker container path only            |

> Note: Older Redgate documentation references Windows 7 / Server 2008 R2. Version 16 aligned to .NET Framework 4.7.2 makes Windows 7 technically feasible but effectively unsupported. The Docker image runs on any host OS that supports Docker with a Windows or Linux container mode.

#### .NET Runtime

| Component              | Required Version         | Notes                                    |
|------------------------|--------------------------|------------------------------------------|
| GUI application        | .NET Framework 4.7.2+    | Ships with Windows 10 1803+              |
| Command line (classic) | .NET Framework 4.7.2+    | Same installer                           |
| Docker image (CLI)     | .NET 8 runtime (bundled) | Self-contained in the container image    |
| SDK assemblies         | .NET Standard 2.0 target | Consumable from .NET Framework or .NET 8 |

#### Memory and Disk

| Resource    | Minimum    | Recommended        |
|-------------|------------|--------------------|
| RAM         | 2 GB       | 4 GB or more       |
| Disk space  | 500 MB     | 1 GB (with caches) |
| Temp space  | 200 MB     | 500 MB             |

Disk usage grows with snapshot size. A snapshot of a large database with thousands of objects can exceed 50 MB.

#### SQL Server Target Versions

SQL Compare 16 support matrix aligns to the Flyway Enterprise SQL Server support matrix. The following table reflects the documented range:

| SQL Server Version            | GUI  | CLI  | Notes                                              |
|-------------------------------|------|------|----------------------------------------------------|
| SQL Server 2008 SP4           | Yes  | Yes  | Legacy; limited feature support                    |
| SQL Server 2012               | Yes  | Yes  |                                                    |
| SQL Server 2014               | Yes  | Yes  |                                                    |
| SQL Server 2016               | Yes  | Yes  |                                                    |
| SQL Server 2017               | Yes  | Yes  |                                                    |
| SQL Server 2019               | Yes  | Yes  |                                                    |
| SQL Server 2022               | Yes  | Yes  |                                                    |
| SQL Server 2025               | Yes  | Yes  | Introduced in SQL Compare 16                       |
| Azure SQL Database            | Yes  | Yes  | Serverless tiers included                          |
| Azure SQL Managed Instance    | Yes  | Yes  |                                                    |
| Amazon RDS for SQL Server     | Yes  | Yes  | Standard and Enterprise editions                   |
| Azure Synapse Analytics (DW)  | No   | No   | Not supported — different dialect                  |

Source: <https://documentation.red-gate.com/xx/support-matrix/sql-server-versions>

#### Optional Components

| Component                        | Required for              | Minimum Version         |
|----------------------------------|---------------------------|-------------------------|
| SSMS (SQL Server Mgmt Studio)    | SSMS integration add-in   | SSMS 18 or SSMS 19/20   |
| Visual C++ Redistributable       | Some older engine builds  | 2015-2022 (x64)         |
| .NET Framework 4.7.2             | GUI and classic CLI       | Ships with Win10 1803+  |
| Windows Installer 4.5            | MSI setup                 | Ships with Win7 SP1+    |

SSMS add-in support tracks SSMS major versions. SSMS 2022 (v19+) introduced API changes that required Redgate to update their extension host; verify compatibility with your SSMS version before deploying.

### 2.2 Our Clone's System Requirements Matrix

| Dimension          | Minimum                        | Target                         | Stretch Goal                   |
|--------------------|--------------------------------|--------------------------------|--------------------------------|
| OS (GUI)           | Windows 10 22H2                | Windows 10/11 (latest)         | Windows + Linux (Avalonia UI)  |
| OS (CLI)           | Windows 10 or Linux (Ubuntu 22)| Any .NET 8 OS                  | macOS ARM64                    |
| .NET runtime       | .NET 8 LTS                     | .NET 8 LTS                     | .NET 10 LTS when released      |
| SQL Server targets | 2016 → 2022, Azure SQL DB      | 2012 → 2025, Azure SQL MI, RDS | Full Redgate parity            |
| RAM                | 2 GB                           | 4 GB                           |                                |
| Disk               | 300 MB install + 200 MB temp   | 1 GB                           |                                |

Rationale for .NET 8 as the baseline: .NET Framework is end-of-evolution, cross-platform support requires the modern runtime, and .NET 8 LTS support runs to November 2026. We avoid .NET Framework dependency by using SMO-compatible libraries or a clean-room schema extraction layer.

---

## 3. Installation Flows

### 3.1 Interactive MSI Install (Typical User)

Redgate distributes a single EXE bootstrapper that embeds the MSI and any prerequisites. The installer:

1. Checks for .NET Framework 4.7.2; installs if missing.
2. Prompts for install scope (current user or all users).
3. Presents feature selection (GUI, CLI, SSMS add-in).
4. Copies files to `%ProgramFiles%\Red Gate\SQL Compare X` (all-users) or `%LOCALAPPDATA%\Programs\Red Gate\SQL Compare X` (per-user).
5. Registers shell extensions and SSMS add-in if selected.
6. Creates Start Menu entries.
7. Writes uninstall information to the registry.

To extract the MSI and MST files from the EXE for silent deployment:

```powershell
# Extract MSI and transform (.mst) files from the EXE bootstrapper
.\SQLCompare_16.x.y.exe extract "C:\Deploy\SQLCompare"
# Output:
#   C:\Deploy\SQLCompare\SQLCompare_16.x.y.msi
#   C:\Deploy\SQLCompare\SQLCompare_16.x.y.mst
```

Source: <https://productsupport.red-gate.com/hc/en-us/articles/360007207454-Installing-from-the-msi-file-silent-install>

### 3.2 Silent Install — CI Agents and Group Policy

```powershell
# Minimum silent install — all features, all users, no UI
msiexec /i "C:\Deploy\SQLCompare\SQLCompare_16.x.y.msi" `
         TRANSFORMS="C:\Deploy\SQLCompare\SQLCompare_16.x.y.mst" `
         ADDLOCAL=ALL `
         /quiet `
         /norestart `
         /log "C:\Logs\SQLCompare_install.log"

# Optional: custom install directory
msiexec /i "C:\Deploy\SQLCompare\SQLCompare_16.x.y.msi" `
         TRANSFORMS="C:\Deploy\SQLCompare\SQLCompare_16.x.y.mst" `
         ADDLOCAL=ALL `
         INSTALLDIR="D:\Tools\SQLCompare" `
         /quiet /norestart
```

Key points:
- The MSI and MST must match in bitness (x64 or x86). Use x64 on all modern systems.
- Always run `msiexec` elevated (as Administrator or via `Start-Process -Verb RunAs`).
- `/quiet` suppresses all UI; `/qn` is equivalent.
- `/norestart` prevents the machine from rebooting without warning — important on shared build agents.
- Log output with `/log` to diagnose failures.
- `ADDLOCAL=ALL` installs all features. To install only the CLI: `ADDLOCAL=CommandLine`.

Group Policy / SCCM deployment: publish the MSI as a machine-scoped package. The transform file must be in the same folder as the MSI on the distribution share.

### 3.3 Per-User vs Per-Machine Install

| Mode        | Install Path                              | Registry Hive   | Admin Required | Recommended For      |
|-------------|-------------------------------------------|-----------------|----------------|----------------------|
| Per-machine | `%ProgramFiles%\Red Gate\SQL Compare X`   | `HKLM`          | Yes            | Build agents, kiosk  |
| Per-user    | `%LOCALAPPDATA%\Programs\Red Gate\...`    | `HKCU`          | No             | Developer workstation|

Per-user installs allow a developer to install without IT involvement. Per-machine is strongly preferred for build agents because it ensures the tool is available regardless of which service account runs the pipeline job.

### 3.4 SDK / Assembly Install

Redgate historically offered the comparison engine as assemblies that third-party tools could reference. In the SDK model:

1. A NuGet package (e.g., `RedGate.SQLCompare.Engine`) is referenced in the consuming project.
2. Assemblies are copied to the output directory on build.
3. License validation occurs at runtime when the engine is first called; it reads a license token from an environment variable or a config file.
4. No MSI is involved; no registry keys are written.

Our clone should publish `SqlCompareClone.Engine` to NuGet.org (or a private feed) separately from the desktop installer. The package should have no GUI dependencies and target `netstandard2.0` for broadest compatibility.

### 3.5 Uninstall and Leftover State Cleanup

**Interactive uninstall** via Settings > Apps.

**Silent uninstall** using the product GUID (find in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`):

```powershell
# Uninstall using product GUID
MsiExec.exe /X{PRODUCT-GUID} /quiet /norestart

# Or using the original MSI path
msiexec /x "C:\Deploy\SQLCompare\SQLCompare_16.x.y.msi" /quiet /norestart
```

**Leftover state that the MSI uninstaller does NOT remove:**

| Location | Contents | Manual Cleanup? |
|----------|----------|-----------------|
| `%APPDATA%\Red Gate\SQL Compare X` | Recent projects, UI state, preferences | Optional — delete for clean slate |
| `%LOCALAPPDATA%\Red Gate\Logs` | Diagnostic logs | Safe to delete |
| `%LOCALAPPDATA%\Red Gate\SQL Compare X` | Cached snapshots | Delete to reclaim disk space |
| `%USERPROFILE%\Documents\SQL Compare` | User-created project files, filters | Do NOT delete — user data |
| Registry `HKCU\Software\Red Gate` | Per-user preferences | Remove with `reg delete` if needed |

A complete clean-uninstall script for CI image cleanup:

```powershell
# After MSI uninstall completes:
$redGatePaths = @(
    "$env:APPDATA\Red Gate",
    "$env:LOCALAPPDATA\Red Gate"
)
foreach ($path in $redGatePaths) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}
reg delete "HKCU\Software\Red Gate" /f 2>$null
```

---

## 4. First-Run Configuration

### 4.1 Telemetry Opt-In

On first launch Redgate presents a telemetry consent dialog. Users can opt in or out. The preference is written to:

```
%APPDATA%\Red Gate\Shared\Telemetry.json
```

For silent deployments, pre-configure this file or set a registry key (varies by version) before first launch to avoid the dialog blocking pipeline execution.

Our clone should default telemetry to opt-out and require explicit opt-in, following modern privacy best practice. Store the preference in:

```
%APPDATA%\SqlCompareClone\telemetry.json
```

### 4.2 License Activation

Redgate's modern activation (v14+) uses a Redgate account sign-in:

1. User clicks "Sign in with Redgate" — opens a browser to `auth.red-gate.com`.
2. After successful auth, a refresh token is stored locally (DPAPI-encrypted).
3. The application polls the license service on startup to validate entitlement.

For offline / CI scenarios Redgate also supports serial-number activation and personal access tokens (PAT):

```powershell
# Activate via command line (serial number, all users)
& "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" `
    /activateSerial:"XXXX-XXXX-XXXX-XXXX" `
    /allUsers
```

The `/allUsers` switch is required when the activation must apply to service accounts (not just the activating user).

### 4.3 User Settings Locations

| File / Directory | Purpose |
|-----------------|---------|
| `%APPDATA%\Red Gate\SQL Compare X\Options.xml` | Global options (comparison defaults, UI) |
| `%APPDATA%\Red Gate\SQL Compare X\RecentProjects.xml` | MRU project list |
| `%APPDATA%\Red Gate\SQL Compare X\Filters\*.scpf` | Named filter files |
| `%USERPROFILE%\Documents\SQL Compare\` | Default folder for new project files |
| `%LOCALAPPDATA%\Red Gate\SQL Compare X\Snapshots\` | Cached database snapshots |

Our clone should use an analogous layout:

```
%APPDATA%\SqlCompareClone\
    options.json
    recent-projects.json
    filters\
%USERPROFILE%\Documents\SqlCompareClone\
    (default project location)
%LOCALAPPDATA%\SqlCompareClone\
    snapshots\
    logs\
```

Prefer JSON over XML for new files — more human-readable and easier to version-control.

### 4.4 Default Project Location

Redgate defaults new project files (`.scp`, XML format) to:
```
%USERPROFILE%\Documents\SQL Compare\
```

Our clone: use the same pattern but under `%USERPROFILE%\Documents\SqlCompareClone\`. Consider allowing the user to set a team-wide default via an environment variable (`SQLCLONE_PROJECTS_DIR`) so that developer workstations aligned to a Git repo can share a projects folder.

### 4.5 Default Options Profile

On first run, write a `DefaultOptions` profile that reflects the most conservative/safest comparison defaults:

- Ignore whitespace: true
- Ignore comments: true
- Ignore constraint names: false
- Ignore user properties: true
- Include deprecated WITH NOCHECK: false
- Generate a deployment transaction: true

Store these as JSON in `options.json`. Let users create named profiles (saved as separate files) and select one as active.

---

## 5. Licensing Model

### 5.1 Redgate's Licensing Model (v16)

**Per-user named licenses**: Each contributor to database changes requires an individual seat. The license is tied to the individual's Redgate account (email). There is no limit on the number of build agent machines; you install the tool on as many agents as needed, but each human user must be licensed. Source: <https://documentation.red-gate.com/sc/getting-started/licensing>

**Editions:**
- **SQL Compare Standard** — aligns to Flyway Teams SQL Server support, GUI + limited CLI
- **SQL Compare Professional** — aligns to Flyway Enterprise, full CLI required for CI/CD automation
- **SQL Toolbelt** — bundle including SQL Compare, SQL Data Compare, SQL Prompt, etc.

**Trial**: 14-day free trial — no credit card required. All features available.

**Subscription vs perpetual**: Redgate moved to subscription-only for new purchases. Perpetual licenses (legacy activation) still exist for older versions.

### 5.2 Activation Methods

| Method | When to Use | How |
|--------|-------------|-----|
| Sign in (online) | Interactive developer workstation | Browser OAuth flow |
| Personal Access Token (PAT) | CI/CD containers and agents | `/token:"<pat>" /email:"<email>"` |
| Serial number (offline) | Air-gapped machines | `/activateSerial:"XXXX-..."` then manual exchange |
| Floating / license server | Enterprise, shared pools | License server URL configured in product |

#### Offline Activation (Air-Gapped)

```powershell
# Step 1: generate activation request on the locked-down machine
& "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" /activateSerial:"XXXX-XXXX-XXXX-XXXX"
# Output: a request string — copy this

# Step 2: on a machine with internet access, go to:
#   https://activate.red-gate.com
# Paste the request string, receive a response string

# Step 3: back on the locked-down machine
& "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" /activateResponse:"<response string>"
```

Source: <https://forum.red-gate.com/discussion/82720>

### 5.3 Floating License Server

Redgate provides a Licensing Server component for enterprises. The license server:
- Checks out seats from a pool when a user launches the product
- Returns the seat when the application closes or after a configurable idle timeout
- Runs as a Windows Service on a central server
- Exposes a web management UI on port 8080 by default
- Requires inbound access from client machines on the configured port

Configuration on the client:
```
%APPDATA%\Red Gate\Shared\LicenseServer.xml
```
```xml
<LicenseServer>
  <Uri>http://license-server.internal:8080/</Uri>
</LicenseServer>
```

### 5.4 Audit and Compliance

Redgate's Customer Portal provides:
- List of activated machines per license
- Usage reports (last-seen dates)
- Over-use alerts

Our clone should implement equivalent audit capabilities if we offer floating licenses.

### 5.5 Our Clone's Licensing Strategy

We propose a dual approach:

| Track | License | Use Case |
|-------|---------|----------|
| Community | MIT or Apache 2.0 open source | Individual developers, OSS projects, learning |
| Commercial | Proprietary subscription | Teams, CI/CD, SDK embedding, priority support |

For the commercial track, evaluate:
- **Cryptlex** or **Keygen.sh** for license server infrastructure (avoid building from scratch)
- JWT-signed license files that embed feature flags and expiry dates
- An optional self-hosted license server for enterprises with no internet egress

The SDK/NuGet package can use a different license key namespace from the desktop installer. Both should validate against the same backend.

---

## 6. Connection and Credential Management

### 6.1 Project File Storage of Connection Strings

SQL Compare stores connection settings in `.scp` project files (XML). Each data source element contains:

```xml
<DataSource Type="Database">
  <Server>myserver\sql2019</Server>
  <Database>AdventureWorks</Database>
  <AuthType>Windows</AuthType>
  <!-- SQL auth: -->
  <UserName>sa</UserName>
  <Password>DPAPI-encrypted-base64-blob</Password>
</DataSource>
```

Passwords are encrypted with the Windows Data Protection API (DPAPI) using **user-scope** protection (`DPAPI_CURRENT_USER`). This means:
- The encrypted blob is only decryptable by the same Windows user account on the same machine.
- If the project file is copied to another machine or opened by a different user, the password cannot be recovered and the user must re-enter it.
- This is intentional security behavior — it prevents credential theft via file copy.

**Machine-scope DPAPI** (`DPAPI_CURRENT_MACHINE`) decrypts as any user on the same machine. Redgate does not use this for passwords because it weakens protection on multi-user machines.

### 6.2 Encryption at Rest — Our Recommendations

| Scenario | Recommendation |
|----------|----------------|
| Developer workstation, Windows auth | Store no credentials — use Windows Integrated Security |
| Developer workstation, SQL auth | DPAPI user-scope encryption in project file |
| Build agent, SQL auth | Read password from environment variable; never write to project file on disk |
| Build agent, cloud SQL | Use Managed Identity (no credential stored anywhere) |
| Shared team project file | Store only server/database name; prompt for credentials at runtime |

Our clone should never write plaintext passwords to disk. If SQL auth credentials must be persisted, use DPAPI user-scope and clearly communicate the limitation (not portable, tied to the encrypting user's profile).

### 6.3 Azure AD / Managed Identity / MFA

Modern cloud SQL authentication:

| Auth Method | Connection String Keyword | Notes |
|-------------|--------------------------|-------|
| Azure AD Password | `Authentication=Active Directory Password` | Requires MFA-exempt account |
| Azure AD Integrated | `Authentication=Active Directory Integrated` | Requires Azure AD-joined machine |
| Azure AD Interactive (MFA) | `Authentication=Active Directory Interactive` | Prompts browser on each connection |
| Managed Identity | `Authentication=Active Directory Managed Identity` | Works on Azure VMs and containers |
| Service Principal | `Authentication=Active Directory Service Principal` | CI/CD preferred |

For CI pipelines targeting Azure SQL, the recommended flow:
1. Assign a Managed Identity to the Azure DevOps self-hosted agent VM.
2. Grant the identity `db_owner` (or least privilege) on the target databases.
3. Use `Authentication=Active Directory Managed Identity` in the connection string.
4. No secret required anywhere in the pipeline YAML.

### 6.4 SSPI / Windows Auth

On-premises SQL Servers typically use Windows Integrated Authentication (SSPI). In a pipeline:
- The agent service account must be a domain account (not `SYSTEM` or `NetworkService` for cross-machine auth).
- Grant the service account appropriate SQL Server logins on source and target.
- Use `Integrated Security=SSPI` in connection strings.

### 6.5 Our Clone's Credential Recommendations

1. Implement a `CredentialStore` interface with two backends:
   - `DpapiCredentialStore` — for interactive desktop use (Windows only)
   - `EnvironmentVariableCredentialStore` — reads `SQLCLONE_PASSWORD_<alias>` environment variables for CI
2. Add a `--no-save-password` flag to the CLI to prevent any credential persistence.
3. Support Azure Key Vault as a third backend via `Azure.Security.KeyVault.Secrets` package.
4. Never log connection strings or passwords to stdout/log files.

---

## 7. Updating

### 7.1 Auto-Update Channel

Redgate products check for updates on startup by querying a Redgate update feed. If a newer version is found the user is prompted (not auto-installed). The check is skipped if the machine has no internet access or if the user has disabled update checks.

Update check endpoint (for reference): `https://update.red-gate.com/updates/{product}/{version}`

The update mechanism respects:
- Whether the user opted out in preferences
- Whether the application was installed per-user (can self-update) vs per-machine (requires elevation)

### 7.2 Check-for-Updates UI

A manual "Check for updates" menu item triggers the same check on demand. If an update is available, the UI shows the version number and release notes, then offers to download and install. The download is the full installer EXE.

### 7.3 Forced Version Pinning for Build Agents

Build agents should never auto-update. A mid-pipeline version change can break scripts that relied on specific output formats or exit codes.

Strategies for pinning:
1. **Install from a versioned artifact store**: upload the specific installer EXE/MSI to S3, Azure Blob, or Artifactory. Reference that exact URL in your agent provisioning scripts.
2. **Disable update checks**: set the registry key (or our equivalent config) to disable the update check entirely on build-agent images.
3. **Immutable agent images**: build a VM image (Packer) or container image with a specific version baked in. Never run the installer at pipeline start.

```powershell
# Disable update checks via registry (Redgate pattern)
New-ItemProperty -Path "HKCU:\Software\Red Gate\SQL Compare 16" `
    -Name "AutoUpdateEnabled" -Value 0 -PropertyType DWORD -Force
```

For our clone, expose an explicit config value:
```json
// %APPDATA%\SqlCompareClone\options.json
{
  "updates": {
    "checkOnStartup": false,
    "channel": "stable"
  }
}
```

### 7.4 Side-by-Side Major Versions

Redgate does not officially support running multiple major versions simultaneously, though in practice different major version directories coexist in `%ProgramFiles%` without conflict because each registers separately.

Our clone should:
- Install to versioned directories: `%ProgramFiles%\SqlCompareClone\1.x\`
- Namespace registry keys by major version
- Allow the user to set which version is the "default" in PATH

---

## 8. Multi-Machine Patterns

### 8.1 Solo Developer Workstation

```
Developer Laptop
  └── SQL Compare GUI (per-user install)
        ├── SSMS add-in (right-click compare)
        ├── Project files in ~/Documents/SQL Compare
        └── License: personal subscription
```

No special configuration. Auto-update enabled. SSMS integration is the primary workflow entry point.

### 8.2 Shared Build Agent Pool (Floating License)

```
License Server (Windows VM, internal network)
  └── Redgate License Server service :8080

Build Agent 1 ──┐
Build Agent 2 ──┼── per-machine install of SQL Compare CLI
Build Agent 3 ──┘
   Each agent checks out a seat from the license server at job start
   and returns it at job end.
```

With Redgate's per-user model (unlimited agents, licensed humans), this pattern is simpler than it looks: install the CLI on all agents, activate with the team PAT or machine activation, and run. The limiting factor is concurrent human users, not concurrent agent runs.

For our clone's floating license model:
1. Configure the license server URL as an environment variable on all agents: `SQLCLONE_LICENSE_SERVER=http://license-server:8443`
2. The CLI checks out a seat on start, releases on exit (or on a 30-minute TTL).
3. If no seat is available, the CLI exits with code 10 (custom: "license unavailable") and the pipeline retries after a delay.

### 8.3 Container Image for Ephemeral CI Runners

Redgate provides an official Docker image (`redgate/sqlcompare`, ~53 MB) that bundles the CLI and all dependencies. This is the cleanest model for ephemeral runners.

**Base image analysis:**
```dockerfile
# Redgate's image uses a Windows Server Core or .NET runtime base
# For our clone, a Linux-based image is preferred (.NET 8):
FROM mcr.microsoft.com/dotnet/runtime:8.0-jammy AS base
WORKDIR /app
COPY ./publish/ .
ENTRYPOINT ["dotnet", "SqlCompareClone.Cli.dll"]
```

**License activation per container:**

Since containers are ephemeral and DPAPI is not available in Linux containers, authentication must be stateless:
- Pass a short-lived PAT as an environment variable: `SQLCLONE_LICENSE_TOKEN`
- The CLI validates the token against the license API on startup (one HTTPS call)
- No license state is persisted in the container filesystem

```dockerfile
# docker-compose.yml snippet for CI
services:
  schema-compare:
    image: sqlcompareclone/cli:1.2.0
    environment:
      SQLCLONE_LICENSE_TOKEN: ${SQLCLONE_LICENSE_TOKEN}
      SQLCLONE_LICENSE_EMAIL: ${SQLCLONE_LICENSE_EMAIL}
    volumes:
      - ./scripts:/work/scripts
```

Docker on Windows (Windows containers) is also viable for a Redgate-compatible clone, but the image size increases substantially (~4 GB for Server Core vs ~200 MB for Linux). Prefer Linux containers.

---

## 9. CI/CD Integration — Reference Architectures

### 9.1 GitHub Actions — Windows Runner

```yaml
# .github/workflows/schema-compare.yml
name: Schema Compare and Deploy

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  schema-compare:
    runs-on: windows-latest
    env:
      SOURCE_SERVER: ${{ secrets.SOURCE_SQL_SERVER }}
      TARGET_SERVER: ${{ secrets.TARGET_SQL_SERVER }}
      SQL_USERNAME: ${{ secrets.SQL_SA_USER }}
      SQL_PASSWORD: ${{ secrets.SQL_SA_PASSWORD }}

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Install SQL Compare CLI
        shell: powershell
        run: |
          $version = "16.5.0.1234"
          $url = "https://artifacts.example.com/sqlcompare/$version/SQLCompare_$version.exe"
          Invoke-WebRequest -Uri $url -OutFile "SQLCompare_install.exe"
          Start-Process "SQLCompare_install.exe" `
            -ArgumentList "extract `"$env:TEMP\sqlcompare`"" `
            -Wait -NoNewWindow
          msiexec /i "$env:TEMP\sqlcompare\SQLCompare_$version.msi" `
                  TRANSFORMS="$env:TEMP\sqlcompare\SQLCompare_$version.mst" `
                  ADDLOCAL=CommandLine /quiet /norestart
          Write-Host "SQL Compare installed"

      - name: Activate license
        shell: powershell
        run: |
          & "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" `
            /activateSerial:"${{ secrets.REDGATE_SERIAL }}" /allUsers

      - name: Compare schemas
        shell: powershell
        run: |
          & "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe" `
            /s1:"$env:SOURCE_SERVER" /db1:MyDatabase /u1:"$env:SQL_USERNAME" /p1:"$env:SQL_PASSWORD" `
            /s2:"$env:TARGET_SERVER" /db2:MyDatabase /u2:"$env:SQL_USERNAME" /p2:"$env:SQL_PASSWORD" `
            /ScriptFile:"${{ github.workspace }}\deploy.sql" `
            /Force /Quiet
          if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 63) {
            Write-Error "SQL Compare failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
          }
          Write-Host "Schema comparison complete"

      - name: Upload migration script
        uses: actions/upload-artifact@v4
        with:
          name: migration-script
          path: deploy.sql
          if-no-files-found: warn
```

Exit code 63 means "no differences found" — treat it as success.

### 9.2 GitHub Actions — Container (Linux, Our Clone)

```yaml
# .github/workflows/schema-compare-container.yml
name: Schema Compare (Container)

on:
  push:
    branches: [main]

jobs:
  schema-compare:
    runs-on: ubuntu-latest
    container:
      image: sqlcompareclone/cli:1.2.0
      env:
        SQLCLONE_LICENSE_TOKEN: ${{ secrets.SQLCLONE_TOKEN }}
        SQLCLONE_LICENSE_EMAIL: ${{ secrets.SQLCLONE_EMAIL }}

    steps:
      - uses: actions/checkout@v4

      - name: Compare schemas
        run: |
          sqlclone compare \
            --source "Server=${{ vars.SOURCE_SERVER }};Database=MyDb;Authentication=Active Directory Managed Identity" \
            --target "Server=${{ vars.TARGET_SERVER }};Database=MyDb;Authentication=Active Directory Managed Identity" \
            --output ./scripts/migration.sql \
            --abort-on-warnings High

      - uses: actions/upload-artifact@v4
        with:
          name: migration-script
          path: scripts/migration.sql
```

### 9.3 Azure DevOps — Windows Self-Hosted Agent

```yaml
# azure-pipelines.yml
trigger:
  - main

pool:
  name: 'SQL-Agent-Pool'   # Windows self-hosted pool with SQL Compare pre-installed

variables:
  - group: 'database-secrets'   # Variable group with SQL credentials

stages:
  - stage: SchemaValidation
    displayName: 'Schema Validation'
    jobs:
      - job: CompareSchemas
        displayName: 'Compare and Script'
        steps:
          - task: PowerShell@2
            displayName: 'Verify SQL Compare is installed'
            inputs:
              targetType: inline
              script: |
                $exe = "C:\Program Files\Red Gate\SQL Compare 16\SQLCompare.exe"
                if (-not (Test-Path $exe)) {
                  Write-Error "SQL Compare not found at $exe. Provision the agent image."
                  exit 1
                }
                & $exe /version
                Write-Host "##vso[task.setvariable variable=SqlCompareExe]$exe"

          - task: PowerShell@2
            displayName: 'Compare schemas'
            inputs:
              targetType: inline
              script: |
                $args = @(
                  "/s1:$(SOURCE_SERVER)", "/db1:$(SourceDatabase)",
                  "/u1:$(SQL_USER)", "/p1:$(SQL_PASSWORD)",
                  "/s2:$(TARGET_SERVER)", "/db2:$(TargetDatabase)",
                  "/u2:$(SQL_USER)", "/p2:$(SQL_PASSWORD)",
                  "/ScriptFile:$(Build.ArtifactStagingDirectory)\migration.sql",
                  "/AbortOnWarnings:High",
                  "/Force", "/Quiet"
                )
                & "$(SqlCompareExe)" @args
                if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 63) {
                  Write-Error "SQL Compare exited with $LASTEXITCODE"
                  exit $LASTEXITCODE
                }

          - task: PublishBuildArtifacts@1
            displayName: 'Publish migration script'
            condition: always()
            inputs:
              pathToPublish: '$(Build.ArtifactStagingDirectory)'
              artifactName: 'migration-scripts'
```

Reference: <https://documentation.red-gate.com/rcc4/deploying-database-changes/example-ci-cd-pipelines/tutorial-implement-azure-devops-classic-pipelines-for-sql-server-with-a-self-hosted-agent>

### 9.4 Linux Agent — Cross-Platform Considerations

**Redgate SQL Compare (official)**: Historically Windows-only. The Redgate Docker image (`redgate/sqlcompare`) runs on a Linux Docker host using a Linux container, indicating that the CLI has been ported to .NET (likely .NET 6/8) for the containerized distribution. However, a bare-metal Linux install (apt/yum) is not officially supported as of v16.

**Our clone**: Targeting .NET 8 from the start means the CLI runs natively on Linux without Docker. The key challenges:

| Challenge | Solution |
|-----------|----------|
| No DPAPI on Linux | Use environment variables or Azure Key Vault for credentials |
| SMO not available on Linux | Replace with `Microsoft.Data.SqlClient` for schema introspection |
| Windows-only APIs (registry) | Abstract behind an `IPlatformServices` interface |
| SSMS add-in | Not applicable on Linux; GUI only on Windows |

**Self-hosted vs managed runner trade-offs:**

| Factor | Managed Runner (GitHub/Azure) | Self-Hosted Runner |
|--------|------------------------------|-------------------|
| Maintenance | None | Team responsible |
| Tool pre-install | Each run provisions tools (~2-5 min overhead) | Tools baked in (fast) |
| Network access to SQL | Requires VPN or public endpoint | Direct access if on same network |
| Cost | Per-minute billing | Infrastructure cost |
| Ephemeral | Yes | Configurable |
| License pinning | Use versioned artifact | Pre-installed, frozen |

Recommendation: use self-hosted Windows agents for pipelines that must reach private SQL Servers; use Linux containers on managed runners for pipelines targeting Azure SQL (public endpoint + Managed Identity).

---

## 10. Security Hardening

### 10.1 Service Account Permissions on the Build Agent

The Windows service account running the agent process needs:

| Permission | Why |
|------------|-----|
| `Log on as a service` | Run as a Windows service |
| Read access to the SQL Compare install dir | Execute the CLI |
| Write access to the pipeline workspace dir | Write migration scripts |
| Write access to the log directory | Diagnostic logs |
| No admin rights | Least privilege; installer is pre-baked |
| No access to `%USERPROFILE%` of other users | Prevent credential theft |

SQL Server permissions for the service account:

```sql
-- Minimum permissions for schema comparison (read-only source)
CREATE LOGIN [DOMAIN\build-agent] FROM WINDOWS;
USE [SourceDatabase];
CREATE USER [build-agent] FOR LOGIN [DOMAIN\build-agent];
ALTER ROLE [db_datareader] ADD MEMBER [build-agent];
GRANT VIEW DEFINITION TO [build-agent];

-- Permissions for deployment (target database)
USE [TargetDatabase];
CREATE USER [build-agent] FOR LOGIN [DOMAIN\build-agent];
ALTER ROLE [db_ddladmin] ADD MEMBER [build-agent];
GRANT ALTER ON SCHEMA::dbo TO [build-agent];
```

Never grant `sysadmin` or `db_owner` to the build agent service account.

### 10.2 Network Access to SQL Servers

| Scenario | Recommendation |
|----------|---------------|
| Agent in same VNet as SQL Server | Use private endpoint; no public exposure |
| Agent in GitHub managed runner → Azure SQL | Enable Azure SQL firewall rule for the GitHub runner IP range, or use private link + self-hosted runner |
| Agent on-premises → cloud SQL | Site-to-site VPN or ExpressRoute; avoid opening port 1433 to the internet |
| Container on shared runner | Managed Identity + Azure SQL service endpoint |

Use SQL Server's encrypted connections (`Encrypt=True;TrustServerCertificate=False`) for all CI connections. Redgate documents how to force encrypted connections: <https://documentation.red-gate.com/sc/getting-more-from-sql-compare/forcing-sql-compare-and-sql-data-compare-to-use-an-encrypted-connection>

### 10.3 Secrets Management for Connection Strings

Never put SQL passwords in pipeline YAML files, project files committed to git, or log output.

**GitHub Actions:**
```yaml
env:
  SQL_PASSWORD: ${{ secrets.SQL_SA_PASSWORD }}
```

**Azure DevOps — Azure Key Vault task:**
```yaml
- task: AzureKeyVault@2
  inputs:
    azureSubscription: 'MyServiceConnection'
    KeyVaultName: 'my-keyvault'
    SecretsFilter: 'sql-build-agent-password,sql-source-password'
    RunAsPreJob: true
```

**HashiCorp Vault:**
```yaml
- name: Import secrets from Vault
  uses: hashicorp/vault-action@v3
  with:
    url: https://vault.internal:8200
    method: approle
    roleId: ${{ secrets.VAULT_ROLE_ID }}
    secretId: ${{ secrets.VAULT_SECRET_ID }}
    secrets: |
      secret/data/ci/sql password | SQL_PASSWORD ;
      secret/data/ci/sql username | SQL_USERNAME
```

**AWS Secrets Manager** for RDS targets:
```powershell
# Retrieve RDS credentials from AWS Secrets Manager
$secret = Get-SECSecretValue -SecretId "prod/rds/schema-compare" -Region "us-east-1"
$creds = $secret.SecretString | ConvertFrom-Json
$env:SQL_PASSWORD = $creds.password
```

### 10.4 Audit Logging

Our clone should log the following events to a structured log (JSON lines):

```json
{
  "timestamp": "2026-05-20T10:23:41Z",
  "event": "comparison.started",
  "user": "DOMAIN\\build-agent",
  "source": { "server": "src-sql", "database": "MyDb" },
  "target": { "server": "tgt-sql", "database": "MyDb" },
  "mode": "cli",
  "version": "1.2.0"
}
{
  "timestamp": "2026-05-20T10:23:58Z",
  "event": "deployment.completed",
  "differences_applied": 42,
  "warnings": 0,
  "exit_code": 0
}
```

Never log passwords, connection string passwords, or license tokens in audit logs. Hash server names if required by data residency policy.

---

## 11. High Availability and Disaster Recovery

This section focuses on the build infrastructure, not on SQL Compare itself.

### 11.1 Agent Pool Redundancy

Run at least two build agents in your SQL-Compare-capable pool. If one agent is down (patching, hardware failure), pipelines queue to the other. Azure DevOps and GitHub Actions both support agent pools with N > 1 agents.

Use immutable agent images (VM scale sets or pre-baked AMIs) so that recovery is a matter of re-provisioning from the golden image, not re-running a setup script.

### 11.2 License Server Availability

If you run a floating license server, it becomes a single point of failure. Mitigation options:
- Run two license server instances behind a load balancer (active-passive).
- Configure clients with a primary and fallback URL.
- Grant a grace period (e.g., 24 hours) during which the client operates without contacting the license server — prevents a license server outage from blocking all deployments.

### 11.3 Artifact Retention

Pipeline-generated migration scripts (`.sql` files) should be stored as build artifacts with a minimum 90-day retention, allowing rollback analysis. Store artifacts in durable storage (Azure Blob, S3) rather than relying solely on the CI platform's default artifact storage.

---

## 12. Backup and Restore of User Data

### 12.1 What Constitutes User Data

| Artifact | Format | Criticality |
|----------|--------|-------------|
| Project files (`.scp` / `.json`) | XML / JSON | High — defines all comparison setups |
| Filter files (`.scpf`) | XML | High — custom object filters |
| Snapshots | Binary / compressed | Medium — reproducible from live databases |
| Options profile | XML / JSON | Low — can be re-configured |
| Recent project list | XML | Low — convenience only |

### 12.2 Recommended Folder Structure for Teams

```
\\fileserver\SqlCompare\
  ├── projects\
  │     ├── AdventureWorks\
  │     │     ├── prod-vs-staging.sqlcmp
  │     │     └── dev-vs-prod.sqlcmp
  │     └── Northwind\
  │           └── baseline.sqlcmp
  ├── filters\
  │     ├── IgnorePermissions.scpf
  │     └── ExcludeAuditTables.scpf
  └── snapshots\
        ├── AdventureWorks_prod_2026-05-19.snap
        └── AdventureWorks_staging_2026-05-19.snap
```

Check project files and filter files into source control (they are text/XML). Do not check in snapshots — they are large and contain schema data that can be regenerated.

### 12.3 Backup Recommendations

- **Project and filter files**: version-controlled in the same Git repo as database migration scripts. Treat them as code.
- **Snapshots**: retain a rolling window of the last 30 days. Use backup software or a cron job to archive to object storage.
- **Options profile**: export and commit to a team configuration repo. Allows consistent defaults across all team members.

```powershell
# Example: nightly snapshot backup to Azure Blob
$snapshotDir = "$env:LOCALAPPDATA\SqlCompareClone\snapshots"
$date = Get-Date -Format "yyyy-MM-dd"
$container = "sql-snapshots"
az storage blob upload-batch `
    --source $snapshotDir `
    --destination "$container/$date" `
    --account-name mystorageaccount `
    --auth-mode login
```

---

## 13. Troubleshooting Common Install Issues

### 13.1 Missing .NET Runtime

**Symptom**: Application fails to launch with "The program can't start because a required .NET component is missing."

**Resolution**:
```powershell
# Check installed .NET Framework versions
Get-ChildItem "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP" -Recurse |
    Get-ItemProperty -Name Version, Release -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match "^(?!S)\p{L}" } |
    Select-Object PSChildName, Version, Release

# Install .NET Framework 4.8 (superset of 4.7.2)
# Download from: https://dotnet.microsoft.com/download/dotnet-framework/net48
# Silent install:
.\ndp48-web.exe /quiet /norestart
```

For our clone (.NET 8): install the .NET 8 Desktop Runtime from <https://dotnet.microsoft.com/download/dotnet/8.0>.

### 13.2 Blocked MSI on Managed Windows

**Symptom**: MSI install fails with error 1625 ("This installation is forbidden by system policy").

**Resolution**:
1. Check Group Policy: `gpedit.msc` → Computer Configuration → Administrative Templates → Windows Components → Windows Installer → "Prohibit User installs" / "Always install with elevated privileges"
2. Ask your IT admin to add a software restriction policy exception for the Redgate installer hash.
3. Deploy via SCCM/Intune as a machine-scoped package instead (bypasses user install restrictions).
4. Alternatively: use the portable ZIP/container distribution that requires no installer.

### 13.3 License Activation Failures (Proxy/Firewall)

**Symptom**: Activation fails with "Unable to connect to the activation server."

**Resolution**:
1. Verify internet access to `https://activate.red-gate.com` and `https://licensing.red-gate.com` on port 443.
2. Configure proxy settings:
```xml
<!-- %APPDATA%\Red Gate\Shared\ProxySettings.xml -->
<ProxySettings>
  <UseProxy>true</UseProxy>
  <Host>proxy.internal</Host>
  <Port>8080</Port>
  <UserName>domain\user</UserName>
  <Password>DPAPI-encrypted</Password>
</ProxySettings>
```
3. If the proxy performs SSL inspection, add the proxy's CA certificate to the Windows Trusted Root store.
4. For fully air-gapped machines, use the offline activation procedure (section 5.2).

### 13.4 Permissions to Write %APPDATA%

**Symptom**: Application crashes or shows settings-related errors on first run.

**Cause**: The service account or user profile does not have write access to `%APPDATA%`.

**Resolution**:
```powershell
# Verify the account has access
$appdata = [Environment]::GetFolderPath("ApplicationData")
icacls $appdata

# Grant access if running as a service account with a redirected profile
$account = "DOMAIN\build-agent"
$redGatePath = Join-Path $appdata "Red Gate"
New-Item -ItemType Directory -Path $redGatePath -Force
icacls $redGatePath /grant "${account}:(OI)(CI)F" /T
```

For containerized environments, ensure the container user has write access to the working directory and that settings are written to a volume-mounted path.

### 13.5 SSMS Add-In Not Loading

**Symptom**: No "SQL Compare" option appears in the SSMS Tools menu or right-click context menu.

**Diagnostic steps**:
```powershell
# Check extension directory
$ssmsExtDir = "C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Extensions"
Get-ChildItem $ssmsExtDir | Where-Object Name -like "*Red Gate*"

# Check event viewer for add-in load errors
Get-EventLog -LogName Application -Source "SSMS" -Newest 20 |
    Where-Object Message -like "*Red Gate*" |
    Select-Object TimeGenerated, Message
```

**Common fixes**:
1. Repair the installation: `Settings > Apps > SQL Compare > Modify > Repair`.
2. Delete the extension directory and reinstall: `C:\Program Files (x86)\Microsoft SQL Server Management Studio XX\Common7\IDE\Extensions\Red Gate SQL Compare`
3. Clear the SSMS extension cache: delete `%APPDATA%\Microsoft\SQL Server Management Studio\20.0\Extensions` and restart SSMS.
4. Ensure SSMS version is supported (SSMS 18 or 19/20). SSMS 17 and earlier are not supported by SQL Compare 16.

Source: <https://productsupport.red-gate.com/hc/en-us/articles/360003503274>

### 13.6 Exit Code Reference

| Code | Meaning | Action |
|------|---------|--------|
| 0 | Success with differences found and applied | OK |
| 1 | Unhandled error | Check log |
| 61 | Aborted due to warnings | Review warnings |
| 63 | No differences found | Treat as success in CI |
| 64 | Invalid command line arguments | Fix the script |
| 65 | Cannot connect to data source | Check connectivity |
| 66 | License error | Check activation |

---

## 14. Migration from Other Tools

### 14.1 From Visual Studio SSDT (Schema Compare)

**What transfers:**
- The SSDT `.scmp` file is XML. You can parse the data source names and comparison options and convert them to our `.sqlcmp` project format.
- Exclusion rules (objects to ignore) translate to filter files.
- Pre/post-deployment scripts are standalone `.sql` files — fully portable.

**What does not transfer:**
- SSDT project references (`.sqlproj`) define build-time schema; SQL Compare works with live databases and snapshot files. The workflow is fundamentally different.
- Dacpac-based deployment is distinct from script-based deployment.

**Migration script sketch (PowerShell):**
```powershell
[xml]$scmp = Get-Content "MyComparison.scmp"
$source = $scmp.SchemaComparison.SourceConnectionString
$target = $scmp.SchemaComparison.TargetConnectionString
# Convert to our project format...
```

### 14.2 From ApexSQL Diff

ApexSQL Diff stores projects as `.axds` XML files. The schema is similar in intent to `.scp` files. Key mapping:

| ApexSQL Diff | SQL Compare Clone |
|-------------|------------------|
| `<Source>` / `<Target>` elements | Data source config |
| Object exclusion lists | Filter files |
| Comparison options | Options profile |
| Deployment script output | `/ScriptFile` equivalent |

No automated migration tool exists; manual re-entry of connection details is typically the fastest path. Filter files may need manual recreation.

### 14.3 From dbForge Schema Compare

dbForge uses `.scomp` project files (XML). The structure is similar to Redgate's. Connection details, object filters, and comparison options can be extracted and mapped.

**What transfers cleanly:**
- Server names and database names
- Object type exclusions
- Comparison options (most map 1:1)

**What does not:**
- dbForge's "snapshot" format is proprietary and not interchangeable
- Any dbForge-specific comparison rules or object groupings

### 14.4 General Advice for Migrations

1. Export a snapshot from the production database using the old tool before switching. Keep it as a reference.
2. Re-create project files in the new tool one at a time; do not attempt bulk migration for complex setups.
3. Run both tools against the same two databases and compare the difference sets — any discrepancies indicate edge cases in the new tool that need investigation.
4. Document which objects were excluded in the old tool and why. These exclusion rules encode institutional knowledge.

---

## 15. Our Clone's Deployment Plan

### 15.1 Distribution Channels

| Channel | Target Audience | Format | Priority |
|---------|----------------|--------|----------|
| GitHub Releases (direct download) | All users | `.exe` bootstrapper + `.zip` portable | P0 |
| Chocolatey community feed | Windows developers | `choco install sqlcompareclone` | P1 |
| WinGet manifest | Windows 11/10 users | `winget install SqlCompareClone` | P1 |
| NuGet.org | SDK consumers | `SqlCompareClone.Engine` package | P0 |
| Docker Hub | CI/CD, Linux, containers | `sqlcompareclone/cli:1.x` | P1 |
| Scoop bucket | Developer-focused users | Custom scoop bucket | P2 |

**Chocolatey package (outline):**
```powershell
# tools\chocolateyInstall.ps1
$packageArgs = @{
  packageName   = 'sqlcompareclone'
  fileType      = 'exe'
  url64bit      = 'https://github.com/your-org/sqlcompareclone/releases/download/v1.2.0/SqlCompareClone_1.2.0_x64.exe'
  checksum64    = 'SHA256-HASH-HERE'
  checksumType64= 'sha256'
  silentArgs    = '/quiet /norestart'
  validExitCodes= @(0, 3010)
}
Install-ChocolateyPackage @packageArgs
```

### 15.2 Version and Release Cadence Proposal

| Release Type | Frequency | Content |
|-------------|-----------|---------|
| Patch (1.x.y) | As needed (bug fixes) | Bug fixes, security patches |
| Minor (1.x.0) | Every 6-8 weeks | New SQL Server version support, feature additions |
| Major (x.0.0) | Annually | Breaking API changes, major architecture shifts |

Follow semantic versioning (SemVer 2.0). Tag every release in Git. Publish release notes in GitHub Releases.

Pin the CI agent version via a `SQLCOMPARECLONE_VERSION` environment variable so pipelines can independently upgrade.

### 15.3 Update Server Architecture

```
GitHub Releases (source of truth)
        │
        ▼
Update manifest API (minimal REST service)
  GET /api/updates/latest?channel=stable&version=1.1.0
  Response: { "version": "1.2.0", "url": "...", "sha256": "..." }
        │
        ▼
Client checks on startup → prompts if newer version available
```

The update manifest API can be a static JSON file hosted on GitHub Pages or a CDN — no server required initially:

```
https://updates.sqlcompareclone.io/stable/latest.json
```

```json
{
  "version": "1.2.0",
  "releaseDate": "2026-05-15",
  "url": "https://github.com/your-org/sqlcompareclone/releases/download/v1.2.0/SqlCompareClone_1.2.0_x64.exe",
  "sha256": "abc123...",
  "releaseNotesUrl": "https://github.com/your-org/sqlcompareclone/releases/tag/v1.2.0",
  "minimumVersion": "1.0.0"
}
```

### 15.4 Containerization Roadmap

| Milestone | Target | Content |
|-----------|--------|---------|
| v1.0 | Q3 2026 | Windows container (Server Core) — parity with Redgate's Docker image |
| v1.1 | Q4 2026 | Linux container (Debian slim, .NET 8) — CLI only, Azure SQL targets |
| v1.2 | Q1 2027 | Multi-arch manifest (linux/amd64, linux/arm64) |
| v2.0 | Q2 2027 | Kubernetes-native: Helm chart for license server + compare job runner |

**Linux Dockerfile (v1.1 target):**
```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim

LABEL maintainer="your-org" \
      version="1.1.0" \
      description="SQL Compare Clone CLI for Linux"

WORKDIR /app

# Create non-root user
RUN adduser --disabled-password --gecos '' compareuser

# Copy published CLI
COPY --chown=compareuser:compareuser ./publish/ .

# Runtime configuration directory
RUN mkdir -p /home/compareuser/.config/sqlcompareclone && \
    chown compareuser:compareuser /home/compareuser/.config/sqlcompareclone

USER compareuser

ENTRYPOINT ["dotnet", "SqlCompareClone.Cli.dll"]
CMD ["--help"]
```

### 15.5 Cross-Platform Roadmap (.NET 8 → Linux/macOS)

**Phase 1 — CLI on Linux (achievable immediately with .NET 8):**
- Remove all Windows-specific APIs from `SqlCompareClone.Engine` and `SqlCompareClone.Cli`
- Replace DPAPI with environment-variable credential provider on non-Windows
- Replace registry-based config with JSON config in `~/.config/sqlcompareclone/`
- Use `Microsoft.Data.SqlClient` (cross-platform) instead of any Windows-only SQL connectivity

**Phase 2 — GUI on Linux/macOS (Avalonia UI):**
- Avalonia provides a cross-platform XAML-based UI framework that runs on Linux (Wayland/X11) and macOS
- The GUI shell can be ported to Avalonia while the engine remains unchanged
- Expected effort: 3-6 months for an initial port

**Phase 3 — macOS native:**
- ARM64 (Apple Silicon) is a first-class target in .NET 8 and Avalonia
- No additional porting work beyond Phase 2
- Distribute via Homebrew cask or `.dmg`

**Platform capability matrix (target state by v2.0):**

| Feature | Windows | Linux | macOS |
|---------|---------|-------|-------|
| GUI | Yes | Yes (Avalonia) | Yes (Avalonia) |
| CLI | Yes | Yes | Yes |
| SSMS add-in | Yes | No | No |
| DPAPI credential store | Yes | No | No |
| Azure Key Vault store | Yes | Yes | Yes |
| Docker image | Yes (Windows) | Yes (Linux) | Via Linux container |
| Auto-update | Yes | Yes | Yes |

---

## References

- Redgate SQL Compare 16 Documentation: <https://documentation.red-gate.com/sc>
- SQL Compare Requirements: <https://documentation.red-gate.com/sc/getting-started/requirements>
- SQL Compare Licensing: <https://documentation.red-gate.com/sc/getting-started/licensing>
- SQL Server Version Support Matrix: <https://documentation.red-gate.com/xx/support-matrix/sql-server-versions>
- Silent Install Guide: <https://productsupport.red-gate.com/hc/en-us/articles/360007207454-Installing-from-the-msi-file-silent-install>
- SQL Compare Docker Image: <https://hub.docker.com/r/redgate/sqlcompare>
- Redgate Blog — SQL Compare v16 Announcement: <https://www.red-gate.com/blog/introducing-sql-compare-sql-data-compare-v16-more-future-ready-more-secure/>
- Azure DevOps Pipeline Tutorial (Redgate): <https://documentation.red-gate.com/rcc4/deploying-database-changes/example-ci-cd-pipelines/tutorial-implement-azure-devops-classic-pipelines-for-sql-server-with-a-self-hosted-agent>
- Forcing Encrypted Connections: <https://documentation.red-gate.com/sc/getting-more-from-sql-compare/forcing-sql-compare-and-sql-data-compare-to-use-an-encrypted-connection>
- SSMS Add-in Troubleshooting: <https://productsupport.red-gate.com/hc/en-us/articles/360003503274-SSMS-plug-ins-SQL-Prompt-SQL-Search-SQL-Source-Control-SQL-Test-are-missing-from-SSMS>
- Offline License Activation: <https://forum.red-gate.com/discussion/82720/license-activation-for-sql-compare-13-without-internet-access>
- Chocolatey SQL Toolbelt: <https://community.chocolatey.org/packages/SqlToolbelt>
