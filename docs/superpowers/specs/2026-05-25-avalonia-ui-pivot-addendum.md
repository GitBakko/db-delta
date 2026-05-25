# Addendum — DbDelta GUI pivot to Avalonia 11

> **Date:** 2026-05-25
> **Author:** Stefano Brunelli + Claude
> **Status:** Locked
> **Supersedes:** `docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md` §2.1 (`DbDelta.App` row) + §2.3 (Blazor / WebView2 rows) + §3.1 (Blazor App actor) + §6.4 row #1 (Blazor / WebView2 risk).

The original v1 design spec named **Blazor Hybrid + WebView2** as the GUI host. During M10 the team migrated to **Avalonia 11** + Fluent theme + CommunityToolkit.Mvvm. This addendum records the reasoning and locks the new stack as the v1 contract.

---

## 1. Decision

The v1 GUI is delivered as `DbDelta.App.Avalonia` (TFM `net10.0`, `OutputType=WinExe`), referencing:

- `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent`
- `Avalonia.Controls.DataGrid`
- `CommunityToolkit.Mvvm`
- `AvaloniaUI.DiagnosticsSupport` (Debug only)

References the same Core / Shared / Providers.LiveDb / Persistence projects as the spec called for. The hexagonal architecture and `NetArchTest.Rules` boundary tests are unchanged.

## 2. Why we left Blazor Hybrid + WebView2

| Issue | Detail |
|------|--------|
| **External runtime dependency** | WebView2 requires the Microsoft Edge WebView2 Runtime on the target machine. Distributing a single self-contained `.exe` (§6.1 perf budget) gets harder once that runtime has to be bootstrapped per machine. |
| **DOM-level diff viewer performance** | The dual-pane SQL diff viewer with synced scroll + minimap (M10 round-6) needs deterministic frame timing. Hand-tuning DOM and CSS inside a Chromium webview added friction we did not budget for. |
| **DataGrid maturity** | `Avalonia.Controls.DataGrid` provides virtualization, grouping, and template columns out of the box — closer to a Redgate-style results grid than what a Razor `<table>` would give us without considerable JS scaffolding. |
| **Cross-platform optionality** | Spec §1.3 lists "Linux / macOS" as out of scope for v1, but Avalonia keeps that door open at zero design cost. Blazor Hybrid on macOS would have required a separate MAUI host. |
| **Toolchain coherence** | Compiled bindings, code-behind, and analyzer support around `.axaml` mirror the rest of the .NET 10 + C# 14 toolchain (no JS interop, no two-language debug session). |

## 3. What the spec rows now mean

### §2.1 Source tree layout

The `DbDelta.App/` row should be read as:

```
src/DbDelta.App.Avalonia/        # net10.0, Avalonia 11 host
├─ App.axaml(.cs)                # Avalonia application bootstrap
├─ Program.cs                    # WinExe entry point
├─ Styles/                       # Tokens.axaml, AppStyles.axaml (semantic brushes + 32 px monoline)
├─ Views/                        # Top-level windows + dialogs (.axaml)
│  └─ Controls/                  # Reusable UserControls (LoadingContent, …)
├─ ViewModels/                   # CommunityToolkit.Mvvm partials
└─ Assets/                       # app-icon.png
```

### §2.3 Dependencies

The "Microsoft.AspNetCore.Components.WebView.WindowsForms" and "Microsoft.Web.WebView2" rows are replaced by:

| Library | Use |
|---------|-----|
| `Avalonia` + `Avalonia.Desktop` | XAML-based desktop UI |
| `Avalonia.Themes.Fluent` | Fluent design tokens base |
| `Avalonia.Controls.DataGrid` | Results grid with virtualization |
| `CommunityToolkit.Mvvm` | `ObservableObject`, `RelayCommand`, source-generated INPC |

### §3.1 Happy path sequence

Read the `App` actor as the Avalonia main-thread + view-model layer. The data flow (`App → Engine → SrcA/SrcB → Sql`) is unchanged.

### §5.2 Test catalogue

The `App.ComponentTests` row backed by **bUnit** is replaced by `DbDelta.App.HeadlessTests` running against `Avalonia.Headless` — same coverage goals (view-model state transitions, accessibility), different framework.

### §6.4 Risks

Row #1 ("Blazor Hybrid + WebView2 instability") is closed. Avalonia adds its own modest risk profile around per-OS rendering quirks; the headless test suite plus manual Windows-first verification covers v1.

## 4. Out-of-scope follow-ups

- Wiring the Avalonia GUI to a macOS / Linux `dotnet publish` once Tier-3 platform support is on the roadmap (currently v2 parking lot per §6.3).
- Adopting `Avalonia.WebView` if a future feature genuinely needs HTML rendering (none in v1).

## 5. Locked invariants (unchanged from main spec)

- Core stays I/O-free; NetArchTest gates remain in place.
- 32 px monoline height + semantic-coloured buttons + DRY rule (`CLAUDE.md` UI rules 1–3).
- Italian terminology — "Carica" not "Apri", "Tabelle / Viste / Procedure / ..." kind labels.
- Hexagonal dependency direction (App → Core / Shared / Providers / Persistence) unchanged.
