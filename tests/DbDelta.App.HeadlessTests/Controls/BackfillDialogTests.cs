using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// This dialog is only ever seen in front of a live deploy, with the operator
/// waiting: a binding that resolves to nothing would ship as blank cells or an
/// uneditable value and be found by a user, mid-run. The view-model tests
/// cannot see any of that — it lives in XAML.
/// </summary>
public class BackfillDialogTests
{
    private static BackfillDialog Show(params BackfillRequirement[] requirements) =>
        Show(null, requirements);

    private static BackfillDialog Show(
        ThemeVariant? theme,
        params BackfillRequirement[] requirements)
    {
        BackfillDialog dialog = new()
        {
            DataContext = new BackfillViewModel(requirements),
            Width = 760,
            Height = 520,
        };
        if (theme is not null) { dialog.RequestedThemeVariant = theme; }
        dialog.Show();
        return dialog;
    }

    private static BackfillRequirement Req(string column, string dataType) =>
        new("dbo", "Corrieri_TipiDocumentazioni", column, dataType);

    /// <summary>
    /// Table, column and type are read-only columns of the grid, so a binding
    /// typo there is silent — the row renders, empty, and the operator is asked
    /// to choose a value for a column nobody named.
    /// </summary>
    [AvaloniaFact]
    public void Every_requirement_is_rendered_with_the_column_it_describes()
    {
        BackfillDialog dialog = Show(Req("Corriere", "nvarchar(16)"));

        IEnumerable<string> texts = dialog.GetVisualDescendants()
            .OfType<DataGridCell>()
            .SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>())
            .Select(t => t.Text ?? string.Empty);

        texts.Should().Contain("dbo.Corrieri_TipiDocumentazioni")
            .And.Contain("Corriere")
            .And.Contain("nvarchar(16)");
    }

    /// <summary>
    /// The value cell is a live TextBox rather than an editable text column, so
    /// that what the operator typed is committed on every keystroke instead of
    /// on losing focus — the click that loses focus here is the one on
    /// «Applica». Both halves are asserted: the box exists, and the text
    /// travelling through it reaches the view-model.
    /// </summary>
    [AvaloniaFact]
    public void The_value_cell_is_a_live_box_that_writes_straight_back()
    {
        BackfillDialog dialog = Show(Req("Corriere", "nvarchar(16)"));
        var vm = (BackfillViewModel)dialog.DataContext!;

        TextBox box = dialog.GetVisualDescendants()
            .OfType<DataGridCell>()
            .SelectMany(c => c.GetVisualDescendants().OfType<TextBox>())
            .Should().ContainSingle("the value column carries an always-editable box")
            .Subject;

        box.Text.Should().Be("('')", "the suggested value is the starting point");

        box.Text = "('GLS')";

        vm.Rows[0].Value.Should().Be("('GLS')", "no focus change is needed to commit");
        vm.ToMap()[("dbo", "Corrieri_TipiDocumentazioni", "Corriere")].Should().Be("('GLS')");
    }

    /// <summary>
    /// The confirm gate has to be visible on the button, not only true in the
    /// view-model: an emptied row that still lets «Applica» through produces a
    /// map with a blank expression, and <c>DEFAULT ;</c> does not compile.
    /// </summary>
    [AvaloniaFact]
    public void The_confirm_button_closes_while_a_row_is_blank()
    {
        BackfillDialog dialog = Show(Req("Corriere", "nvarchar(16)"));
        var vm = (BackfillViewModel)dialog.DataContext!;

        Button apply = dialog.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("primary"));
        apply.IsEnabled.Should().BeTrue();

        vm.Rows[0].Value = "  ";

        apply.IsEnabled.Should().BeFalse();
    }

    /// <summary>
    /// The band shipped once with WarningFg on WarningBrush — the token for
    /// text on the PALE warning surface, painted on the solid one, i.e. dark
    /// brown on amber. It was reported from the app within minutes. Asserted as
    /// a measured contrast ratio rather than as "uses the right token", because
    /// the bug was picking a token that exists and is named plausibly.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light", "#FFAE5C00")]
    [InlineData("Dark", "#FF8A4A00")]
    public void The_warning_band_stays_readable_in_both_themes(string variant, string expectedBand)
    {
        // Set on the APPLICATION, not the window: the design-system brushes are
        // single instances in Application.Resources whose Color follows a
        // DynamicResource, so they re-resolve with the app's variant and ignore
        // the window's. A per-window variant here left both cases running
        // against Light and the theory proved nothing twice.
        Application app = Application.Current!;
        ThemeVariant previous = app.RequestedThemeVariant ?? ThemeVariant.Light;
        try
        {
            app.RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            BackfillDialog dialog = Show(Req("Corriere", "nvarchar(16)"));

            Border band = dialog.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Name == "WarningBand");
            Color background = ((ISolidColorBrush)band.Background!).Color;
            background.ToString().Should().BeEquivalentTo(expectedBand,
                "otherwise the variant never took and both cases assert the same theme");

            foreach (TextBlock text in band.GetVisualDescendants().OfType<TextBlock>())
            {
                Color foreground = ((ISolidColorBrush)text.Foreground!).Color;
                Contrast(background, foreground).Should().BeGreaterThanOrEqualTo(
                    4.5, "WCAG AA for body text — the band is the reason the dialog is open");
            }

            Color icon = ((ISolidColorBrush)band.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>().First().Fill!).Color;
            Contrast(background, icon).Should().BeGreaterThanOrEqualTo(
                3.0, "WCAG AA for a graphical element");
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    /// <summary>WCAG 2.x relative-luminance contrast ratio, 1.0 … 21.0.</summary>
    private static double Contrast(Color a, Color b)
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
