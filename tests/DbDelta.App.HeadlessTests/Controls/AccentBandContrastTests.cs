using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// Every solid accent band in the app, measured in both themes.
/// </summary>
/// <remarks>
/// <para>
/// Three of these shipped wrong, twice for the same reason. The palette has
/// two colours per semantic: the accent (tuned to stand out ON THE PAGE, so the
/// dark ramp LIGHTENS it) and the Fg (tuned for text on the pale Soft surface).
/// Neither is the fill for a solid band carrying near-white text, which is what
/// the *Strong* tokens are for — and the mistake is invisible on whichever
/// theme the author happens to be running.
/// </para>
/// <para>
/// Asserted as a measured ratio rather than as "uses the right token": both
/// bugs were picking a token that exists and is named plausibly. A new band
/// belongs in <see cref="Bands"/>.
/// </para>
/// </remarks>
public class AccentBandContrastTests
{
    /// <summary>WCAG AA for body text.</summary>
    private const double TextMinimum = 4.5;

    /// <summary>WCAG AA for a graphical element.</summary>
    private const double GlyphMinimum = 3.0;

    private static Window BackfillWindow() => new BackfillDialog
    {
        DataContext = new BackfillViewModel(
            [new BackfillRequirement("dbo", "T", "C", "int")]),
    };

    private static Window ConfirmWindow() => new ConfirmExecuteDialog
    {
        DataContext = new ConfirmExecuteViewModel(
            1, 1, 0, 0, "src", "tgt",
            () => Task.FromResult(new SqlBatchResult(true, null, 1, 1))),
    };

    private static Window LastRunWindow(bool succeeded) => new LastRunDialog
    {
        DataContext = new LastRunViewModel(
            new SqlBatchResult(succeeded, succeeded ? null : "boom", 1, 10),
            new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Local)),
    };

    public static TheoryData<string, string, string> Bands() => new()
    {
        { "Light", "backfill",       "WarningBand" },
        { "Dark",  "backfill",       "WarningBand" },
        { "Light", "confirm",        "DangerBand" },
        { "Dark",  "confirm",        "DangerBand" },
        { "Light", "lastrun-ok",     "SuccessBand" },
        { "Dark",  "lastrun-ok",     "SuccessBand" },
        { "Light", "lastrun-failed", "DangerBand" },
        { "Dark",  "lastrun-failed", "DangerBand" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Bands))]
    public void Every_accent_band_is_readable_in_both_themes(string variant, string dialog, string band)
    {
        // Set on the APPLICATION, not the window: the design-system brushes are
        // single instances in Application.Resources whose Color follows a
        // DynamicResource, so they re-resolve with the app's variant and ignore
        // the window's. A per-window variant leaves every case asserting Light.
        Application app = Application.Current!;
        ThemeVariant previous = app.RequestedThemeVariant ?? ThemeVariant.Light;
        try
        {
            app.RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

            Window window = dialog switch
            {
                "backfill" => BackfillWindow(),
                "confirm" => ConfirmWindow(),
                "lastrun-ok" => LastRunWindow(succeeded: true),
                _ => LastRunWindow(succeeded: false),
            };
            window.Width = 800;
            window.Height = 560;
            window.Show();

            Border target = window.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Name == band);
            Color background = ((ISolidColorBrush)target.Background!).Color;

            using (new FluentAssertions.Execution.AssertionScope($"{dialog}/{band} in {variant}"))
            {
                foreach (TextBlock text in target.GetVisualDescendants().OfType<TextBlock>())
                {
                    Contrast(background, ((ISolidColorBrush)text.Foreground!).Color)
                        .Should().BeGreaterThanOrEqualTo(TextMinimum);
                }
                foreach (Avalonia.Controls.Shapes.Path glyph in
                         target.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
                {
                    Contrast(background, ((ISolidColorBrush)glyph.Fill!).Color)
                        .Should().BeGreaterThanOrEqualTo(GlyphMinimum);
                }
            }
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    /// <summary>
    /// The two themes must actually resolve to different fills, or the theory
    /// proves the same thing twice — which is exactly what a per-window variant
    /// did before.
    /// </summary>
    [AvaloniaFact]
    public void The_theme_variant_really_changes_the_fill()
    {
        Application app = Application.Current!;
        ThemeVariant previous = app.RequestedThemeVariant ?? ThemeVariant.Light;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            Color light = BandColour(LastRunWindow(succeeded: true), "SuccessBand");
            app.RequestedThemeVariant = ThemeVariant.Dark;
            Color dark = BandColour(LastRunWindow(succeeded: true), "SuccessBand");

            dark.Should().NotBe(light);
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    private static Color BandColour(Window window, string band)
    {
        window.Show();
        return ((ISolidColorBrush)window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == band).Background!).Color;
    }

    /// <summary>WCAG 2.x relative-luminance contrast ratio, 1.0 … 21.0.</summary>
    internal static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        double v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
