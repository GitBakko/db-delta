using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

/// <summary>
/// The invariant the whole tool rests on: a module we deploy must compare
/// Identical the next time it is read back. A difference that survives its own
/// script is a difference no operator can remove — the second run emits exactly
/// what the first one emitted, reports success, and changes nothing.
/// </summary>
/// <remarks>
/// Found the hard way on a real database: 33 modules stayed Different after a
/// clean deploy of 279 objects. Every one of their bodies ended with a
/// semicolon FOLLOWED BY A NEWLINE, so the emitter — which asked whether the
/// last character was a semicolon — appended a second one, and the normalizer
/// stripped only one of the two. Nothing else about them was unusual, and no
/// unit test looked at a body's last character.
/// </remarks>
public class DeployedModuleConvergesTests
{
    private static Database Db(params View[] v) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: v, Procedures: []);

    private static Database Db(params StoredProcedure[] p) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: p);

    private static Database Db(params Function[] f) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: []) { Functions = f };

    private static Database Db(params Trigger[] t) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: []) { Triggers = t };

    /// <summary>
    /// The tails a real catalog actually holds. The newline ones are the bug:
    /// SQL Server stores the definition VERBATIM, and a developer's file
    /// virtually always ends with a newline.
    /// </summary>
    public static TheoryData<string> Tails() =>
    [
        "SELECT 1 AS Id",       // no terminator at all
        "SELECT 1 AS Id;",      // terminator, nothing after
        "SELECT 1 AS Id;\n",    // terminator + newline  ← the 33
        "SELECT 1 AS Id;\r\n",  // same, CRLF
        "SELECT 1 AS Id;\n\n\n",// terminator + blank lines
        "SELECT 1 AS Id\n",     // no terminator, trailing newline
        "SELECT 1 AS Id;;",     // already doubled in the catalog
    ];

    [Theory]
    [MemberData(nameof(Tails))]
    public void A_deployed_view_compares_identical_on_the_next_run(string tail)
    {
        View source = new("dbo", "VwArticoli", $"CREATE VIEW [dbo].[VwArticoli] AS {tail}", IsEncrypted: false);

        // Exactly what the deploy writes into the target: SQL Server stores the
        // text it was handed, verbatim.
        string deployed = new ViewScriptEmitter().Emit(
            new DifferencePair(source.Identity, DifferenceStatus.OnlyInA, source, null));
        View target = new("dbo", "VwArticoli", deployed, IsEncrypted: false);

        new ComparisonEngine().Compare(Db(source), Db(target), ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical, $"deployed:\n{deployed}");
    }

    [Theory]
    [MemberData(nameof(Tails))]
    public void A_deployed_procedure_compares_identical_on_the_next_run(string tail)
    {
        StoredProcedure source = new(
            "dbo", "SpKpi", $"CREATE PROCEDURE [dbo].[SpKpi] AS BEGIN {tail} END;\n", IsEncrypted: false);

        string deployed = new ProcedureScriptEmitter().Emit(
            new DifferencePair(source.Identity, DifferenceStatus.OnlyInA, source, null));
        StoredProcedure target = new("dbo", "SpKpi", deployed, IsEncrypted: false);

        new ComparisonEngine().Compare(Db(source), Db(target), ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure")
            .Status.Should().Be(DifferenceStatus.Identical, $"deployed:\n{deployed}");
    }

    [Theory]
    [MemberData(nameof(Tails))]
    public void A_deployed_function_compares_identical_on_the_next_run(string tail)
    {
        Function source = new(
            "dbo", "FnTotale",
            $"CREATE FUNCTION [dbo].[FnTotale]() RETURNS TABLE AS RETURN {tail}",
            IsEncrypted: false, FunctionKind.InlineTableValued);

        string deployed = new FunctionScriptEmitter().Emit(
            new DifferencePair(source.Identity, DifferenceStatus.OnlyInA, source, null));
        Function target = source with { Body = deployed };

        new ComparisonEngine().Compare(Db(source), Db(target), ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Function")
            .Status.Should().Be(DifferenceStatus.Identical, $"deployed:\n{deployed}");
    }

    [Theory]
    [MemberData(nameof(Tails))]
    public void A_deployed_trigger_compares_identical_on_the_next_run(string tail)
    {
        Trigger source = new(
            "dbo", "TrgArticoli",
            $"CREATE TRIGGER [dbo].[TrgArticoli] ON [dbo].[Articoli] AFTER INSERT AS BEGIN {tail} END",
            IsEncrypted: false, ParentSchema: "dbo", ParentTable: "Articoli",
            IsDisabled: false, IsNotForReplication: false);

        string deployed = new TriggerScriptEmitter().Emit(
            new DifferencePair(source.Identity, DifferenceStatus.OnlyInA, source, null));
        Trigger target = source with { Body = deployed };

        new ComparisonEngine().Compare(Db(source), Db(target), ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.Identical, $"deployed:\n{deployed}");
    }

    /// <summary>
    /// The emitter half, stated directly: one terminator, never two, whatever
    /// whitespace trails the body.
    /// </summary>
    [Theory]
    [InlineData("CREATE VIEW [dbo].[v] AS SELECT 1;\n")]
    [InlineData("CREATE VIEW [dbo].[v] AS SELECT 1;\r\n")]
    [InlineData("CREATE VIEW [dbo].[v] AS SELECT 1;   ")]
    [InlineData("CREATE VIEW [dbo].[v] AS SELECT 1;\n\n")]
    public void A_body_already_terminated_does_not_get_a_second_semicolon(string body)
    {
        string ddl = ModuleHeader.ToCreateOrAlterScript(body, "dbo", "v");

        ddl.Should().EndWith("SELECT 1;");
        ddl.Should().NotContain(";\n;").And.NotContain(";;");
    }

    /// <summary>
    /// The normalizer half: two terminators say what one says. Without this the
    /// 33 modules already carrying <c>";\n;"</c> in the target would stay
    /// Different until someone deployed over them again.
    /// </summary>
    [Fact]
    public void Repeated_trailing_semicolons_normalize_to_the_same_thing()
    {
        BodyNormalizer.Normalize("SELECT 1;;").Should().Be(BodyNormalizer.Normalize("SELECT 1"));
        BodyNormalizer.Normalize("SELECT 1;\n;").Should().Be(BodyNormalizer.Normalize("SELECT 1;"));
    }
}
