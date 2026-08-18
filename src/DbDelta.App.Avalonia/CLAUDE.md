# DbDelta.App.Avalonia — UI / UX Invariable Rules

These are **non-negotiable** styling rules. Apply on every UI change.

1. **No naked buttons.** Every button MUST have a visible background fill OR a
   visible border. Buttons that only reveal on hover are banned.

   **Naming, so nobody "fixes" 47 compliant buttons:** the XAML classes are
   still called `ghost`, `ghost-violet`, `ghost-emerald`, `ghost-amber`,
   `ghost-cobalt`, `ghost-crimson` — the name was kept to avoid touching every
   call site — but they are NOT transparent. `.ghost` **is** the filled neutral
   (`Styles/AppStyles.axaml:91-105`) and the coloured variants carry a soft
   accent fill at rest. Do not rename them and do not "correct" them. Pick
   the fill colour by semantic meaning of the action:
   - `primary` (cobalt) — confirmation / commit / "go forward" actions (OK,
     Connetti, Allinea destinazione)
   - `secondary` (violet) — informational / discovery actions (Scansiona)
   - `success` (emerald) — read-only success path (Genera script, Connetti
     test-only)
   - `danger` (crimson) — destructive / irreversible (Esegui, Drop)
   - `neutral` (raised-grey filled) — secondary / utility actions (Salva,
     Carica, Annulla, Modifica, navigation icons)
   The SEMANTIC brushes — `PrimaryBrush`, `SecondaryBrush`, `SuccessBrush` /
   `SuccessSoftBrush`, `DangerBrush`, `BgRaisedBrush`, `FgOnAccentBrush` — live
   in `Styles/Themes.axaml`, one set per theme. `Styles/Tokens.axaml` holds only
   the raw ramps (Cobalt, Violet, Emerald, Crimson, Amber, Slate). Always bind
   the semantic brush, never the ramp. Note the two vocabularies do not match:
   the ramp is Emerald, the semantic brush is Success.

2. **Uniform monoline height.** All single-line interactive controls in the
   same surface share the SAME height. Default for app shell + dialogs:
   **32 px of `MinHeight`**, set once in `Styles/AppStyles.axaml:11-21`. There is
   no MaxHeight anywhere — the rule used to claim "Min + Max" and only Min
   exists. Covered by that style: `Button`, `TextBox`, `ComboBox`,
   `AutoCompleteBox`. Includes:
   - `Button`
   - `TextBox`
   - `AutoCompleteBox`
   - `ComboBox`
   - `CheckBox` — **no shared style exists yet** (see P4 in `docs/BACKLOG.md`);
     until it does, set the height by hand
   - Declared exception: `Button.swap` is a 36x36 round icon
     (`Styles/AppStyles.axaml:122-124`)
   The shared height makes rows of mixed controls (Cerca + Raggruppa +
   Tema, panel forms, footer action bars) read as a single elegant strip.
   When introducing a new control, set `Height="32"` or use a shared style.

3. **DRY — Don't Repeat Yourself, ALWAYS.** This is the single binding rule
   for every future change. Any UI pattern (XAML markup, code-behind logic,
   view-model boilerplate) that would be duplicated more than **once**
   MUST be extracted before the second copy ships:
   - Repeated XAML: extract to a `UserControl` under `Views/Controls/` (or
     a `Style` in `AppStyles.axaml` when it is purely visual).
   - Repeated behaviour: extract to a method, base class, or `behavior`.
   - Repeated view-model logic: extract to a shared service or partial
     base view-model.
   - When tempted to copy-paste a snippet "just this once" — STOP, create
     the abstraction first, then use it from both call sites.
   **Known open violation, tracked not ignored:** the icon+label content of the
   topbar and action-bar buttons (`<StackPanel Horizontal><Path/><TextBlock/>`)
   is inline in 8 places in `Views/MainWindow.axaml` — the eighth was added on
   2026-08-18 with «Salva report». Each `Path` carries a different `Data`, so
   extraction is not free. It is a P4 item in `docs/BACKLOG.md`; until it is
   decided, do not add a ninth without saying so there.

   Violation example we've already paid for: the in-button "busy" markup
   (spinner + label) was copy-pasted into 4 buttons; one was missed in a
   refactor and shipped in an inconsistent state. The lesson: a reusable
   `LoadingContent` UserControl is mandatory — see
   `Views/Controls/LoadingContent.axaml`. Apply the same principle to
   every future repeated pattern. No spaghetti programming.
