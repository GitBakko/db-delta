using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// The two rules in <c>src/DbDelta.App.Avalonia/CLAUDE.md</c> that nothing
/// asserted: no naked buttons, and one height for every single-line control.
/// </summary>
/// <remarks>
/// <see cref="AccentBandContrastTests"/> has covered the accent bands since
/// 2026-08-01, and it covers them by MEASURING rather than by naming a token —
/// which is why it caught two bugs that had picked a plausible token. These do
/// the same for the other two rules: nothing failed if someone put
/// <c>Background="Transparent"</c> back on <c>.ghost</c> or dropped a
/// <c>MinHeight</c>, and both are exactly the kind of change a diff makes look
/// harmless.
/// </remarks>
public class UiInvariantTests
{
    /// <summary>Every button class the app styles define, minus the two below.</summary>
    public static TheoryData<string> ButtonClasses =>
    [
        "ghost", "ghost-amber", "ghost-cobalt", "ghost-crimson", "ghost-emerald",
        "ghost-violet", "lg", "primary", "solid-crimson", "solid-emerald", "swap",
    ];

    private static T Realised<T>(T control) where T : Control
    {
        Window host = new() { Content = control };
        host.Show();
        return control;
    }

    [AvaloniaTheory]
    [MemberData(nameof(ButtonClasses))]
    public void No_button_class_ships_without_a_visible_surface(string cssClass)
    {
        // Rule #1: a fill OR a border. "Ghost" here does NOT mean transparent —
        // the class names were kept when the look changed, so the name is the
        // one thing that cannot be trusted, and the brush is what gets asked.
        Button button = Realised(new Button { Classes = { cssClass }, Content = "x" });

        bool hasFill = button.Background is not null
            && (button.Background as ISolidColorBrush)?.Color.A != 0;
        bool hasBorder = button.BorderThickness.Top > 0
            && button.BorderBrush is not null
            && (button.BorderBrush as ISolidColorBrush)?.Color.A != 0;

        (hasFill || hasBorder).Should().BeTrue(
            $"Button.{cssClass} has to be visible without hovering it");
    }

    [AvaloniaFact]
    public void Every_single_line_control_shares_the_same_minimum_height()
    {
        // Rule #2: rows of mixed controls read as one strip only while all of
        // them agree. CheckBox is in the rule's list and had no style at all.
        Realised(new Button { Content = "x" }).MinHeight.Should().Be(32);
        Realised(new TextBox()).MinHeight.Should().Be(32);
        Realised(new ComboBox()).MinHeight.Should().Be(32);
        Realised(new AutoCompleteBox()).MinHeight.Should().Be(32);
        Realised(new CheckBox { Content = "x" }).MinHeight.Should().Be(32);
    }

    [AvaloniaFact]
    public void The_round_swap_button_keeps_its_declared_exception()
    {
        // The negative control. Without it the rule above could be satisfied by
        // setting 32 on everything, and the one button that is deliberately
        // 36x36 round would be "fixed" into the strip.
        Button swap = Realised(new Button { Classes = { "swap" } });

        swap.Width.Should().Be(36);
        swap.Height.Should().Be(36);
    }
}
