using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// Server chooser: a list-only <see cref="ComboBox"/> over the discovered
/// servers plus a free-text <see cref="TextBox"/> carrying the name that is
/// actually used.
/// </summary>
/// <remarks>
/// It replaces an <c>AutoCompleteBox</c> that filtered its own list by the text
/// it had just written: once a server was picked, the popup matched that one
/// name and every other server became unreachable. Splitting "browse" from
/// "type" removes the shared state that made the list depend on the field.
/// </remarks>
public partial class ServerPicker : UserControl
{
    /// <summary>Servers offered in the drop-down (recents + scan results).</summary>
    public static readonly StyledProperty<IEnumerable?> ServersProperty =
        AvaloniaProperty.Register<ServerPicker, IEnumerable?>(nameof(Servers));

    /// <summary>The server name in use — bind TwoWay to the view-model field.</summary>
    public static readonly StyledProperty<string> ServerNameProperty =
        AvaloniaProperty.Register<ServerPicker, string>(
            nameof(ServerName),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            defaultValue: string.Empty);

    /// <summary>Hides the drop-down while nothing has been discovered yet.</summary>
    public static readonly StyledProperty<bool> HasServersProperty =
        AvaloniaProperty.Register<ServerPicker, bool>(nameof(HasServers));

    /// <summary>Command behind the "Scansiona" button.</summary>
    public static readonly StyledProperty<ICommand?> ScanCommandProperty =
        AvaloniaProperty.Register<ServerPicker, ICommand?>(nameof(ScanCommand));

    /// <summary>Parameter passed to <see cref="ScanCommand"/>.</summary>
    public static readonly StyledProperty<object?> ScanCommandParameterProperty =
        AvaloniaProperty.Register<ServerPicker, object?>(nameof(ScanCommandParameter));

    /// <summary>Drives the in-button spinner while a scan runs.</summary>
    public static readonly StyledProperty<bool> IsScanningProperty =
        AvaloniaProperty.Register<ServerPicker, bool>(nameof(IsScanning));

    public IEnumerable? Servers
    {
        get => GetValue(ServersProperty);
        set => SetValue(ServersProperty, value);
    }

    public string ServerName
    {
        get => GetValue(ServerNameProperty);
        set => SetValue(ServerNameProperty, value);
    }

    public bool HasServers
    {
        get => GetValue(HasServersProperty);
        set => SetValue(HasServersProperty, value);
    }

    public ICommand? ScanCommand
    {
        get => GetValue(ScanCommandProperty);
        set => SetValue(ScanCommandProperty, value);
    }

    public object? ScanCommandParameter
    {
        get => GetValue(ScanCommandParameterProperty);
        set => SetValue(ScanCommandParameterProperty, value);
    }

    public bool IsScanning
    {
        get => GetValue(IsScanningProperty);
        set => SetValue(IsScanningProperty, value);
    }

    private readonly ComboBox _list;
    private bool _syncing;

    public ServerPicker()
    {
        InitializeComponent();
        _list = this.FindControl<ComboBox>("PART_ServerList")!;
        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing) { return; }
            if (_list.SelectedItem is DiscoveredServer { IsHeaderOnly: false } picked)
            {
                ServerName = picked.Name;
            }
        };

        // A scan rebuilds the collection in place, which drops the ComboBox's
        // selection while the name field keeps its value. Re-sync off ItemCount
        // rather than off the collection itself: it is raised after the ComboBox
        // has digested the change, so we cannot re-select into a stale list.
        _list.GetObservable(ItemsControl.ItemCountProperty)
             .Subscribe(new AnonymousObserver<int>(_ => SyncSelection()));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ServerNameProperty || change.Property == ServersProperty)
        {
            SyncSelection();
        }
    }

    /// <summary>
    /// Keeps the drop-down showing whatever the text field says — including the
    /// blank state when the name was typed by hand or cleared, so the list never
    /// claims a server that is not the one being used.
    /// </summary>
    private void SyncSelection()
    {
        _syncing = true;
        try
        {
            _list.SelectedItem = Servers?
                .OfType<DiscoveredServer>()
                .FirstOrDefault(s => !s.IsHeaderOnly
                                     && string.Equals(s.Name, ServerName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncing = false;
        }
    }
}
