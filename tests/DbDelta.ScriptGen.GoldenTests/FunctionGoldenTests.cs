using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class FunctionGoldenTests
{
    private readonly FunctionScriptEmitter _emitter = new();

    [Fact]
    public Task Add_scalar_function_emits_CREATE_OR_ALTER()
    {
        Function fn = new("dbo", "fnSum",
            "CREATE FUNCTION dbo.fnSum(@a int, @b int) RETURNS int AS BEGIN RETURN @a + @b; END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        DifferencePair pair = new(fn.Identity, DifferenceStatus.OnlyInA, fn, null);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Drop_function_emits_DROP_FUNCTION_IF_EXISTS()
    {
        Function fn = new("dbo", "fnSum",
            "CREATE FUNCTION dbo.fnSum() RETURNS int AS BEGIN RETURN 0; END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        DifferencePair pair = new(fn.Identity, DifferenceStatus.OnlyInB, null, fn);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Different_inline_TVF_emits_CREATE_OR_ALTER_with_new_body()
    {
        Function a = new("dbo", "fnList",
            "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        Function b = new("dbo", "fnList",
            "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 2 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Encrypted_function_emits_warning_comment()
    {
        Function fn = new("dbo", "fnSecret", Body: null, IsEncrypted: true, FunctionKind: FunctionKind.Scalar);
        DifferencePair pair = new(fn.Identity, DifferenceStatus.OnlyInA, fn, null);
        return Verify(_emitter.Emit(pair));
    }
}
