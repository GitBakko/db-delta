using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class DependencyOrderingGoldenTests
{
    [Fact]
    public Task Function_referenced_by_computed_column_is_emitted_before_its_table()
    {
        Schema dbo = new("dbo");
        Table customer = new("dbo", "Customer",
            Columns: [new Column("Id", "int", isNullable: false, ordinal: 1)],
            Constraints: [], Indexes: []);
        Function fn = new("dbo", "fnZ",
            "CREATE FUNCTION dbo.fnZ() RETURNS int AS BEGIN RETURN 1 END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);

        Database source = new("Db", Schemas: [dbo], Tables: [customer],
            Views: [], Procedures: [], Functions: [fn], Triggers: [])
        {
            Dependencies =
            [
                new DependencyEdge(
                    new("dbo", "Customer", "Table"),
                    new("dbo", "fnZ", "Function"),
                    EdgeKind.ComputedColumn),
            ],
        };
        Database target = new("Db", Schemas: [dbo], Tables: [],
            Views: [], Procedures: [], Functions: [], Triggers: []);

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            result, selection: null, options: ComparisonOptions.Default,
            dependencies: source.Dependencies);
        return Verify(script);
    }
}
