using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

        // Wire hold-to-reveal for Source password box.
        Button srcReveal = this.FindControl<Button>("SrcRevealButton")!;
        srcReveal.AddHandler(PointerPressedEvent, OnSrcRevealPressed, RoutingStrategies.Tunnel);
        srcReveal.AddHandler(PointerReleasedEvent, OnSrcRevealReleased, RoutingStrategies.Tunnel);

        // Wire hold-to-reveal for Target password box.
        Button tgtReveal = this.FindControl<Button>("TgtRevealButton")!;
        tgtReveal.AddHandler(PointerPressedEvent, OnTgtRevealPressed, RoutingStrategies.Tunnel);
        tgtReveal.AddHandler(PointerReleasedEvent, OnTgtRevealReleased, RoutingStrategies.Tunnel);

        // Wire custom item filters for the server AutoCompleteBoxes.
        // TextFilter is typed AutoCompleteFilterPredicate<string?> (matches by
        // the item's ToString); we need to inspect DiscoveredServer.Name and
        // .IpAddress directly, which requires ItemFilter (typed <object?>).
        AutoCompleteFilterPredicate<object?> filter = ServerItemFilter;
        AutoCompleteBox? srcServerBox = this.FindControl<AutoCompleteBox>("SrcServerBox");
        srcServerBox?.SetValue(AutoCompleteBox.ItemFilterProperty, filter);
        AutoCompleteBox? tgtServerBox = this.FindControl<AutoCompleteBox>("TgtServerBox");
        tgtServerBox?.SetValue(AutoCompleteBox.ItemFilterProperty, filter);

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

    private static bool ServerItemFilter(string? searchText, object? item) =>
        item is DiscoveredServer s
        && (string.IsNullOrEmpty(searchText)
            || s.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || (s.IpAddress?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false));

    private void HandleEndpointPropertyChanged(
        string? propertyName,
        string serverBoxName,
        string dbBoxName,
        ProjectEndpointPanelViewModel panel)
    {
        // Server scan no longer auto-opens its dropdown — the auto-scan now
        // runs on dialog open, and popping the combo open on a background
        // event was disruptive. The user opens the dropdown explicitly via
        // the chevron button when they want to browse the list.
        _ = serverBoxName; // kept for future use

        if (propertyName == nameof(ProjectEndpointPanelViewModel.IsLoadingDatabases)
            && !panel.IsLoadingDatabases
            && panel.HasDatabases)
        {
            OpenDropdownDeferred(dbBoxName);
        }
    }

    private void OpenDropdownDeferred(string boxName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AutoCompleteBox? box = this.FindControl<AutoCompleteBox>(boxName);
            if (box is null) { return; }
            box.Focus();
            box.IsDropDownOpen = true;
        }, DispatcherPriority.Background);
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

    // ── Password reveal ───────────────────────────────────────────────────────

    private void OnSrcRevealPressed(object? sender, PointerPressedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("SrcPasswordBox")!;
        box.PasswordChar = '\0';
    }

    private void OnSrcRevealReleased(object? sender, PointerReleasedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("SrcPasswordBox")!;
        box.PasswordChar = '•';
    }

    private void OnTgtRevealPressed(object? sender, PointerPressedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("TgtPasswordBox")!;
        box.PasswordChar = '\0';
    }

    private void OnTgtRevealReleased(object? sender, PointerReleasedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("TgtPasswordBox")!;
        box.PasswordChar = '•';
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

    // Quick save: delegates to Save-as flow when no path is associated.
    private void OnSaveClick(object? sender, RoutedEventArgs e) => _ = SaveAsAsync();

    private void OnSaveAsClick(object? sender, RoutedEventArgs e) => _ = SaveAsAsync();

    private async Task SaveAsAsync()
    {
        if (DataContext is not ProjectSetupViewModel vm)
        {
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva progetto DbDelta",
            DefaultExtension = "dbd",
            SuggestedFileName = vm.ProjectName,
            FileTypeChoices =
            [
                new FilePickerFileType("Progetto DbDelta")
                {
                    Patterns = ["*.dbd"],
                },
            ],
        }).ConfigureAwait(true);

        if (file is null)
        {
            return;
        }

        DbDeltaProject project = vm.Build();
        Persistence.Xml.XmlProjectStore store = new();
        await store.SaveAsync(file.Path.LocalPath, project, CancellationToken.None)
                   .ConfigureAwait(true);
    }

    // ── Carica… button ────────────────────────────────────────────────────────

    private async void OnLoadClick(object? sender, RoutedEventArgs e)
    {
        FilePickerOpenOptions opts = new()
        {
            Title = "Carica progetto DbDelta",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Progetto DbDelta") { Patterns = ["*.dbd"] },
            ],
        };
        IReadOnlyList<IStorageFile> files =
            await StorageProvider.OpenFilePickerAsync(opts).ConfigureAwait(true);
        if (files.Count == 0) { return; }
        IStorageFile file = files[0];

        Persistence.Xml.XmlProjectStore store = new();
        DbDeltaProject project =
            await store.LoadAsync(file.Path.LocalPath, CancellationToken.None)
                       .ConfigureAwait(true);

        if (DataContext is ProjectSetupViewModel vm)
        {
            vm.LoadFrom(project);
        }
    }
}
