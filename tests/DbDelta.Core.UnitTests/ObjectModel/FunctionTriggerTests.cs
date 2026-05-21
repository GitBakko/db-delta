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

    [Fact]
    public void Trigger_identity_uses_Trigger_kind_and_carries_parent_table()
    {
        Trigger trg = new(
            Schema: "dbo",
            Name: "trg_Customer_Audit",
            Body: "CREATE TRIGGER dbo.trg_Customer_Audit ON dbo.Customer AFTER INSERT AS BEGIN INSERT INTO dbo.Audit DEFAULT VALUES; END",
            IsEncrypted: false,
            ParentSchema: "dbo",
            ParentTable: "Customer",
            IsDisabled: false,
            IsNotForReplication: false);
        trg.Identity.SchemaName.Should().Be("dbo");
        trg.Identity.ObjectName.Should().Be("trg_Customer_Audit");
        trg.Identity.Kind.Should().Be("Trigger");
        trg.ParentSchema.Should().Be("dbo");
        trg.ParentTable.Should().Be("Customer");
        trg.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public void Disabled_trigger_carries_the_flag()
    {
        Trigger trg = new("dbo", "trg", "BODY", IsEncrypted: false,
            ParentSchema: "dbo", ParentTable: "T",
            IsDisabled: true, IsNotForReplication: false);
        trg.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public void Trigger_records_have_value_equality()
    {
        Trigger a = new("dbo", "trg", "BODY", false, "dbo", "T", false, false);
        Trigger b = new("dbo", "trg", "BODY", false, "dbo", "T", false, false);
        a.Should().Be(b);
    }

    [Fact]
    public void Database_carries_functions_and_triggers_collections()
    {
        Schema dbo = new("dbo");
        Function fn = new("dbo", "fnA", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        Trigger trg = new("dbo", "trgA", "BODY", IsEncrypted: false,
            ParentSchema: "dbo", ParentTable: "T", IsDisabled: false, IsNotForReplication: false);
        Database db = new("Db", Schemas: [dbo], Tables: [], Views: [], Procedures: [],
            Functions: [fn], Triggers: [trg]);
        db.Functions.Should().ContainSingle().Which.Should().Be(fn);
        db.Triggers.Should().ContainSingle().Which.Should().Be(trg);
    }

    [Fact]
    public void Database_defaults_functions_and_triggers_to_empty()
    {
        Schema dbo = new("dbo");
        Database db = new("Db", Schemas: [dbo], Tables: []);
        db.Functions.Should().BeEmpty();
        db.Triggers.Should().BeEmpty();
    }
}
