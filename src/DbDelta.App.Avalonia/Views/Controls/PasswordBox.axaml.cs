using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// Reusable masked-password input with a hold-to-reveal ghost button on the
/// right. Replaces the 6 hand-rolled handlers that previously lived in
/// ProjectSetupDialog (Source + Target) and ConnectionEditDialog. See CLAUDE.md
/// UI rule #3 (DRY) for the contract.
/// </summary>
public partial class PasswordBox : UserControl
{
    /// <summary>The masked password text — bind TwoWay to the view-model field.</summary>
    public static readonly StyledProperty<string> PasswordProperty =
        AvaloniaProperty.Register<PasswordBox, string>(nameof(Password), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, defaultValue: string.Empty);

    /// <summary>Tooltip surfaced on the reveal button — default Italian.</summary>
    public static readonly StyledProperty<string> RevealTooltipProperty =
        AvaloniaProperty.Register<PasswordBox, string>(nameof(RevealTooltip), defaultValue: "Tieni premuto per mostrare la password");

    public string Password
    {
        get => GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    public string RevealTooltip
    {
        get => GetValue(RevealTooltipProperty);
        set => SetValue(RevealTooltipProperty, value);
    }

    public PasswordBox()
    {
        InitializeComponent();
        Button reveal = this.FindControl<Button>("PART_RevealButton")!;
        reveal.AddHandler(PointerPressedEvent, OnRevealPressed, RoutingStrategies.Tunnel);
        reveal.AddHandler(PointerReleasedEvent, OnRevealReleased, RoutingStrategies.Tunnel);
    }

    private void OnRevealPressed(object? sender, PointerPressedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("PART_PasswordBox")!;
        box.PasswordChar = '\0';
    }

    private void OnRevealReleased(object? sender, PointerReleasedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("PART_PasswordBox")!;
        box.PasswordChar = '•';
    }
}
