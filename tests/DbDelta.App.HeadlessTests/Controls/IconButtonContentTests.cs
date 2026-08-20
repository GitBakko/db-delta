using Avalonia.Controls;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.App.Views.Controls;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Shared.Dtos;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// The icon+label content of a text button, extracted once and used seven
/// times. Before this it was inline in MainWindow.axaml — the DRY rule broken
/// by the window whose own CLAUDE.md states it.
/// </summary>
public class IconButtonContentTests
{
    /// <summary>
    /// The shell with a comparison on screen. Without one the action bar is
    /// collapsed and its two buttons never reach the visual tree — which is
    /// how the first version of this test counted five and called it seven.
    /// </summary>
    private static MainWindow Shell()
    {
        AppStateViewModel appState = new();
        DifferenceDto[] diffs = [new("Table", "dbo", "Orders", "Different")];
        DifferencePair[] pairs = [.. diffs.Select(d => new DifferencePair(
            Identity: new ObjectIdentity(d.SchemaName, d.ObjectName, d.Kind),
            Status: DifferenceStatus.Different,
            SideA: null,
            SideB: null))];
        appState.PublishComparison(new ComparisonResult(pairs), "src", "tgt");
        appState.LastComparison = new ComparisonResultDto([.. diffs]);

        MainWindow window = new() { DataContext = new MainWindowViewModel(appState) };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void Every_icon_button_in_the_shell_draws_an_icon_and_a_label()
    {
        MainWindow window = Shell();

        IconButtonContent[] contents = [.. window.GetVisualDescendants().OfType<IconButtonContent>()];

        contents.Should().HaveCountGreaterThanOrEqualTo(7, "the seven inline copies became usages");
        foreach (IconButtonContent content in contents)
        {
            content.Geometry.Should().NotBeNull($"\"{content.Text}\" has to draw something");
            content.Text.Should().NotBeNullOrWhiteSpace();
        }
    }

    [AvaloniaFact]
    public void The_icon_and_the_label_both_reach_the_visual_tree()
    {
        // A control that binds nothing renders an empty strip, and the window
        // still opens. The bindings are the whole point of the extraction, so
        // they are what gets asserted.
        IconButtonContent sut = new() { Text = "Carica", Geometry = Avalonia.Media.Geometry.Parse("M 0,0 L 8,8") };
        Window host = new() { Content = sut };
        host.Show();

        // The SAME geometry, not merely some geometry: a hardcoded Data in the
        // template renders happily and binds nothing.
        sut.GetVisualDescendants().OfType<AvaloniaPath>().Should().ContainSingle()
            .Which.Data.Should().BeSameAs(sut.Geometry);
        sut.GetVisualDescendants().OfType<TextBlock>().Should().ContainSingle()
            .Which.Text.Should().Be("Carica");
    }

    /// <summary>
    /// The connection manager had no way in. Connections autosave on every
    /// successful compare and feed the recent list, so while the dialog was
    /// unreachable that list grew and nobody could prune it.
    /// </summary>
    [AvaloniaFact]
    public void The_connection_manager_has_a_button_that_opens_it()
    {
        MainWindow window = Shell();
        var vm = (MainWindowViewModel)window.DataContext!;

        Button button = window.GetVisualDescendants()
            .OfType<Button>()
            .Should().ContainSingle(b => b.Command == vm.OpenConnectionManagerCommand)
            .Subject;

        button.IsVisible.Should().BeTrue();
        button.GetVisualDescendants().OfType<IconButtonContent>()
            .Should().ContainSingle().Which.Text.Should().Be("Connessioni");
    }

    [AvaloniaFact]
    public void The_three_button_families_keep_the_weights_they_were_drawn_at()
    {
        // The negative control on the extraction: the topbar, the project strip
        // and the action bar were drawn at different sizes ON PURPOSE. A single
        // hardcoded value here would be a design change hiding in a refactor.
        MainWindow window = Shell();

        double[] sizes = [.. window.GetVisualDescendants()
            .OfType<IconButtonContent>()
            .Select(c => c.IconSize)
            .Distinct()
            .Order()];

        sizes.Should().Equal(13d, 14d, 16d);
    }
}
