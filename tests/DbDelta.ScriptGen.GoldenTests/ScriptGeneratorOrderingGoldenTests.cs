using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class ScriptGeneratorOrderingGoldenTests
{
    [Fact]
    public Task Generate_orders_tables_indexes_views_functions_procs_triggers_FKs()
    {
        Schema dbo = new("dbo");
        Table customer = new(
            "dbo",
            "Customer",
            Columns: [new Column("Id", "int", isNullable: false, ordinal: 1)],
            Constraints: [],
            Indexes: []);
        View v = new("dbo", "vCustomer",
            "CREATE VIEW dbo.vCustomer AS SELECT 1 AS Id;", IsEncrypted: false);
        Function fn = new("dbo", "fnList",
            "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        StoredProcedure p = new("dbo", "uspGet",
            "CREATE PROCEDURE dbo.uspGet AS SELECT 1;", IsEncrypted: false);
        Trigger trg = new("dbo", "trgCustomerAudit",
            "CREATE TRIGGER dbo.trgCustomerAudit ON dbo.Customer AFTER INSERT AS SELECT 1;",
            IsEncrypted: false, ParentSchema: "dbo", ParentTable: "Customer",
            IsDisabled: false, IsNotForReplication: false);

        Database source = new("Db", Schemas: [dbo], Tables: [customer],
            Views: [v], Procedures: [p], Functions: [fn], Triggers: [trg]);
        Database target = new("Db", Schemas: [dbo], Tables: [], Views: [], Procedures: [], Functions: [], Triggers: []);

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(result);
        return Verify(script);
    }
}
