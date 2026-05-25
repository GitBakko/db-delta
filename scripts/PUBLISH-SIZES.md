# Single-file publish — binary sizes

Spec §6.1 perf budget targets a `<80 MB` self-contained `.exe` (with
a stretch goal of `<50 MB`). M13-RC.2 measures both flavours against
that budget and locks the v1.0 RC distribution mode.

## 2026-05-25 — commit `d32e005` baseline

Build matrix: `dotnet 10`, `win-x64`, `Release`, `PublishSingleFile=true`.

| Target | Mode | Size | vs §6.1 budget |
|--------|------|-----:|---------------:|
| `dbdelta.exe` (CLI) | Self-contained | **89.1 MB** | over (+11%) |
| `DbDelta.App.exe` (Avalonia GUI) | Self-contained | **97.1 MB** | over (+21%) |
| `dbdelta.exe` (CLI) | Framework-dependent | **20 MB** | well under |
| `DbDelta.App.exe` (Avalonia GUI) | Framework-dependent | **28 MB** | well under |

## Trim experiment

Enabling `-p:PublishTrimmed=true -p:TrimMode=partial` against the
self-contained build fails the analyzer gate. Eight `IL2026`
warnings (TreatWarningsAsErrors) come from reflection-heavy code:

- `System.Text.Json.JsonSerializer.Serialize/Deserialize` in
  `JsonRecentProjectsStore`, `JsonConnectionStore`, and
  `JsonReportGenerator`.
- `System.Xml.Serialization.XmlSerializer` in `XmlProjectStore`.

Getting the self-contained build under 80 MB requires:

1. Converting all `System.Text.Json` call-sites to source-generated
   `JsonSerializerContext`s (or `[JsonSerializable]` attributes).
2. Replacing `XmlSerializer` with a hand-rolled `XmlReader/XmlWriter`
   round-trip for the `.dbd` project file, or annotating the
   `[XmlRoot]` types with `[DynamicallyAccessedMembers]`.
3. Optionally pursuing `<PublishAot>true</PublishAot>` (spec §8 open
   Q #2) for an even smaller, AOT-compiled CLI.

That refactor is post-RC. For v1.0 alpha the distribution mode is
**framework-dependent single-file**:

- CLI binaries ship as `dbdelta.exe` (20 MB) + `runtimeconfig.json`.
  Target machines need .NET 10 installed (`dotnet-install --channel 10.0`).
- GUI binaries ship as `DbDelta.App.exe` (28 MB) plus the same
  runtime dependency. The WiX v5 installer (parked task #26) will
  bundle the runtime when authored.

## Replay

```bash
dotnet publish src/DbDelta.Cli/DbDelta.Cli.csproj \
  -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o publish/cli

dotnet publish src/DbDelta.App.Avalonia/DbDelta.App.Avalonia.csproj \
  -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o publish/app
```

Binaries live under `publish/cli` and `publish/app` — both directories
are git-ignored.
