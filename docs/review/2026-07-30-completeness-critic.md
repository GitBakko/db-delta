# Critico di completezza — cosa la review a 8 dimensioni ha mancato

8 finding nuovi trovati leggendo i blind spot, correzioni di ranking, e le affermazioni
di sicurezza che nessuno aveva verificato leggendo il file definitivo.

## New findings

### 1. `connections.json` load throws on any non-JSON IO error → app dies at startup with no window, no message, no log
**`src/DbDelta.Persistence/Json/JsonConnectionStore.cs:88-105`** · severity **high** · effort **S**

```csharp
byte[] bytes = await File.ReadAllBytesAsync(_filePath, ct)...   // line 88
...
catch (JsonException)   // line 101 — ONLY JsonException
```
`MoveAside` (line 108-119) catches only `IOException`, and it is called *from inside* that `catch (JsonException)` block (line 103).

Reached from `src/DbDelta.App.Avalonia/App.axaml.cs:42` — `await connections.LoadAsync(CancellationToken.None)` inside `desktop.MainWindow.Opened += async (_, _) =>`, an `async void` handler with no try/catch, and there is no global handler anywhere (confirmed: `grep -rn 'UnhandledException|UnobservedTaskException|CurrentDomain' src/` → zero hits).

**Failure scenario:** `%LOCALAPPDATA%\DbDelta\connections.json` is held open by OneDrive/Dropbox sync, or the roaming profile is read-only (managed corporate image). `File.ReadAllBytesAsync` throws `IOException`/`UnauthorizedAccessException` → escapes the async void → dispatcher rethrow → **the process exits after the main window paints but before the setup dialog appears**. No dialog, no log, no crash file. The user sees DbDelta flash and vanish, every launch, forever, with no way to diagnose. Same death if the file is corrupt JSON *and* the profile is read-only (`MoveAside`'s `File.Move` throws `UnauthorizedAccessException`, uncaught).

**This directly refutes an explicit reviewer non-report.** The app-ui-robustness coverage note says: *"JsonConnectionStore.LoadAsync (the latter does catch JsonException/IOException, so the App-startup connections.LoadAsync path is safe — I did not report it)."* It does not catch `IOException`. Line 101 is the only catch in the method.

Fix: `catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)` on both stores, and wrap `MoveAside`'s `File.Move` in the same.

---

### 2. The server picker is populated from unauthenticated UDP broadcast replies — a LAN attacker chooses the hostname the app then auto-connects to
**`src/DbDelta.Persistence/Sql/SqlServerDiscovery.cs:197-231`** · severity **high** · effort **M**

Nobody covered the discovery *data path*. `EnumerateServersAsync` broadcasts `0x02` to UDP/1434 (global broadcast, every NIC's directed broadcast, and loopback — lines 69-79) and then accepts **any** reply that arrives at its ephemeral port:

```csharp
string payload = Encoding.ASCII.GetString(buffer, 3, buffer.Length - 3);   // 203
...
if (string.Equals(key, "ServerName", ...)) { server = value; }             // 214-216
...
seen[entry] = sourceIp;                                                     // 125
```
The only validation is `buffer[0] != 0x05` (line 199). No length cap, no character filter, no correlation to the request, no check that the responder is the host it names.

That list becomes `DiscoveredServer.Name` → seeded into the picker (`App.axaml.cs:59-71` auto-fires `ScanForCommand` the moment the setup dialog opens, before the user touches anything) → clicking an entry sets `ProjectEndpointPanelViewModel.ServerName`.

**Failure scenario:** an attacker on the same VLAN (or anything that can see the broadcast) answers with `ServerName;PROD-SQL01;InstanceName;MSSQLSERVER;;` from their own IP. The entry renders identically to the real `PROD-SQL01`. The operator — who has just had DPAPI autofill their production `sa` credentials into the boxes for the *previous* server — clicks it. `OnServerNameChanged` does **not** clear Password/UserName (`ProjectEndpointPanelViewModel.cs:107-123`), `IsAutoConnectEligible` only checks that the three fields are non-empty (`:438-443`), and 450 ms later `LoadDatabasesAsync` opens a connection to the attacker's host with `Encrypt=False;TrustServerCertificate=True` (`:447-461`). Production `sa` credentials, delivered with no click and no certificate validation. On success, `TryPersistCredentialsAsync` then writes them into Windows Credential Manager under the *attacker's* server key (`:646-676`, `RememberCredentials` having been force-set true at `:628`).

This is the same code path as the confirmed `credential-autofill-sends-previous-server-password-to-new-host` finding, but the trigger is a remote unauthenticated packet rather than a typo — see Ranking corrections.

Fix: the clear-password-on-server-change fix already proposed for the autofill finding closes the credential half. Independently: cap the payload, reject non-hostname characters, and mark scan results visually distinct from `Usati di recente` entries (the `Section` field already exists for this).

---

### 3. Two permission rows can produce the same `ObjectIdentity` → `ToDictionary` throws → the whole app dies on Aggiorna
**`src/DbDelta.Core/ObjectModel/Permission.cs:26-30`** + **`src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:477-479`** · severity **medium** · effort **S**

`Permission.DiffKey` (line 23-24) includes `ClassDesc`. `Permission.Identity` (line 26-30) **drops it**:

```csharp
public ObjectIdentity Identity => new(
    SchemaName: ObjectSchema ?? string.Empty,
    ObjectName: $"{State} {Action} TO [{GranteeName}] ON {ObjectName ?? "DATABASE"}" + ...,
    Kind: "Permission");
```
`ComparePermissions` is defensively written — `GroupBy(p => p.DiffKey).ToDictionary(...)` (ComparisonEngine.cs:113-116) — so it emits one pair *per DiffKey*. `RebuildRows` is not:

```csharp
Dictionary<(string Kind, string Schema, string Name), DifferencePair> pairMap =
    AppState.LastComparisonRaw.Differences.ToDictionary(
        p => (p.Identity.Kind, p.Identity.SchemaName, p.Identity.ObjectName));   // 477-479
```

**Reachability, established:** `PermissionReader`'s `LEFT JOIN sys.objects` only fires for `class_desc = 'OBJECT_OR_COLUMN'` (PermissionReader.cs:26-28), and the `sys.schemas` join needs `obj.schema_id` (line 29-31). Under SQL Server metadata visibility, an OBJECT-class permission whose target object the *comparison login* cannot see returns `obj.name = NULL` **and** `sch.name = NULL` → Identity = `("", "Grant SELECT TO [app] ON DATABASE", "Permission")` — byte-identical to the Identity of a genuine database-scope `GRANT SELECT TO [app]`. Two distinct DiffKeys, one Identity.

**Failure scenario:** target DB has `GRANT SELECT TO [app_reader]` (database scope — the normal pattern for read-only accounts) plus `GRANT SELECT ON dbo.Ledger TO [app_reader]`, and the comparison login is least-privilege so `dbo.Ledger` is invisible in `sys.objects` (the exact precondition of the confirmed critical `metadata-visibility-turns-into-drop-table`). `ToDictionary` throws `ArgumentException: An item with the same key has already been added` → out of `RebuildRows` → out of the `AppStateViewModel.LastComparison` PropertyChanged handler (MainWindowViewModel.cs:45-55) → out of the `AsyncRelayCommand`, which rethrows onto the UI context → no global handler → **process death mid-comparison, with no message**.

The diff-engine reviewer filed the label side of this as a *low improvement* (`schema-permission-label`) and wrote "MainWindowViewModel.cs:478 uses ToDictionary, which throws on a duplicate key" without establishing that a duplicate is reachable. It is.

Fix: put `ClassDesc` into `Identity.ObjectName`, and change line 478 to `GroupBy(...).ToDictionary(g => g.Key, g => g.First())` to mirror what the engine already does.

---

### 4. A third copy of the broken password regex — this one writes to disk on every successful comparison
**`src/DbDelta.App.Avalonia/ViewModels/ConnectionStoreViewModel.cs:174-178`** · severity **medium** (latent) · effort **S**

```csharp
private static string ReplacePassword(string original, string replacement) =>
    Regex.Replace(original, @"(?i)(password|pwd)\s*=\s*[^;]+", $"$1={replacement}");
```
Called at line 66 to build `ConnectionStringTemplate`, which `store.UpsertAsync` (line 84) persists **in cleartext** to `%LOCALAPPDATA%\DbDelta\connections.json`. And it runs on the main path, unconditionally: `AppStateViewModel.cs:208-212`

```csharp
if (Connections is not null)
{
    await Connections.AutosaveAsync(srcCs, ct)...;
    await Connections.AutosaveAsync(tgtCs, ct)...;
}
```

The review found this exact `[^;]+` bug in `ConnectionStringRedactor.cs:13` and traced it only to **on-screen display**. This is the same defect writing to **persistent storage**, defeating the DPAPI store that exists precisely so the password never lands in a file. With a quoted password (`Password="a;b"` — accepted by `SqlConnectionStringBuilder`, so it survives the guard at line 49-51) the regex matches `Password="a` and the tail `b"` is persisted verbatim, plus the template is corrupted so `MaterialiseAsync` (line 102) yields an unparseable string.

**Honest scope:** on the *shipped* path this is currently unreachable, because the four hand-rolled builders never quote and an unquoted `;` password makes `new SqlConnectionStringBuilder(...)` throw at line 50 (and earlier, at AppStateViewModel.cs:160). It becomes live the moment any paste-a-connection-string path exists — and the debug code at AppStateViewModel.cs:155-157 ("markdown \*\* that survived the paste") shows that path was in play during development. Report it because the single-`SqlConnectionStringBuilder`-factory fix already proposed for the other two copies must cover this one, or the on-disk copy survives the cleanup.

---

### 5. `UserReader`'s server-principal join makes every user report Different under a least-privilege login
**`src/DbDelta.Providers.LiveDb/Readers/UserReader.cs:25`** · severity **medium** · effort **S**

```sql
LEFT JOIN sys.server_principals AS sp ON sp.sid = p.sid
```
`sys.server_principals` is server-scoped and metadata-visibility-filtered: a login without `VIEW ANY DEFINITION` sees only its own row. So `sp.name` → NULL for every other user, and `DatabaseUser.LoginName` (DatabaseUser.cs:20) is null for all of them.

**Failure scenario:** source read as `db_owner`/sysadmin (LoginName populated), target read with the read-only comparison login that the SICURO requirement encourages (LoginName NULL). `UsersEqual` compares `LoginName` (ComparisonEngine.cs:59-62), so **every user in the database reports Different**, and the deploy emits `CREATE USER … FOR LOGIN [x]` from the source side for users that already exist correctly on the target. The `server-version-and-edition-gating` improvement flagged this join as an *Azure* concern and explicitly said it was unverified; it fires on any on-prem instance with an under-privileged read login, no Azure involved.

Fix: fall back to `p.authentication_type_desc` when `sp.name` is null, or treat a null `LoginName` on either side as "unknown, not different".

---

### 6. Unbounded `stackalloc` in the project-name sanitiser
**`src/DbDelta.Persistence/Json/ProjectsFolder.cs:42`** · severity **low** · effort **S**

```csharp
Span<char> buffer = stackalloc char[name.Length];
```
`name` is the project name typed/pasted into `SaveProjectDialog`. A pasted ~500 KB string → 1 MB stack allocation → `StackOverflowException`, which is **uncatchable in .NET** — immediate process termination, no dialog, no log, unsaved selections lost. Also: `Path.GetInvalidFileNameChars()` does not include `.`, and reserved device names are not filtered, so a project named `NUL` resolves to `NUL.dbd` (the null device) and the save silently discards. Fix: cap at 128 chars before the `stackalloc` and reject reserved names.

---

### 7. Nobody reviewed `installer/` or the release channel
**`installer/Package.wxs`**, **`installer/DbDelta.Installer.wixproj`**, **`.github/workflows/release.yml`** · severity **low** · effort **S**

The tests-cicd coverage note states outright: *"I did not review the Avalonia views/XAML, the installer wixproj."* No other dimension mentions `installer/` either. I read both. The MSI itself is sound — `Scope="perMachine"`, `ProgramFiles64Folder` (ACL-protected, so the `Part="last"` system-PATH entry at Package.wxs:48-53 is not a DLL-planting vector), `MajorUpgrade` with a downgrade guard, and a real smoke install/uninstall gate in CI (release.yml:47-64).

The gap is **distribution integrity**: `softprops/action-gh-release@v3` (release.yml:66-70) attaches the bare `.msi` and nothing else — no `.sha256`, no provenance attestation, and (already known to the owner) no Authenticode signature. A user downloading DbDelta has literally no way to verify what they got, for a tool that runs DDL against production. Publishing a checksum file is three lines in the workflow and is the free half of the signing story that is currently blocked on a cert.

---

### 8. MRU store lacks the file-mode hardening and version gate its sibling has
**`src/DbDelta.Persistence/Json/JsonRecentProjectsStore.cs:106-120`, `:88-104`** · severity **low** · effort **S**

`JsonConnectionStore.WriteAtomicAsync` sets `options.UnixCreateMode = UserRead | UserWrite` (0600) on non-Windows (JsonConnectionStore.cs:130-134) and gates `SchemaVersion > CurrentSchemaVersion` on read (line 94-98). The MRU store does neither — world-readable project paths on Linux/macOS, and a future-schema file is silently parsed as v1 instead of being moved aside. Same class, same file layout, one copy hardened and one not — a DRY violation with a security delta.

---

## Ranking corrections

**Upgrade — `credential-autofill-sends-previous-server-password-to-new-host`: medium → high.** The owner note justifies medium with *"a typo, or a colleague's dev box on the same LAN"*. New finding #2 shows the same sink is fed by unauthenticated LAN UDP: an attacker picks the hostname, the scan auto-fires on dialog open, and auto-connect delivers the credentials 450 ms later with `TrustServerCertificate=True`. That is remote-triggerable credential exfiltration, not a user typo.

**Upgrade — `schema-permission-label`: low improvement → medium finding.** It was filed as a cosmetic label bug that happened to mention the `ToDictionary` risk. New finding #3 establishes the duplicate is reachable, and the outcome is process death mid-comparison.

**Correct the scenario — `execute-runs-unseen-sql-no-preview-no-cancel-no-undo`.** Its scenario opens *"User ticks 'seleziona tutto' on a PROD→PROD-copy comparison"*. **There is no select-all affordance.** `grep -rni 'SelectAll|Seleziona|IsSelected = true|ToggleAll' src/` returns only unrelated Italian UI strings and one `TextBox.SelectAll()` in `SaveProjectDialog.axaml.cs:22`. Rows default to unselected and must be ticked one by one (`DifferenceRowViewModel.cs:32`). The finding stands on its merits; the multiplier in its scenario does not exist. Conversely this **confirms** the owner notes that correctly narrowed `owner-table-mappings-never-applied` and the case-sensitivity finding on the same grounds.

**Downgrade the practical weight of `unencrypted-untrusted-tds-by-default`'s sibling claim, but note the interaction.** `Encrypt=False` alone is medium as filed; combined with #2 the `TrustServerCertificate=True` default is what turns a spoofed picker entry into a completed login. Fix the default in the same change as #2, not separately.

**~20 of ~60 findings are duplicates, which will distort any triage.** Same defect reported under different ids by different dimensions:

| Defect | Duplicate ids |
|---|---|
| Schemas never compared / no `CREATE SCHEMA` | `no-create-schema-emitted-ever`, `schemas-never-compared`, `schemas-read-but-never-compared-or-created`, `missing-create-schema-emission` (**×4**) |
| Non-rowstore indexes invisible | `index-and-table-roundtrip-incompleteness`, `non-btree-indexes-invisible`, `index-reader-skips-non-rowstore-and-view-indexes`, `non-rowstore-indexes-invisible` (**×4**) |
| ComparisonOptions / ProjectOptions dead | `comparison-options-are-decorative`, `comparison-options-mostly-dead`, `project-comparison-options-ignored`, `project-options-never-applied` (**×4**) |
| Rebuild drops indexes/triggers/FKs | `rebuild-drops-indexes-and-then-fails-on-index-delta`, `rebuild-silently-drops-triggers`, `identity-rebuild-drops-indexes-and-triggers` (**×3**) |
| Deploy-script header leaks password | `deploy-script-header-leaks-plaintext-password` (**×2**, sql-injection + app-ui) |
| 60 s command timeout | `command-timeout-hardcoded-60s`, `command-timeout-60s-hardcoded` (**×2**) |
| App loses dependency edges | `gui-deploy-script-has-no-dependency-ordering`, `app-script-loses-dependency-edges` (**×2**) |
| System-named constraint churn | `system-named-constraints-false-positive`, `system-named-constraint-name-churn` (**×2**) |
| Masking invisible | `masked-columns-invisible`, `dynamic-data-masking-invisible` (**×2**) |
| `LineDiffer` quadratic | `linediffer-quadratic-memory` (**×2**) |
| `apply` has no transaction | `cli-apply-runs-arbitrary-script-with-no-transaction`, `apply-has-no-transaction` (**×2**) |
| `script` discards `opts` | `script-cmd-discards-computed-options`, `script-command-dead-options-variable` (**×2**) |
| INCLUDE order-sensitive | `included-columns-order-sensitive`, `included-column-order-nondeterministic` (**×2**) |
| Sequence bigint overflow | `sequence-cast-bigint-overflow`, `sequence-reader-bigint-cast-overflow` (**×2**) |
| Rollback-script improvement | 4 near-identical entries across dimensions |

Deduping first would cut the list by a third and make the four genuine criticals visible.

---

## Safety claims I settled (asserted by no one, or asserted without reading the definitive file)

**XXE / DTD in the XML project store — NOT vulnerable.** The prompt flagged this; no reviewer addressed it. `XmlProjectStore.LoadAsync:30` uses `XDocument.Parse(xml)`, whose internal `GetXmlReaderSettings` hardcodes `DtdProcessing = Prohibit` and `MaxCharactersFromEntities = 10_000_000` — external entities and billion-laughs are both blocked, and a `<!DOCTYPE` makes it throw. The V1 legacy path (`:337-344`) runs `XmlSerializer` over `root.CreateReader()`, an in-memory reader over already-parsed content — no second parse, no DTD surface. The only consequence is the `XmlException` on the uncaught-throw path already filed as `projectsetupdialog-load-crash`.

**The deploy envelope's failure gate — correct as written.** `DeploymentScriptWriter.cs:25/38/47` emits `IF @@ERROR <> 0 SET NOEXEC ON` and `WriteVerdict:49-59` relies on `DECLARE` compiling but not executing under NOEXEC so `@Success` stays NULL → `ELSE` → `IF @@TRANCOUNT > 0 ROLLBACK`. That is the exact Redgate idiom and it is sound. `SET NOEXEC ON` does not leak into other sessions: `SqlExecutor` uses `await using SqlConnection` (`:75`), so the connection returns to the pool and `sp_reset_connection` clears SET options and rolls back any open transaction. The undo-rollback reviewer's transaction conclusion holds under scrutiny.

**Culture-sensitive string handling — the two filed findings are the complete set.** `grep 'ToUpper()|ToLower()|ToUpperInvariant|ToLowerInvariant'` over `src/` returns exactly three sites, all Invariant: `ModuleHeader.cs:57`, `ProjectEndpointPanelViewModel.cs:590`, `UserDefinedTypeScriptEmitter.cs:39`. Every `Contains`/`StartsWith`/`Replace` on identifiers passes an explicit `StringComparison`. `TextFormatter.cs:21` and `DeployScriptBuilder.cs:35` are the only culture leaks, both already filed as low.

**HTML report — no XSS, no leak.** Read `HtmlReportGenerator.cs` end to end (217 lines). Only `SchemaName`, `ObjectName`, kind label and status label are emitted, all through `WebUtility.HtmlEncode` (`:156/175/176/178`); no object bodies, no DDL, no endpoint/connection string anywhere. All counts use `CultureInfo.InvariantCulture`. Clean.

**`Mapper.cs` (never opened by anyone) — clean.** 36 lines, pure projection, `ArgumentNullException.ThrowIfNull`, no reflection, no serialization surface.

**`SerilogBootstrap.cs` — dead, and would throw if used.** `grep` shows no caller in `src/`. If wired, `Directory.CreateDirectory(Path.GetDirectoryName(logFile)!)` (`:20`) throws `ArgumentException` for a bare filename like `dbdelta.log` (`GetDirectoryName` returns `""`, not null). No PII/schema-leak risk today because nothing logs.

---

## Genuinely-uncovered areas that remain

- **`src/DbDelta.App.Avalonia/Styles/*.axaml`** (Tokens, Themes, Templates, AppStyles) — four files, zero coverage from any dimension. Out of my scope to judge visually, but they are the enforcement point for the CLAUDE.md 32 px / no-naked-buttons invariants and no reviewer verified them.
- **`SynonymReader.SplitBaseObjectName`** (`:43-69`) — a hand-rolled bracket parser with no `]]` un-escaping, so a synonym targeting `[dbo].[Ord]]ers]` splits to schema `dbo`, object `Ord`. Harmless for emission (`baseRaw` is kept and used by the emitter) but wrong in the diff viewer's segment display. Not worth a finding; noting it because it is the only hand-rolled identifier parser in the readers and it shares the missing-`]]`-escape root cause with the confirmed `identifier-bracket-injection-no-escape`.
- **Concurrency between the two DB scans** — I checked and there is none to review: `AppStateViewModel.cs:184-196` awaits `src.LoadAsync` then `tgt.LoadAsync` strictly sequentially, so the source scan holds no lock while the target scan runs and there is no shared mutable state. No finding, but no reviewer stated this either.
- **10k-object memory behaviour** — still unmeasured, as the `scale-10k-objects` improvement admits. I did not measure it either; the `OnSearchTextChanged` → `_rowsView.Refresh()` per keystroke (`MainWindowViewModel.cs:461`) remains the untested hot path.