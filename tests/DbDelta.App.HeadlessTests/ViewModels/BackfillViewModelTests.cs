using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.ScriptGen;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// The dialog that answers Msg 4901: a NOT NULL column with no default cannot
/// be added to a populated table, and the value has to come from a human. These
/// cover what the dialog hands back to the generator.
/// </summary>
public class BackfillViewModelTests
{
    private static BackfillViewModel Vm(params BackfillRequirement[] requirements) => new(requirements);

    private static BackfillRequirement Req(string column, string dataType) =>
        new("dbo", "Corrieri_TipiDocumentazioni", column, dataType);

    [AvaloniaFact]
    public void Every_column_gets_a_row_seeded_with_the_suggested_value()
    {
        BackfillViewModel vm = Vm(Req("Corriere", "nvarchar(16)"), Req("Ordine", "int"));

        vm.ColumnCount.Should().Be(2);
        vm.Rows[0].QualifiedTable.Should().Be("dbo.Corrieri_TipiDocumentazioni");
        vm.Rows[0].ColumnName.Should().Be("Corriere");
        vm.Rows[0].DataType.Should().Be("nvarchar(16)");
        vm.Rows[0].Value.Should().Be("('')");
        vm.Rows[1].Value.Should().Be("((0))");
    }

    /// <summary>
    /// The key has to be the one <c>ScriptGenerator</c> looks values up by, or
    /// the dialog is answered and the column is still emitted unaided.
    /// </summary>
    [AvaloniaFact]
    public void The_map_is_keyed_the_way_the_generator_reads_it()
    {
        BackfillViewModel vm = Vm(Req("Corriere", "nvarchar(16)"));
        vm.Rows[0].Value = "  ('GLS')  ";

        IReadOnlyDictionary<(string Schema, string Table, string Column), string> map = vm.ToMap();

        map[("dbo", "Corrieri_TipiDocumentazioni", "Corriere")].Should().Be("('GLS')");
    }

    /// <summary>
    /// A blank row is not "no value": omitting it emits the column unchanged and
    /// the deploy dies on that table halfway through — the very failure this
    /// dialog exists to prevent. So the confirm button is closed until every row
    /// is answered, and it reopens the moment the last one is.
    /// </summary>
    [AvaloniaFact]
    public void Confirmation_is_closed_while_any_row_is_blank()
    {
        BackfillViewModel vm = Vm(Req("Corriere", "nvarchar(16)"), Req("Ordine", "int"));
        vm.CanConfirm.Should().BeTrue("every row starts on its suggested value");

        vm.Rows[1].Value = "   ";
        vm.CanConfirm.Should().BeFalse();

        vm.Rows[1].Value = "((1))";
        vm.CanConfirm.Should().BeTrue();
    }

    /// <summary>
    /// The button binds to <see cref="BackfillViewModel.CanConfirm"/>, so a
    /// value edited in place has to raise it — otherwise the gate is stuck on
    /// whatever it was when the dialog opened.
    /// </summary>
    [AvaloniaFact]
    public void Editing_a_value_notifies_the_confirmation_gate()
    {
        BackfillViewModel vm = Vm(Req("Corriere", "nvarchar(16)"));
        List<string?> raised = [];
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Rows[0].Value = "('GLS')";

        raised.Should().Contain(nameof(BackfillViewModel.CanConfirm));
    }
}
