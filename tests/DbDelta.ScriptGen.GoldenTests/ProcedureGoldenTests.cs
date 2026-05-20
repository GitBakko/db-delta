using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class ProcedureGoldenTests
{
    private readonly ProcedureScriptEmitter _emitter = new();

    [Fact]
    public Task Add_procedure_emits_CREATE_OR_ALTER()
    {
        StoredProcedure p = new("dbo", "uspGetCustomer",
            "CREATE PROCEDURE dbo.uspGetCustomer @Id int AS SELECT * FROM dbo.Customer WHERE Id = @Id;",
            IsEncrypted: false);
        DifferencePair pair = new(p.Identity, DifferenceStatus.OnlyInA, p, null);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Drop_procedure_emits_DROP_PROCEDURE_IF_EXISTS()
    {
        StoredProcedure p = new("dbo", "uspGetCustomer", "CREATE PROCEDURE dbo.uspGetCustomer AS RETURN 0;", IsEncrypted: false);
        DifferencePair pair = new(p.Identity, DifferenceStatus.OnlyInB, null, p);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Different_procedure_emits_CREATE_OR_ALTER_with_new_body()
    {
        StoredProcedure a = new("dbo", "u", "CREATE PROCEDURE dbo.u AS SELECT 1;", IsEncrypted: false);
        StoredProcedure b = new("dbo", "u", "CREATE PROCEDURE dbo.u AS SELECT 2;", IsEncrypted: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Encrypted_procedure_emits_warning_comment()
    {
        StoredProcedure p = new("dbo", "uspSecret", Body: null, IsEncrypted: true);
        DifferencePair pair = new(p.Identity, DifferenceStatus.OnlyInA, p, null);
        string ddl = _emitter.Emit(pair);
        return Verify(ddl);
    }
}
