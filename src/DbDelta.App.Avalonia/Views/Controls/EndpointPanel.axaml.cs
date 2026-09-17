using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// The one endpoint panel the setup dialog hosts twice, for Source and Target
/// (CLAUDE.md UI rule #3). Its DataContext is a <c>ProjectEndpointPanelViewModel</c>;
/// <see cref="ScanCommandParameter"/> is the only per-instance input.
/// </summary>
public partial class EndpointPanel : UserControl
{
    /// <summary>Parameter passed to the shared scan command — "source" or "target".</summary>
    public static readonly StyledProperty<object?> ScanCommandParameterProperty =
        AvaloniaProperty.Register<EndpointPanel, object?>(nameof(ScanCommandParameter));

    public object? ScanCommandParameter
    {
        get => GetValue(ScanCommandParameterProperty);
        set => SetValue(ScanCommandParameterProperty, value);
    }

    public EndpointPanel()
    {
        InitializeComponent();
    }

    private void OnDatabaseDropToggleClick(object? sender, RoutedEventArgs e)
    {
        AutoCompleteBox? box = this.FindControl<AutoCompleteBox>("PART_DatabaseBox");
        if (box is null) { return; }
        box.Focus();
        box.IsDropDownOpen = !box.IsDropDownOpen;
    }
}
