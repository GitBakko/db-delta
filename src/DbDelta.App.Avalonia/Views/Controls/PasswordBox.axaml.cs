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

        // A press does not always end in a release. Alt+Tab, a notification
        // stealing activation, a touch or pen contact the system cancels — all
        // take the capture away and no PointerReleased ever arrives, which used
        // to leave the password on screen in clear with nobody holding anything.
        // Measured, not assumed: see PasswordRevealCaptureTests.
        //
        // Direct, NOT Tunnel: PointerCaptureLost is registered as a direct
        // routed event, so a tunnelling handler is never invoked and the fix
        // would look applied while doing nothing.
        reveal.AddHandler(PointerCaptureLostEvent, OnRevealCaptureLost, RoutingStrategies.Direct);
    }

    private void OnRevealPressed(object? sender, PointerPressedEventArgs e) => Reveal();

    private void OnRevealReleased(object? sender, PointerReleasedEventArgs e) => Mask();

    private void OnRevealCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Mask();

    private void Reveal() => this.FindControl<TextBox>("PART_PasswordBox")!.PasswordChar = '\0';

    private void Mask() => this.FindControl<TextBox>("PART_PasswordBox")!.PasswordChar = '•';
}
