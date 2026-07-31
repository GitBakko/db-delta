using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// <c>CREATE OR ALTER</c> always yields an ENABLED trigger, and the
/// <c>DROP TABLE</c> inside an identity rebuild takes every trigger on the table
/// with it whatever its difference status. Both facts were handled at one call
/// site each instead of in the shared path, which left the sibling cases broken.
/// </summary>
public class TriggerLifecycleTests
{
    private static readonly ScriptGenerator Sut = new();

    private static Trigger Audit(string parentTable, string body, bool isDisabled) => new(
        Schema: "dbo",
        Name: "trg_Audit",
        Body: body,
        IsEncrypted: false,
        ParentSchema: "dbo",
        ParentTable: parentTable,
        IsDisabled: isDisabled,
        IsNotForReplication: false);

    private static string BodyFor(string parentTable, string statement) =>
        $"CREATE TRIGGER dbo.trg_Audit ON dbo.{parentTable} AFTER INSERT AS BEGIN {statement} END";

    private static Table Fatture(bool identity) =>
        new("dbo", "Fatture",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: identity, identitySeed: identity ? 1 : null,
                identityIncrement: identity ? 1 : null)],
            [new PrimaryKey("PK_Fatture", ["Id"], IsClustered: true)],
            []);

    private static DifferencePair RebuildPair() =>
        new(Fatture(true).Identity, DifferenceStatus.Different, Fatture(true), Fatture(false));

    [Fact]
    public void A_disabled_trigger_whose_body_changed_is_disabled_again()
    {
        // No rebuild here — the plain CREATE pass. CREATE OR ALTER re-enables the
        // trigger, so a deliberately disabled audit trigger came back ON the
        // moment its body changed. The guard existed only at the rebuild call
        // site.
        Trigger src = Audit("Ordini", BodyFor("Ordini", "SET NOCOUNT ON;"), isDisabled: true);
        Trigger tgt = Audit("Ordini", BodyFor("Ordini", "SET NOCOUNT OFF;"), isDisabled: true);

        string sql = Sut.Generate(new ComparisonResult(
            [new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt)]));

        int create = sql.IndexOf("TRIGGER dbo.trg_Audit", StringComparison.Ordinal);
        int disable = sql.IndexOf(
            "DISABLE TRIGGER [dbo].[trg_Audit] ON [dbo].[Ordini];", StringComparison.Ordinal);
        create.Should().BeGreaterThan(0);
        disable.Should().BeGreaterThan(create, "the trigger must not come back enabled");
    }

    [Fact]
    public void A_different_trigger_the_user_did_not_select_survives_the_rebuild_of_its_table()
    {
        // The GUI shape: the user ticks the Fatture row only. The trigger pair is
        // Different (its body changed too) so it is NOT Identical, and it is not
        // in the selection either — the rescue used to filter on Identical, so
        // nothing re-created it and DROP TABLE took it away under a success
        // verdict.
        Trigger src = Audit("Fatture", BodyFor("Fatture", "SET NOCOUNT ON;"), isDisabled: false);
        Trigger tgt = Audit("Fatture", BodyFor("Fatture", "SET NOCOUNT OFF;"), isDisabled: false);
        DifferencePair tablePair = RebuildPair();
        DifferencePair triggerPair = new(src.Identity, DifferenceStatus.Different, src, tgt);

        string script = DeployScriptBuilder.Build(
            new ComparisonResult([tablePair, triggerPair]),
            [tablePair],
            "src",
            "tgt",
            DateTime.UtcNow,
            [],
            []);

        int rename = script.IndexOf("sp_rename", StringComparison.Ordinal);
        int trigger = script.IndexOf("TRIGGER dbo.trg_Audit", StringComparison.Ordinal);
        rename.Should().BeGreaterThan(0, "this is the rebuild path");
        trigger.Should().BeGreaterThan(rename, "DROP TABLE destroyed it, so it must be re-created");
    }

    [Fact]
    public void A_state_only_trigger_change_on_a_rebuilt_table_is_recreated_not_enabled()
    {
        // Body identical on both sides, only IsDisabled flipped, and the user
        // ticked both rows. The CREATE pass emitted a bare ENABLE TRIGGER — for
        // an object the rebuild had already dropped (Msg 4916). The rebuild pass
        // owns every trigger on the table, so the CREATE pass must skip it.
        Trigger src = Audit("Fatture", BodyFor("Fatture", "SET NOCOUNT ON;"), isDisabled: false);
        Trigger tgt = Audit("Fatture", BodyFor("Fatture", "SET NOCOUNT ON;"), isDisabled: true);
        DifferencePair tablePair = RebuildPair();
        DifferencePair triggerPair = new(src.Identity, DifferenceStatus.Different, src, tgt);

        string sql = Sut.Generate(new ComparisonResult([tablePair, triggerPair]));

        sql.Should().NotContain("ENABLE TRIGGER",
            "the trigger no longer exists at that point — it has to be re-created in full");
        int rename = sql.IndexOf("sp_rename", StringComparison.Ordinal);
        int trigger = sql.IndexOf("TRIGGER dbo.trg_Audit", StringComparison.Ordinal);
        trigger.Should().BeGreaterThan(rename);
    }
}
