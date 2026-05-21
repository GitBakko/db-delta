using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;

namespace DbDelta.App.Views;

/// <summary>
/// Code-behind for the "New project" setup dialog.
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

    private void HandleEndpointPropertyChanged(
        string? propertyName,
        string serverBoxName,
        string dbBoxName,
        ProjectEndpointPanelViewModel panel)
    {
        if (propertyName == nameof(ProjectEndpointPanelViewModel.IsScanningServers)
            && !panel.IsScanningServers
            && panel.ServerSuggestions.Count > 0)
        {
            OpenDropdownDeferred(serverBoxName);
        }
        else if (propertyName == nameof(ProjectEndpointPanelViewModel.IsLoadingDatabases)
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

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectSetupViewModel vm)
        {
            Close(vm.Build());
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        // Quick save: delegates to Save-as flow when no path is associated.
        _ = SaveAsAsync();

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
                new FilePickerFileType("DbDelta project")
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
}
