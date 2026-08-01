using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.ScriptGen;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// This dialog is only ever seen in front of a live deploy, with the operator
/// waiting: a binding that resolves to nothing would ship as blank cells or an
/// uneditable value and be found by a user, mid-run. The view-model tests
/// cannot see any of that — it lives in XAML.
/// </summary>
public class BackfillDialogTests
{
    private static BackfillDialog Show(params BackfillRequirement[] requirements)
    {
        BackfillDialog dialog = new()
        {
            DataContext = new BackfillViewModel(requirements),
            Width = 760,
            Height = 520,
        };
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

}
