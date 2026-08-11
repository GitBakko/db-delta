# DbDelta.App.Avalonia — UI / UX Invariable Rules

These are **non-negotiable** styling rules. Apply on every UI change.

1. **No naked buttons.** Every button MUST have a visible background fill OR a
   visible border. "Ghost" buttons that only reveal on hover are banned. Pick
   the fill colour by semantic meaning of the action:
   - `primary` (cobalt) — confirmation / commit / "go forward" actions (OK,
     Connetti, Allinea destinazione)
   - `secondary` (violet) — informational / discovery actions (Scansiona)
   - `success` (emerald) — read-only success path (Genera script, Connetti
     test-only)
   - `danger` (crimson) — destructive / irreversible (Esegui, Drop)
   - `neutral` (raised-grey filled) — secondary / utility actions (Salva,
     Carica, Annulla, Modifica, navigation icons)
   Refer to the design-system brushes in `Styles/Tokens.axaml` (Primary,
   Secondary, Emerald, Danger, BgRaised ramps).

2. **Uniform monoline height.** All single-line interactive controls in the
   same surface share the SAME height. Default for app shell + dialogs:
   **32 px** (Min + Max). Includes:
   - `Button`
   - `TextBox`
   - `AutoCompleteBox`
   - `ComboBox`
   - `CheckBox` (visual height; checkbox itself is 16 but row height is 32)
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
   Violation example we've already paid for: the in-button "busy" markup
   (spinner + label) was copy-pasted into 4 buttons; one was missed in a
   refactor and shipped in an inconsistent state. The lesson: a reusable
   `LoadingContent` UserControl is mandatory — see
   `Views/Controls/LoadingContent.axaml`. Apply the same principle to
   every future repeated pattern. No spaghetti programming.
