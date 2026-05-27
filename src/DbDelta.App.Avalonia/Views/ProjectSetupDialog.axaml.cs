using Avalonia.Controls;
using Avalonia.Interactivity;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Sql;

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

        // Wire custom item filters for the server AutoCompleteBoxes.
        // TextFilter is typed AutoCompleteFilterPredicate<string?> (matches by
        // the item's ToString); we need to inspect DiscoveredServer.Name and
        // .IpAddress directly, which requires ItemFilter (typed <object?>).
        AutoCompleteFilterPredicate<object?> filter = ServerItemFilter;
        AutoCompleteBox? srcServerBox = this.FindControl<AutoCompleteBox>("SrcServerBox");
        srcServerBox?.SetValue(AutoCompleteBox.ItemFilterProperty, filter);
        AutoCompleteBox? tgtServerBox = this.FindControl<AutoCompleteBox>("TgtServerBox");
        tgtServerBox?.SetValue(AutoCompleteBox.ItemFilterProperty, filter);

        // Reject header sentinel picks: clicking "Usati di recente" or
        // "Risultati scansione" must not select the divider as a value.
        if (srcServerBox is { }) { srcServerBox.SelectionChanged += OnServerSelectionChanged; }
        if (tgtServerBox is { }) { tgtServerBox.SelectionChanged += OnServerSelectionChanged; }

        // After scan finishes → open server dropdown deferred.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ProjectSetupViewModel vm)
            {
                vm.Source.PropertyChanged += (_, args) =>
                    HandleEndpointPropertyChanged(args.PropertyName, "SrcServerBox", "SrcDatabaseBox", vm.Source);
                vm.Target.PropertyChanged += (_, args) =>
                    HandleEndpointPropertyChanged(args.PropertyName, "TgtServerBox", "TgtDatabaseBox", vm.Target);
            }
        };
    }

    // ── Server item filter (Name OR IpAddress) ────────────────────────────────

    /// <summary>
    /// Match-by-Name or match-by-IP filter, with section-header sentinels
    /// (<c>IsHeaderOnly</c>) always passed through so the dividers stay
    /// visible regardless of what the user types.
    /// </summary>
    private static bool ServerItemFilter(string? searchText, object? item) =>
        item is DiscoveredServer s
        && (s.IsHeaderOnly
            || string.IsNullOrEmpty(searchText)
            || s.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || (s.IpAddress?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// Rejects accidental selection of the section-header sentinel items —
    /// they are dividers, not real servers. Resets the AutoCompleteBox text
    /// + selection back to whatever was active before the click.
    /// </summary>
    private static void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) { return; }
        if (box.SelectedItem is DiscoveredServer { IsHeaderOnly: true })
        {
            // Avoid recursion: temporarily detach, clear, re-attach.
            box.SelectionChanged -= OnServerSelectionChanged;
            box.SelectedItem = null;
            box.Text = string.Empty;
            box.SelectionChanged += OnServerSelectionChanged;
        }
    }

    private void HandleEndpointPropertyChanged(
        string? propertyName,
        string serverBoxName,
        string dbBoxName,
        ProjectEndpointPanelViewModel panel)
    {
        // Neither the server scan nor the database load auto-opens its
        // dropdown anymore — popping a combo open on a background event is
        // disruptive. The user clicks the chevron explicitly when they want
        // to browse the list.
        _ = propertyName;
        _ = serverBoxName;
        _ = dbBoxName;
        _ = panel;
    }


    // ── Server chevron clicks ─────────────────────────────────────────────────

    private void OnSrcServerDropToggleClick(object? sender, RoutedEventArgs e)
        => ToggleDropdown("SrcServerBox");

    private void OnTgtServerDropToggleClick(object? sender, RoutedEventArgs e)
        => ToggleDropdown("TgtServerBox");

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
