using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Spec §1.2 13th object kind — table-type UDT (UDTT). Distinct from alias
/// UDT: this is a typed row set, commonly used as TVPs to stored procedures.
/// </summary>
public class TableTypeUdtTests
{
    private static readonly ScriptGenerator Sut = new();

    [Fact]
    public void OnlyInA_TableType_emits_CREATE_TYPE_AS_TABLE()
    {
        TableTypeUdt udt = new("dbo", "OrderItemTvp",
        [
            new Column("ProductId", "int", isNullable: false, ordinal: 1),
            new Column("Quantity", "int", isNullable: false, ordinal: 2),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInA, udt, null),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE TYPE [dbo].[OrderItemTvp] AS TABLE");
        sql.Should().Contain("[ProductId] [int] NOT NULL");
        sql.Should().Contain("[Quantity] [int] NOT NULL");
    }

    [Fact]
    public void OnlyInB_TableType_emits_DROP_TYPE()
    {
        TableTypeUdt udt = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInB, null, udt),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP TYPE [dbo].[OrderItemTvp]");
    }

    [Fact]
    public void Different_TableType_emits_DROP_then_CREATE_in_correct_order()
    {
        TableTypeUdt src = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "bigint", isNullable: false, ordinal: 1)]);
        TableTypeUdt tgt = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt),
        ]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP TYPE [dbo].[OrderItemTvp]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE TYPE [dbo].[OrderItemTvp] AS TABLE", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
    }

    [Fact]
    public void TableTypes_appear_in_prologue_after_alias_UDTs_and_before_tables()
    {
        UserDefinedType aliasUdt = new("dbo", "ShortDescription", "nvarchar", 200, 0, 0, true);
        TableTypeUdt tt = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "int", isNullable: false, ordinal: 1)]);
        Table table = new("dbo", "Orders",
            [new Column("Id", "int", isNullable: false, ordinal: 1)]);

        ComparisonResult result = new(
        [
            new DifferencePair(aliasUdt.Identity, DifferenceStatus.OnlyInA, aliasUdt, null),
            new DifferencePair(tt.Identity, DifferenceStatus.OnlyInA, tt, null),
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result);

        int aliasIdx = sql.IndexOf("CREATE TYPE [dbo].[ShortDescription]", StringComparison.Ordinal);
        int ttIdx = sql.IndexOf("CREATE TYPE [dbo].[OrderItemTvp] AS TABLE", StringComparison.Ordinal);
        int tableIdx = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);

        aliasIdx.Should().BeGreaterThan(0);
        ttIdx.Should().BeGreaterThan(aliasIdx);
        tableIdx.Should().BeGreaterThan(ttIdx);
    }

    [Fact]
    public void Identical_TableTypes_are_reported_by_engine_with_no_DDL()
    {
        TableTypeUdt udt = new("dbo", "Tvp",
            [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [udt] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [udt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);
        string sql = Sut.Generate(result);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
        sql.Should().NotContain("CREATE TYPE");
        sql.Should().NotContain("DROP TYPE");
    }

    /// <summary>
    /// A table type carries PRIMARY KEY / UNIQUE / CHECK / DEFAULT / IDENTITY
    /// and inline INDEX, and the only way to change one is DROP + CREATE. Every
    /// one of them therefore has to reach the CREATE, or the rebuild silently
    /// drops it — see <c>docs/parity/redgate-2026-08-31.md</c> R1.
    /// </summary>
    /// <remarks>
    /// **None of them may carry a CONSTRAINT clause.** SQL Server rejects a
    /// named constraint inside CREATE TYPE … AS TABLE outright — "Incorrect
    /// syntax near the keyword 'CONSTRAINT'" — so the names the catalog reports
    /// are always its own, and writing one back is not a cosmetic choice but a
    /// syntax error. An inline INDEX is the one exception: its name IS the
    /// user's.
    /// </remarks>
    [Fact]
    public void The_whole_declarable_surface_of_a_table_type_reaches_the_CREATE()
    {
        TableTypeUdt udt = FullSurface();
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInA, udt, null),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("[Id] [int] IDENTITY(1,1) NOT NULL");
        sql.Should().Contain("[Qty] [int] NOT NULL DEFAULT ((0))");
        sql.Should().Contain("PRIMARY KEY CLUSTERED ([Id] ASC, [Qty] DESC)");
        sql.Should().Contain("UNIQUE NONCLUSTERED ([Code] ASC)");
        sql.Should().Contain("CHECK ([Qty]>(0))");
        sql.Should().Contain("INDEX [IX_Note] NONCLUSTERED ([Note] ASC) INCLUDE ([Code])");
        sql.Should().Contain("[Total] AS ([Id]+[Qty])");
    }

    /// <summary>
    /// The negative control for the test above, and the reason it is not merely
    /// cosmetic: a system-minted constraint name may never be written back.
    /// </summary>
    [Fact]
    public void No_constraint_name_is_written_into_a_CREATE_TYPE()
    {
        TableTypeUdt udt = FullSurface();
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInA, udt, null),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().NotContain("CONSTRAINT");
        sql.Should().NotContain("PK__TT_Tvp__A1");
        sql.Should().NotContain("UQ__TT_Tvp__B2");
        sql.Should().NotContain("CK__TT_Tvp__C3");
    }

    /// <summary>
    /// The half that made the loss silent: with the keys outside the model the
    /// two sides compared equal on columns alone, so a re-read after a deploy
    /// that had dropped the key still reported Identical and no second run
    /// could ever repair it.
    /// </summary>
    [Fact]
    public void A_table_type_that_differs_only_by_its_primary_key_is_Different()
    {
        TableTypeUdt src = FullSurface();
        TableTypeUdt tgt = FullSurface() with { Keys = [.. FullSurface().Keys.Where(k => !k.IsPrimaryKey)] };
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [src] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [tgt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }

    /// <summary>
    /// A key column's sort direction is part of the key. SQL Server accepts
    /// <c>PRIMARY KEY (A ASC, B DESC)</c> on a table type — measured — so a
    /// comparison blind to the direction reports Identical and the next rebuild
    /// flattens the index to all-ascending without saying so.
    /// </summary>
    [Fact]
    public void A_table_type_that_differs_only_by_a_key_columns_direction_is_Different()
    {
        TableTypeUdt src = FullSurface();
        TableTypeUdt tgt = FullSurface() with
        {
            Keys =
            [
                .. FullSurface().Keys.Select(k => k.IsPrimaryKey
                    ? k with { KeyColumns = [.. k.KeyColumns.Select(c => c with { IsDescending = false })] }
                    : k),
            ],
        };
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [src] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [tgt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }

    /// <summary>
    /// An inline index's INCLUDE list is part of the index. Found by a mutation
    /// probe: with the reader, the emitter and the round-trip all covering it,
    /// deleting the INCLUDE list from the comparison still left every test
    /// green — two types differing only there would have reported Identical.
    /// </summary>
    [Fact]
    public void A_table_type_that_differs_only_by_an_indexs_INCLUDE_list_is_Different()
    {
        TableTypeUdt src = FullSurface();
        TableTypeUdt tgt = FullSurface() with
        {
            Keys =
            [
                .. FullSurface().Keys.Select(k => k.IncludedColumns.Count == 0
                    ? k
                    : k with { IncludedColumns = [] }),
            ],
        };
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [src] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [tgt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }

    /// <summary>
    /// Negative control: the same keys on both sides stay Identical, so the
    /// comparison above is reacting to the key and not to the system-minted
    /// name, which differs between two servers by construction.
    /// </summary>
    [Fact]
    public void Two_table_types_whose_keys_differ_only_by_their_minted_names_are_Identical()
    {
        TableTypeUdt src = FullSurface();
        TableTypeUdt tgt = FullSurface(nameSuffix: "_OTHER");
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [src] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [tgt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    /// <summary>
    /// Everything SQL Server lets a table type declare, measured on
    /// mssql/server:2022-latest rather than assumed: IDENTITY, DEFAULT,
    /// a computed column, a PRIMARY KEY with a DESC key column, UNIQUE, CHECK,
    /// and an inline INDEX with INCLUDE. A filtered inline index is NOT here
    /// because SQL Server rejects one.
    /// </summary>
    /// <remarks>
    /// The constraint names carry a suffix because a real one always does, and
    /// the two sides of the pairing test have to disagree on them.
    /// </remarks>
    private static TableTypeUdt FullSurface(string nameSuffix = "") => new("dbo", "Tvp",
    [
        new Column("Id", "int", isNullable: false, ordinal: 1,
            isIdentity: true, identitySeed: 1, identityIncrement: 1),
        new Column("Code", "nvarchar(10)", isNullable: false, ordinal: 2),
        new Column("Qty", "int", isNullable: false, ordinal: 3, defaultExpression: "((0))"),
        new Column("Note", "nvarchar(50)", isNullable: true, ordinal: 4),
        new Column("Total", "int", isNullable: true, ordinal: 5, computedExpression: "([Id]+[Qty])"),
    ])
    {
        Keys =
        [
            new TableIndex($"PK__TT_Tvp__A1{nameSuffix}", IsUnique: true, IsClustered: true,
                FilterExpression: null,
                KeyColumns: [new IndexColumn("Id", IsDescending: false), new IndexColumn("Qty", IsDescending: true)],
                IncludedColumns: [])
            {
                IsPrimaryKey = true,
            },
            new TableIndex($"UQ__TT_Tvp__B2{nameSuffix}", IsUnique: true, IsClustered: false,
                FilterExpression: null,
                KeyColumns: [new IndexColumn("Code", IsDescending: false)],
                IncludedColumns: [])
            {
                IsUniqueConstraint = true,
            },
            new TableIndex("IX_Note", IsUnique: false, IsClustered: false, FilterExpression: null,
                KeyColumns: [new IndexColumn("Note", IsDescending: false)],
                IncludedColumns: ["Code"]),
        ],
        CheckConstraints =
        [
            new CheckConstraint($"CK__TT_Tvp__C3{nameSuffix}", "([Qty]>(0))",
                IsDisabled: false, IsNotForReplication: false)
            {
                IsSystemNamed = true,
            },
        ],
    };

    [Fact]
    public void Engine_classifies_TableType_with_different_columns_as_Different()
    {
        TableTypeUdt src = new("dbo", "Tvp",
            [new Column("Id", "bigint", isNullable: false, ordinal: 1)]);
        TableTypeUdt tgt = new("dbo", "Tvp",
            [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [src] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [tgt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }
}
