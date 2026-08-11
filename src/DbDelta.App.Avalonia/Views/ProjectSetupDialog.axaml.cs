using Avalonia.Controls;
using Avalonia.Interactivity;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;

namespace DbDelta.App.Views;

/// <summary>
/// Code-behind for the "Nuovo progetto" setup dialog.
/// Returns a <see cref="DbDeltaProject"/> on OK / Save-as,
/// or <see langword="null"/> when the user cancels.
/// </summary>
public partial class ProjectSetupDialog : Window
{
    public ProjectSetupDialog()
    {
        InitializeComponent();

        // Fire the shared network scan as soon as the dialog is up, so the
        // server drop-down fills itself. It lives here rather than at each call
        // site because it was wired into the startup path alone: "Nuovo",
        // "Modifica" and load-from-MRU all opened with an empty picker.
        Opened += (_, _) =>
        {
            if (DataContext is ProjectSetupViewModel vm
                && vm.ScanForCommand.CanExecute(null))
            {
                _ = vm.ScanForCommand.ExecuteAsync(null);
            }
        };
    }

    // ── Database chevron clicks ───────────────────────────────────────────────

    private void OnSrcDatabaseDropToggleClick(object? sender, RoutedEventArgs e)
        => ToggleDropdown("SrcDatabaseBox");

    private void OnTgtDatabaseDropToggleClick(object? sender, RoutedEventArgs e)
        => ToggleDropdown("TgtDatabaseBox");

    private void ToggleDropdown(string boxName)
    {
        AutoCompleteBox? box = this.FindControl<AutoCompleteBox>(boxName);
        if (box is null) { return; }
        box.Focus();
        box.IsDropDownOpen = !box.IsDropDownOpen;
    }

    // ── Action bar ────────────────────────────────────────────────────────────

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>Source connection string from the last successful OK, including
    /// the live password the user typed. Read by <c>App</c> to seed
    /// <c>AppState.SourceConnectionString</c> without re-prompting.</summary>
    public string? LastSourceConnectionString { get; private set; }

    /// <summary>Target connection string from the last successful OK.</summary>
    public string? LastTargetConnectionString { get; private set; }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectSetupViewModel vm)
        {
            LastSourceConnectionString = vm.BuildSourceConnectionString();
            LastTargetConnectionString = vm.BuildTargetConnectionString();
            Close(vm.Build());
        }
    }

    // Round-9: a single "Salva" button drives the name-only save flow —
    // "Salva con nome" was redundant once paths became implicit.
    private void OnSaveClick(object? sender, RoutedEventArgs e) => _ = SaveAsAsync();

    private async Task SaveAsAsync()
    {
        if (DataContext is not ProjectSetupViewModel vm)
        {
            return;
        }

        // Round-8 UX: ditch the OS save-file picker; the modal's Salva /
        // Salva con nome flow routes through SaveProjectDialog (name-only)
        // so projects land under %LOCALAPPDATA%\DbDelta\Projects\ and show
        // up in the MRU automatically.
        SaveProjectDialog dialog = new();
        dialog.SetInitialName(vm.ProjectName);
        dialog.SetHint($"Verrà salvato in: {Persistence.Json.ProjectsFolder.GetOrCreate()}");

        string? name = await dialog.ShowDialog<string?>(this).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) { return; }

        string path = Persistence.Json.ProjectsFolder.ResolvePath(name);

        DbDeltaProject project = vm.Build() with { Name = name, LastModifiedUtc = DateTime.UtcNow };
        Persistence.Xml.XmlProjectStore store = new();
        await store.SaveAsync(path, project, CancellationToken.None).ConfigureAwait(true);

        // Touch the MRU so the next session lists this project first.
        var mru =
            Persistence.Json.JsonRecentProjectsStore.CreateDefault();
        await mru.AddOrTouchAsync(path, CancellationToken.None).ConfigureAwait(true);
    }

    // ── Carica… button ────────────────────────────────────────────────────────

    private async void OnLoadClick(object? sender, RoutedEventArgs e)
    {
        // Round-7 UX: show the MRU dialog (with browse-from-disk fallback)
        // instead of jumping straight into the OS file picker.
        var recents =
            Persistence.Json.JsonRecentProjectsStore.CreateDefault();
        IReadOnlyList<Persistence.Json.RecentProject> list =
            await recents.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        LoadProjectDialog loadDialog = new();
        loadDialog.SetRecentProjects(list);

        string? pickedPath = await loadDialog.ShowDialog<string?>(this).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(pickedPath)) { return; }

        Persistence.Xml.XmlProjectStore store = new();
        DbDeltaProject project =
            await store.LoadAsync(pickedPath, CancellationToken.None)
                       .ConfigureAwait(true);

        if (DataContext is ProjectSetupViewModel vm)
        {
            vm.LoadFrom(project);
        }
    }
}
