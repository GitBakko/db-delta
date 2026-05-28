# DbDelta — Backlog (task list for the next session)

**State at creation (2026-05-28):** `main` = `b09c234`, synced with origin, all CI
workflows green. **v0.17.0 released** (GitHub Release + `DbDelta-0.17.0-win-x64.msi`).
The entire v1.0-RC **code** backlog is done: M1–M13, #24 Kahn dependency resolver,
#25 DocFX site (live at <https://gitbakko.github.io/db-delta/>), WiX MSI installer,
and the self-contained verbose deploy script. 410 tests green.

Pick an item, then run it through the usual flow: brainstorm → spec →
writing-plans → subagent-driven execution → finishing. Collaboration rules:
terse Italian, default to the Recommended option, commit per phase, **push stays
manual**, parity > invariants.

---

## A. Non-code (need external input from the owner)

- [ ] **Code signing** — acquire an Authenticode code-signing certificate, then
      sign the MSI + bundled `.exe`s in `.github/workflows/release.yml` (e.g. a
      `signtool`/`AzureSignTool` step before the WiX build or on the produced MSI).
      Removes the Windows SmartScreen / "unknown publisher" prompt the unsigned
      MSI currently shows. **Blocked: needs a certificate.**
- [ ] **Public alpha announcement** — polish `README.md` (download link to the
      latest Release MSI + the DocFX site), draft the GitHub Release notes, and
      announce. Non-code; owner's call on channel/wording.

## B. Release decision

- [ ] **Cut v1.0.0 RC** — all milestones (M1–M13) + DocFX + MSI + verbose script
      are shipped. Decide whether to tag `v1.0.0-rc1` (the release workflow builds
      + smoke-tests + attaches the MSI on any `v*` tag). Likely gate this behind
      code signing + the announcement above.

## C. Optional / ongoing hardening

- [ ] **Expand Redgate parity scenarios** — the parity fixture
      (`tests/Fixtures/Parity/`) covers 12 scenarios; add more (e.g. computed
      columns referencing functions, filtered/columnstore indexes, check
      constraints across tables, schema-bound views, extended properties) and
      re-run the parity audit (`docs/parity/redgate-YYYY-MM-DD.md`). Note: Redgate
      **CLI is license-blocked** on this host (exit 35) — use the GUI for the
      Redgate side; DbDelta side via `dbdelta script`. Live instances:
      `192.168.3.243` (source) + `192.168.3.242` (target); sa password asked each
      session, never stored.
- [ ] **ScriptGenerator review polish** (low value, from code-review findings):
      remove the unreachable switch-arm fallthroughs in the `BuildOneX` helpers;
      make `PhaseLabel`'s `_ =>` a throw for symmetry with the create-validated
      arms; normalize `EmitUsers` (staged `body`) vs `EmitRoles` (inline
      `WriteBatch`) onto one pattern.
- [ ] **MSI size optimization** (optional) — the installer bundles two
      self-contained .NET runtimes (~94 MB). Publishing app + CLI into a shared
      runtime folder would roughly halve it. YAGNI unless size becomes a complaint.
- [ ] **Workflow action Node-20 deprecation** — GitHub flagged `actions/*@v4` etc.
      run on Node 20 (forced to Node 24 by 2026-06-02). Bump action versions when
      newer majors are available, across `ci.yml` / `docs.yml` / `release.yml`.

## D. v2 parking-lot (explicitly out of v1 scope — spec §6.3)

- [ ] Scripts-Folder / Snapshot / Source-Control (LibGit2Sharp) providers.
- [ ] Migration scripts (user-authored DDL overrides).
- [ ] Tier-3 object kinds: CLR Assembly, Full-text, XML schemas, Service Broker,
      Partition function/scheme, Filegroup.
- [ ] SSMS / VS extension; opt-in OpenTelemetry; auto-update channel; Linux +
      macOS CLI support.

---

## Pointers

- Specs + plans: `docs/superpowers/{specs,plans}/`.
- Parity audits: `docs/parity/`.
- CI: `.github/workflows/{ci.yml,docs.yml,release.yml}`. **CI gates hard on
  `dotnet format --verify-no-changes`** under the `global.json`-pinned SDK — keep
  every touched file formatted (run `dotnet format` before committing) or the
  windows-build job goes red.
- DocFX site source: `docfx/`; published by `docs.yml` on push to `main`.
- Installer: `installer/DbDelta.Installer.wixproj` + `Package.wxs` (WiX v5);
  `installer/staging/` + `*.msi` are git-ignored; `installer/` is NOT in the `.sln`.
