using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// Every value <see cref="BackfillRequirement.SuggestedValue"/> can produce has
/// to be one SQL Server actually accepts as the DEFAULT of a column of that
/// type, added NOT NULL to a table that already has rows.
/// </summary>
/// <remarks>
/// The unit tests pin the mapping; only a server can say whether the mapping is
/// right. The defect that opened this — one <c>_ =&gt; "('')"</c> arm for every
/// unrecognised type — was invisible to a unit test by construction, because a
/// unit test asserts the string the switch returns, and the string was exactly
/// what someone had decided it should be. What no unit test could say is that
/// the server refuses <c>('')</c> for <c>varbinary</c>, <c>hierarchyid</c> and
/// the two spatial types, and that for an alias over <c>bigint</c> it does NOT
/// refuse it — it stores <c>0</c>, silently.
/// <para>
/// It also guards the shape of the four expressions that are not literals:
/// a typo in <c>hierarchyid::GetRoot()</c> or <c>geometry::Parse</c> compiles,
/// passes every unit test, and fails on the operator's deploy.
/// </para>
/// </remarks>
[Collection(nameof(LiveDbCollection))]
public class BackfillSuggestionTests(LiveDbFixture fixture)
{
    private const string DbName = "DbDeltaBackfill";

    /// <summary>
    /// The declared type, and the type text the model would carry for it. They
    /// differ only where the catalog spells the type differently from the DDL.
    /// </summary>
    public static TheoryData<string, string> Types => new()
    {
        { "bit", "bit" },
        { "int", "int" },
        { "bigint", "bigint" },
        { "decimal(9,2)", "decimal(9,2)" },
        { "money", "money" },
        { "float", "float" },
        { "date", "date" },
        { "datetime2", "datetime2" },
        { "datetimeoffset", "datetimeoffset" },
        { "time", "time" },
        { "uniqueidentifier", "uniqueidentifier" },
        { "nvarchar(20)", "nvarchar(20)" },
        { "varchar(20)", "varchar(20)" },
        { "char(4)", "char(4)" },
        { "sysname", "sysname" },
        { "xml", "xml" },
        { "sql_variant", "sql_variant" },
        // Every row below was answered ('') before, and every one of them is
        // refused by the server with that answer.
        { "binary(4)", "binary(4)" },
        { "varbinary(20)", "varbinary(20)" },
        { "varbinary(max)", "varbinary(max)" },
        { "image", "image" },
        { "hierarchyid", "hierarchyid" },
        { "geometry", "geometry" },
        { "geography", "geography" },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public async Task The_suggested_default_is_accepted_by_the_server(string ddlType, string modelType)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string suggestion = new BackfillRequirement("dbo", "T", "C", modelType).SuggestedValue;
        suggestion.Should().NotBeEmpty("every type in this table is one DbDelta claims to have a dull value for");

        await AddColumnAsync(ddlType, suggestion, ct);
    }

    /// <summary>
    /// The alias case, end to end: the suggestion is chosen from the BASE type
    /// and has to land on a column declared with the ALIAS.
    /// </summary>
    /// <remarks>
    /// <c>('')</c> here does not fail — it stores 0 — so this test would have
    /// stayed green against the old code. What it pins is that the value now
    /// offered for an alias over <c>bigint</c> is <c>((0))</c> and that the
    /// server takes it on a column of that alias.
    /// </remarks>
    [Theory]
    [InlineData("bigint", "((0))")]
    [InlineData("uniqueidentifier", "(NEWID())")]
    [InlineData("varbinary(20)", "(0x)")]
    [InlineData("decimal(9,2)", "((0))")]
    public async Task An_alias_column_takes_the_default_chosen_for_its_base_type(string baseType, string expected)
    {
        ArgumentNullException.ThrowIfNull(baseType);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string typeName = "Alias_" + baseType.Replace("(", "_").Replace(")", "").Replace(",", "_");
        string dbConn = await EnsureDbAsync(ct);

        await using SqlConnection c = new(dbConn);
        await c.OpenAsync(ct);
        await ExecAsync(c, $"IF TYPE_ID('dbo.{typeName}') IS NULL CREATE TYPE dbo.{typeName} FROM {baseType};", ct);

        // The suggestion is driven by BaseType, exactly as BackfillPreflight.Scan
        // fills it after resolving the alias against the comparison's UDT pairs.
        string suggestion = new BackfillRequirement("dbo", "T", "C", typeName, baseType).SuggestedValue;
        suggestion.Should().Be(expected);

        await AddColumnAsync($"dbo.{typeName}", suggestion, ct);
    }

    /// <summary>
    /// A rowversion column is added NOT NULL to a populated table with no
    /// DEFAULT at all — which is why the preflight must never ask about one.
    /// </summary>
    [Fact]
    public async Task A_rowversion_column_needs_no_default_at_all()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await EnsureDbAsync(ct);
        string table = "RV_" + Guid.NewGuid().ToString("N")[..8];

        await using SqlConnection c = new(dbConn);
        await c.OpenAsync(ct);
        await ExecAsync(c, $"CREATE TABLE dbo.[{table}] (Id int NOT NULL); INSERT dbo.[{table}] VALUES (1);", ct);

        // No DEFAULT clause. If this ever starts failing, the filter in
        // ColumnsNeedingABackfillDefault is wrong and the operator must be asked.
        await ExecAsync(c, $"ALTER TABLE dbo.[{table}] ADD Ver rowversion NOT NULL;", ct);

        await using SqlCommand cmd = new($"SELECT COUNT(*) FROM dbo.[{table}] WHERE Ver IS NOT NULL;", c);
        (await cmd.ExecuteScalarAsync(ct)).Should().Be(1, "the server fills a rowversion for the existing row");
    }

    private async Task AddColumnAsync(string ddlType, string suggestion, CancellationToken ct)
    {
        string dbConn = await EnsureDbAsync(ct);
        string table = "T_" + Guid.NewGuid().ToString("N")[..8];

        await using SqlConnection c = new(dbConn);
        await c.OpenAsync(ct);
        // A row already in the table is the whole problem: without one, Msg 4901
        // never fires and the DEFAULT is never evaluated.
        await ExecAsync(c, $"CREATE TABLE dbo.[{table}] (Id int NOT NULL); INSERT dbo.[{table}] VALUES (1);", ct);

        // The exact shape TableScriptEmitter emits: a named throwaway constraint
        // carrying the value, dropped immediately after.
        await ExecAsync(
            c,
            $"ALTER TABLE dbo.[{table}] ADD C {ddlType} NOT NULL "
            + $"CONSTRAINT [DF__dbdelta_backfill__C] DEFAULT {suggestion};"
            + $"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [DF__dbdelta_backfill__C];",
            ct);

        await using SqlCommand cmd = new($"SELECT COUNT(*) FROM dbo.[{table}] WHERE C IS NOT NULL;", c);
        (await cmd.ExecuteScalarAsync(ct)).Should().Be(1, "the existing row must have been seeded");
    }

    private async Task<string> EnsureDbAsync(CancellationToken ct)
    {
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, $"IF DB_ID('{DbName}') IS NULL CREATE DATABASE {DbName};", ct);
        }

        return new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = DbName
        }.ConnectionString;
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
