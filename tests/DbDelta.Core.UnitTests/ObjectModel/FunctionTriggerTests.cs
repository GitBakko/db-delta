// tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class FunctionTriggerTests
{
    [Fact]
    public void Scalar_function_identity_uses_Function_kind()
    {
        Function fn = new("dbo", "fnSum", "CREATE FUNCTION dbo.fnSum() RETURNS int AS BEGIN RETURN 1; END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        fn.Identity.SchemaName.Should().Be("dbo");
        fn.Identity.ObjectName.Should().Be("fnSum");
        fn.Identity.Kind.Should().Be("Function");
        fn.FunctionKind.Should().Be(FunctionKind.Scalar);
    }

    [Fact]
    public void Inline_TVF_function_kind_is_inline_table_valued()
    {
        Function fn = new("dbo", "fnList", "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        fn.FunctionKind.Should().Be(FunctionKind.InlineTableValued);
    }

    [Fact]
    public void Multi_statement_TVF_function_kind_set()
    {
        Function fn = new("dbo", "fnRows", "CREATE FUNCTION dbo.fnRows() RETURNS @T TABLE(X int) AS BEGIN INSERT INTO @T VALUES (1); RETURN; END",
            IsEncrypted: false, FunctionKind: FunctionKind.MultiStatementTableValued);
        fn.FunctionKind.Should().Be(FunctionKind.MultiStatementTableValued);
    }

    [Fact]
    public void Function_records_have_value_equality()
    {
        Function a = new("dbo", "fnA", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        Function b = new("dbo", "fnA", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        a.Should().Be(b);
    }
}
