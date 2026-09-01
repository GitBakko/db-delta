using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A module's SET options are compiled into it, so deploying one under the
/// script preamble's blanket <c>QUOTED_IDENTIFIER ON</c> creates an object that
/// means something else than the one it was copied from.
/// </summary>
public class ModuleSetOptionsTests
{
    private static DifferencePair Pair(View v) =>
        new(v.Identity, DifferenceStatus.OnlyInA, v, null);

    private static View ViewWith(bool quoted = true, bool ansiNulls = true) =>
        new("dbo", "v", "CREATE VIEW dbo.v AS SELECT 1 AS Id", IsEncrypted: false,
            ModifyDate: null, UsesQuotedIdentifier: quoted, UsesAnsiNulls: ansiNulls);

    /// <summary>
    /// The common case has to stay byte-for-byte what it was — every golden file
    /// says so, and a preamble line repeated in front of 300 modules is noise an
    /// operator has to read past.
    /// </summary>
    [Fact]
    public void A_module_with_the_default_options_is_emitted_unchanged()
    {
        string sql = new ViewScriptEmitter().Emit(Pair(ViewWith()));

        sql.Should().NotContain("SET QUOTED_IDENTIFIER").And.NotContain("SET ANSI_NULLS");
    }

    /// <summary>
    /// Set before, restored after. Restoring is not tidiness: the preamble turned
    /// these ON for the whole script, and leaving one OFF bakes it into every
    /// module created after this one.
    /// </summary>
    [Fact]
    public void A_quoted_identifier_off_module_carries_its_setting_and_restores_it()
    {
        string sql = new ViewScriptEmitter().Emit(Pair(ViewWith(quoted: false)));

        sql.Should().StartWith("SET QUOTED_IDENTIFIER OFF;\nGO\n");
        sql.Should().EndWith("\nGO\nSET QUOTED_IDENTIFIER ON;");
        sql.Should().Contain("CREATE OR ALTER VIEW");
        sql.Should().NotContain("ANSI_NULLS", "only the option that differs is touched");
    }

    [Fact]
    public void An_ansi_nulls_off_module_carries_its_setting_too()
    {
        string sql = new ViewScriptEmitter().Emit(Pair(ViewWith(ansiNulls: false)));

        sql.Should().StartWith("SET ANSI_NULLS OFF;\nGO\n").And.EndWith("\nGO\nSET ANSI_NULLS ON;");
    }

    /// <summary>
    /// Both off is one SET statement, in the order the preamble lists them.
    /// </summary>
    [Fact]
    public void Both_options_off_are_set_together()
    {
        string sql = new ViewScriptEmitter().Emit(Pair(ViewWith(quoted: false, ansiNulls: false)));

        sql.Should().StartWith("SET QUOTED_IDENTIFIER, ANSI_NULLS OFF;\nGO\n");
        sql.Should().EndWith("\nGO\nSET QUOTED_IDENTIFIER, ANSI_NULLS ON;");
    }

    /// <summary>
    /// The wrapper has to reach every module kind: the settings apply to all four
    /// and the emitters share one helper precisely so none is forgotten.
    /// </summary>
    [Fact]
    public void Every_module_kind_carries_its_options()
    {
        StoredProcedure proc = new(
            "dbo", "p", "CREATE PROCEDURE dbo.p AS SELECT 1", IsEncrypted: false,
            ModifyDate: null, UsesQuotedIdentifier: false);
        Function fn = new(
            "dbo", "f", "CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar,
            ModifyDate: null, UsesQuotedIdentifier: false);
        Trigger trg = new(
            "dbo", "t", "CREATE TRIGGER dbo.t ON dbo.T AFTER INSERT AS SELECT 1",
            IsEncrypted: false, ParentSchema: "dbo", ParentTable: "T",
            IsDisabled: false, IsNotForReplication: false,
            ModifyDate: null, UsesQuotedIdentifier: false);

        new ProcedureScriptEmitter().Emit(new DifferencePair(proc.Identity, DifferenceStatus.OnlyInA, proc, null))
            .Should().StartWith("SET QUOTED_IDENTIFIER OFF;");
        new FunctionScriptEmitter().Emit(new DifferencePair(fn.Identity, DifferenceStatus.OnlyInA, fn, null))
            .Should().StartWith("SET QUOTED_IDENTIFIER OFF;");
        new TriggerScriptEmitter().Emit(new DifferencePair(trg.Identity, DifferenceStatus.OnlyInA, trg, null))
            .Should().StartWith("SET QUOTED_IDENTIFIER OFF;");
    }

    /// <summary>
    /// A DROP is not a compilation, so it must not drag the wrapper along —
    /// and it must certainly not leave the option OFF behind it.
    /// </summary>
    [Fact]
    public void A_dropped_module_carries_no_set_options()
    {
        View v = ViewWith(quoted: false);
        string sql = new ViewScriptEmitter().Emit(
            new DifferencePair(v.Identity, DifferenceStatus.OnlyInB, null, v));

        sql.Should().Be("DROP VIEW [dbo].[v];");
    }
}
