using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class TriggerGoldenTests
{
    private readonly TriggerScriptEmitter _emitter = new();

    [Fact]
    public Task Add_trigger_emits_CREATE_OR_ALTER()
    {
        Trigger trg = new("dbo", "trg_Customer_Audit",
            "CREATE TRIGGER dbo.trg_Customer_Audit ON dbo.Customer AFTER INSERT AS BEGIN INSERT INTO dbo.Audit DEFAULT VALUES; END",
            IsEncrypted: false,
            ParentSchema: "dbo", ParentTable: "Customer",
            IsDisabled: false, IsNotForReplication: false);
        DifferencePair pair = new(trg.Identity, DifferenceStatus.OnlyInA, trg, null);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Drop_trigger_emits_DROP_TRIGGER_IF_EXISTS()
    {
        Trigger trg = new("dbo", "trg_Customer_Audit", "BODY", false, "dbo", "Customer", false, false);
        DifferencePair pair = new(trg.Identity, DifferenceStatus.OnlyInB, null, trg);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Different_trigger_with_changed_body_emits_CREATE_OR_ALTER()
    {
        Trigger a = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", false, false);
        Trigger b = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 2;", false, "dbo", "T", false, false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task State_only_diff_disabled_in_source_emits_DISABLE_TRIGGER()
    {
        Trigger a = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: true, IsNotForReplication: false);
        Trigger b = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: false, IsNotForReplication: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task State_only_diff_enabled_in_source_emits_ENABLE_TRIGGER()
    {
        Trigger a = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: false, IsNotForReplication: false);
        Trigger b = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: true, IsNotForReplication: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Encrypted_trigger_emits_warning_comment()
    {
        Trigger trg = new("dbo", "trgSecret", Body: null, IsEncrypted: true,
            ParentSchema: "dbo", ParentTable: "T", IsDisabled: false, IsNotForReplication: false);
        DifferencePair pair = new(trg.Identity, DifferenceStatus.OnlyInA, trg, null);
        return Verify(_emitter.Emit(pair));
    }
}
