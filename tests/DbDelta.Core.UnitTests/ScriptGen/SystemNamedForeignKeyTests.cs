using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// The foreign-key half of roadmap item 12. <c>FK__Righe__TestaId__2B3F6F97</c>
/// takes its suffix from that constraint's own <c>object_id</c>, so the same
/// schema deployed twice produces two names that disagree by construction —
/// and paired by name, a table carrying an inline <c>REFERENCES</c> is
/// Different forever while the script drops the target's hash to add the
/// source's.
/// </summary>
/// <remarks>
/// Item 12 fixed PK / UQ / CHECK / DEFAULT and deliberately left FKs paired by
/// name, because their emission side is keyed on the name in more places than
/// one file. The count written down at the time was three and the real one is
/// five: the two FK deltas, the orchestrated re-add set, and the two lookups
/// that find the OTHER side's foreign key by this side's name — which no grep
/// for the set names finds.
/// </remarks>
public class SystemNamedForeignKeyTests
{
    private const string SourceHash = "FK__Righe__TestaId__2B3F6F97";
    private const string TargetHash = "FK__Righe__TestaId__7C9A1B2D";

    private static Column Col(string name, int ordinal) =>
        new(name, "int", isNullable: false, ordinal);

    private static ForeignKey Fk(
        string name,
        bool systemNamed = true,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        bool disabled = false) =>
        new(name, ["TestaId"], "dbo", "Testa", ["Id"], onDelete,
            ReferentialAction.NoAction, IsDisabled: disabled, IsNotForReplication: false)
        { IsSystemNamed = systemNamed };

    private static Table Righe(params Constraint[] constraints) =>
        new("dbo", "Righe", [Col("Id", 1), Col("TestaId", 2)], constraints, []);

    private static string Script(Table src, Table tgt)
    {
        ComparisonResult result = new ComparisonEngine().Compare(
            new Database("d", [], [src]),
            new Database("d", [], [tgt]),
            ComparisonOptions.Default);
        return new ScriptGenerator().Generate(result);
    }

    [Fact]
    public void A_table_differing_only_by_the_foreign_key_hash_produces_no_script()
    {
        // Engine AND emitter in one: the pair has to come back Identical and
        // the script has to be empty. If only one of the two learns the rule,
        // this is the test that says so.
        Script(Righe(Fk(SourceHash)), Righe(Fk(TargetHash)))
            .Should().NotContain("FOREIGN KEY").And.NotContain("DROP CONSTRAINT");
    }

    [Fact]
    public void The_engine_calls_a_table_differing_only_by_the_hash_identical()
    {
        ComparisonResult result = new ComparisonEngine().Compare(
            new Database("d", [], [Righe(Fk(SourceHash))]),
            new Database("d", [], [Righe(Fk(TargetHash))]),
            ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(p => p.Identity.Kind == "Table")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void A_new_table_creates_its_auto_named_foreign_key_without_a_name()
    {
        // Copying the source's hash onto the target pins a name the next
        // comparison can never reproduce. Omitting it lets the target server
        // mint its own, which is what the source's server did.
        string sql = new ForeignKeyScriptEmitter().EmitAdd("dbo", "Righe", Fk(SourceHash));

        sql.Should().Be("ALTER TABLE [dbo].[Righe] ADD FOREIGN KEY ([TestaId]) "
                      + "REFERENCES [dbo].[Testa] ([Id]);");
    }

    [Fact]
    public void An_explicitly_named_foreign_key_is_still_created_by_name()
    {
        // The negative control: if the name ever stops being written at all,
        // this is the test that falls, and it is the one that is right.
        string sql = new ForeignKeyScriptEmitter().EmitAdd(
            "dbo", "Righe", Fk("FK_Righe_Testa", systemNamed: false));

        sql.Should().StartWith("ALTER TABLE [dbo].[Righe] ADD CONSTRAINT [FK_Righe_Testa] FOREIGN KEY");
    }

    [Fact]
    public void A_named_target_foreign_key_facing_an_auto_named_source_one_is_replaced()
    {
        // The second negative control: these two really are different
        // constraints. The target holds a name the source never asked for, so
        // the script drops it and lets the server coin its own.
        string sql = Script(Righe(Fk(SourceHash)), Righe(Fk("FK_Righe_Testa", systemNamed: false)));

        sql.Should().Contain("DROP CONSTRAINT [FK_Righe_Testa]");
        sql.Should().Contain("ADD FOREIGN KEY ([TestaId])");
    }

    [Fact]
    public void An_auto_named_foreign_key_whose_shape_changed_is_replaced_once()
    {
        // Same foreign key, changed — not two foreign keys. Pairing by shape
        // must be narrow enough to still say "this one, changed".
        string sql = Script(
            Righe(Fk(SourceHash, onDelete: ReferentialAction.Cascade)),
            Righe(Fk(TargetHash)));

        sql.Should().Contain($"DROP CONSTRAINT {Sql.Q(TargetHash)}");
        sql.Should().Contain("ADD FOREIGN KEY ([TestaId])").And.Contain("ON DELETE CASCADE");
        CountOf(sql, "ADD FOREIGN KEY").Should().Be(1, "the delta and the re-add must not both fire");
    }

    [Fact]
    public void A_rebuild_drops_and_restores_an_inbound_auto_named_key_once()
    {
        // The orchestrated re-add path, which is where the name lookup was
        // hidden: rebuilding Testa forces every inbound key to be dropped
        // first and put back after the rename. The source side is
        // authoritative, and finding ITS key under the target's hash finds
        // nothing — the key would then be claimed under a name the skip check
        // never asks about, and come back twice or not at all.
        Table testaOld = new("dbo", "Testa",
            [new Column("Id", "int", isNullable: false, ordinal: 1)], [], []);
        Table testaNew = new("dbo", "Testa",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1)], [], []);

        ComparisonResult result = new ComparisonEngine().Compare(
            new Database("d", [], [testaNew, Righe(Fk(SourceHash))]),
            new Database("d", [], [testaOld, Righe(Fk(TargetHash))]),
            ComparisonOptions.Default);
        string sql = new ScriptGenerator().Generate(result);

        sql.Should().Contain($"DROP CONSTRAINT {Sql.Q(TargetHash)}");
        CountOf(sql, "ADD FOREIGN KEY").Should().Be(1, "dropped once, restored once");
        sql.Should().NotContain(SourceHash, "the source's hash must not be pinned onto the target");
    }

    [Fact]
    public void Widening_the_referenced_column_restores_the_inbound_key_once()
    {
        // The OTHER orchestrated path, and the one where the two sides really
        // do carry different hashes: a foreign key over a column being retyped
        // blocks the ALTER (Msg 5074), so it is dropped up front and claimed
        // for the late re-add. The claim is keyed with the SOURCE's name
        // because that is what the add delta looks itself up with — keyed with
        // the target's, the delta never sees the claim and adds the key a
        // second time.
        ComparisonResult result = new ComparisonEngine().Compare(
            new Database("d", [], [Testa("bigint"),
                RigheOver("bigint", Fk(SourceHash, onDelete: ReferentialAction.Cascade))]),
            new Database("d", [], [Testa("int"), RigheOver("int", Fk(TargetHash))]),
            ComparisonOptions.Default);
        string sql = new ScriptGenerator().Generate(result);

        sql.Should().Contain($"DROP CONSTRAINT {Sql.Q(TargetHash)}");
        // ON DELETE CASCADE only on the source, so the add delta WANTS to write
        // this key as well — the claim is the only thing holding it back, and
        // the claim only lands if it is keyed the way the delta reads it.
        sql.Should().Contain("ON DELETE CASCADE");
        CountOf(sql, "ADD FOREIGN KEY").Should().Be(1, "dropped once, restored once");
        sql.Should().NotContain(SourceHash);
    }

    [Fact]
    public void The_diff_viewer_body_is_identical_on_both_sides()
    {
        // The other end of the round-16 bug: a pane showing two different texts
        // for a row the grid now calls Identical.
        TableScriptEmitter.GenerateFullTableBody(Righe(Fk(SourceHash)))
            .Should().Be(TableScriptEmitter.GenerateFullTableBody(Righe(Fk(TargetHash))));
    }

    [Fact]
    public void A_disabled_auto_named_foreign_key_keeps_its_name()
    {
        // Declared limitation, not an oversight: NOCHECK CONSTRAINT needs a
        // name, and the only name available is the one the source server
        // minted. Emitting the constraint unnamed and skipping the disable
        // would silently change enforcement — so this shape keeps the hash, and
        // keeps churning between two servers. See docs/BACKLOG.md.
        string sql = new ForeignKeyScriptEmitter().EmitAdd("dbo", "Righe", Fk(SourceHash, disabled: true));

        sql.Should().Contain($"ADD CONSTRAINT {Sql.Q(SourceHash)} FOREIGN KEY");
        sql.Should().Contain($"NOCHECK CONSTRAINT {Sql.Q(SourceHash)}");
    }

    private static Table Testa(string type) =>
        new("dbo", "Testa", [new Column("Id", type, isNullable: false, ordinal: 1)], [], []);

    private static Table RigheOver(string type, ForeignKey fk) =>
        new("dbo", "Righe",
            [Col("Id", 1), new Column("TestaId", type, isNullable: false, ordinal: 2)], [fk], []);

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }
}
