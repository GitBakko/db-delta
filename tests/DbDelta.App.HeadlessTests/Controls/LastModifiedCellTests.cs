using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using DbDelta.App.Views.Controls;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// The shared cell behind both "Ultima modifica" columns. One control, used
/// twice — see the DRY rule in the app's CLAUDE.md; the two columns were
/// already near-identical before the marker was added to them.
/// </summary>
public class LastModifiedCellTests
{
    private static LastModifiedCell Show(string text, bool isNewer, string? tip = null)
    {
        LastModifiedCell cell = new() { Text = text, IsNewer = isNewer, Tip = tip };
        Window window = new() { Content = cell };
        window.Show();
        return cell;
    }

    private static TextBlock Part(LastModifiedCell cell, string name) =>
        cell.FindControl<TextBlock>(name)!;

    [AvaloniaFact]
    public void Date_is_always_rendered()
    {
        LastModifiedCell cell = Show("05/01/2026 08:00", isNewer: false);

        Part(cell, "PartValue").Text.Should().Be("05/01/2026 08:00");
    }

    [AvaloniaFact]
    public void Marker_is_hidden_on_the_older_side()
    {
        LastModifiedCell cell = Show("05/01/2026 08:00", isNewer: false);

        Part(cell, "PartArrow").IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Marker_is_shown_on_the_newer_side()
    {
        LastModifiedCell cell = Show("12/08/2026 14:22", isNewer: true);

        Part(cell, "PartArrow").IsVisible.Should().BeTrue();
    }

    // Bold carries the signal for anyone who cannot pick the small arrow out —
    // the marker is never colour or glyph alone.
    [AvaloniaFact]
    public void Newer_date_is_bold_and_the_older_one_is_not()
    {
        Part(Show("12/08/2026 14:22", isNewer: true), "PartValue")
            .FontWeight.Should().Be(FontWeight.Bold);

        Part(Show("05/01/2026 08:00", isNewer: false), "PartValue")
            .FontWeight.Should().NotBe(FontWeight.Bold);
    }

    // The marker sits next to a monospace timestamp in a 140px column; at the
    // same size as the digits it reads as punctuation instead of a marker.
    [AvaloniaFact]
    public void Marker_is_larger_than_the_timestamp_beside_it()
    {
        LastModifiedCell cell = Show("12/08/2026 14:22", isNewer: true);

        Part(cell, "PartArrow").FontSize
            .Should().BeGreaterThan(Part(cell, "PartValue").FontSize);
    }

    // Green, and green in both themes: the marker says "this is the side that
    // moved", which is the palette's success semantic.
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Marker_is_green(string variant)
    {
        InVariant(variant, () =>
        {
            Color arrow = MarkerColour();

            arrow.G.Should().BeGreaterThan(arrow.R);
            arrow.G.Should().BeGreaterThan(arrow.B);
        });
    }

    // The lesson AccentBandContrastTests already paid for twice: a colour that
    // reads on the author's theme can vanish on the other one.
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Marker_stays_readable_against_the_page_in_both_themes(string variant)
    {
        InVariant(variant, () =>
        {
            Application.Current!.TryGetResource(
                "BgBrush", Application.Current!.RequestedThemeVariant, out object? bg);
            Color surface = ((ISolidColorBrush)bg!).Color;

            // WCAG AA for a graphical element.
            AccentBandContrastTests.Contrast(surface, MarkerColour())
                .Should().BeGreaterThanOrEqualTo(3.0);
        });
    }

    // A hardcoded hex would satisfy both theories above while being the same
    // colour twice; the ramp has to actually move between themes.
    [AvaloniaFact]
    public void Marker_resolves_to_a_different_green_per_theme()
    {
        Color light = default;
        Color dark = default;
        InVariant("Light", () => light = MarkerColour());
        InVariant("Dark", () => dark = MarkerColour());

        dark.Should().NotBe(light);
    }

    private static Color MarkerColour() =>
        ((ISolidColorBrush)Part(Show("12/08/2026 14:22", isNewer: true), "PartArrow").Foreground!).Color;

    /// <summary>
    /// Runs <paramref name="body"/> with the APPLICATION on the given variant.
    /// The design-system brushes are single instances whose Color follows a
    /// DynamicResource, so they re-resolve with the app's variant and ignore a
    /// per-window one.
    /// </summary>
    private static void InVariant(string variant, Action body)
    {
        Application app = Application.Current!;
        ThemeVariant previous = app.RequestedThemeVariant ?? ThemeVariant.Light;
        try
        {
            app.RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            body();
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    // Kinds without a modify_date render an empty cell; the marker must not
    // appear next to nothing.
    [AvaloniaFact]
    public void Empty_cell_shows_no_marker()
    {
        LastModifiedCell cell = Show(string.Empty, isNewer: false);

        Part(cell, "PartArrow").IsVisible.Should().BeFalse();
        Part(cell, "PartValue").Text.Should().BeEmpty();
    }

    /// <summary>
    /// The tooltip belongs to the marker, not to the cell.
    /// </summary>
    /// <remarks>
    /// Both columns always pass the same NewerTooltip, so with the tip on the
    /// root panel "Modifica più recente fra i due lati…" appeared over the
    /// OLDER side, over identical rows, over one-sided rows and over the empty
    /// cells of a Sequence or Synonym — explaining a marker that was not there.
    /// Attached to the arrow it can only appear where the claim is true,
    /// because the arrow already carries that condition.
    /// </remarks>
    [AvaloniaFact]
    public void The_tooltip_hangs_off_the_marker_not_the_whole_cell()
    {
        LastModifiedCell cell = Show("12/08/2026 15:00", isNewer: true,
            tip: "Modifica più recente fra i due lati, secondo l'orologio di ciascun server.");

        ToolTip.GetTip(Part(cell, "PartArrow")).Should().NotBeNull(
            "the side that carries the marker is the side the text describes");
        ToolTip.GetTip(cell.FindControl<StackPanel>("PartRoot")!)
            .Should().BeNull("on the root it covered the whole cell, marker or not");
    }
}
