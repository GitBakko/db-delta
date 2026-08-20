using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// C3 — a connection that can only read part of the catalog must be refused,
/// because everything it cannot see is scripted as a DROP against the other
/// endpoint. The last test is the other half of the contract: a genuinely
/// least-privilege login that holds the grants we ask for must actually work,
/// otherwise the guard would make read-only accounts unusable and the
/// remediation message would be a lie.
/// </summary>
[Collection(nameof(LiveDbCollection))]
public class CatalogVisibilityTests(LiveDbFixture fixture)
{
    private const string Password = "Pr0be!Pass_9x";

    /// <summary>The grants a probe login is created with.</summary>
    [Flags]
    private enum Grants
    {
        None = 0,
        ViewDefinition = 1,
        SelectOnDependencies = 2,
    }

    [Fact]
    public async Task A_login_that_can_only_see_some_tables_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string login = "dbdelta_probe_blind";
        string dbConn = await SetUpProbeDatabaseAsync("VisibilityBlind", login, Grants.None, ct);

        // The mechanism this guard exists for: SQL Server itself hides the row.
        // Without this the test would only prove that we refuse *something*.
        (await CountVisibleTablesAsync(dbConn, ct)).Should().Be(
            1,
            "sys.tables is filtered by metadata visibility, so the login sees only what it holds SELECT on");

        Result<Database> result = await new LiveDbSource(dbConn, "source").LoadAsync(ct);

        result.IsSuccess.Should().BeFalse("a partial catalog turns every hidden object into a DROP");
        result.Error!.Code.Should().Be(ErrorCode.InsufficientPermissions);
        result.Error.Message.Should().Contain("source").And.Contain(login);
        result.Error.Remediation.Should().Contain("GRANT VIEW DEFINITION");
    }

    /// <summary>
    /// VIEW DEFINITION lifts the metadata filter but does NOT grant SELECT on
    /// sys.sql_expression_dependencies, which ships granted to db_owner alone.
    /// Before the preflight covered it, such a login got all the way to the last
    /// reader and failed with a raw "SELECT permission was denied on the object
    /// 'sql_expression_dependencies'" — and our own remediation text told users
    /// to create exactly this account.
    /// </summary>
    [Fact]
    public async Task A_login_that_can_see_everything_but_not_the_dependencies_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string login = "dbdelta_probe_nodeps";
        string dbConn = await SetUpProbeDatabaseAsync("VisibilityNoDeps", login, Grants.ViewDefinition, ct);

        (await CountVisibleTablesAsync(dbConn, ct)).Should().Be(2, "VIEW DEFINITION lifts the metadata filter");

        Result<Database> result = await new LiveDbSource(dbConn, "target").LoadAsync(ct);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCode.InsufficientPermissions);
        result.Error.Message.Should().Contain("sys.sql_expression_dependencies");
        result.Error.Remediation.Should().Contain("GRANT SELECT ON sys.sql_expression_dependencies");
    }

    [Fact]
    public async Task A_read_only_login_holding_the_grants_we_ask_for_loads_the_whole_catalog()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string login = "dbdelta_probe_reader";
        string dbConn = await SetUpProbeDatabaseAsync(
            "VisibilityReader",
            login,
            Grants.ViewDefinition | Grants.SelectOnDependencies,
            ct);

        Result<Database> result = await new LiveDbSource(dbConn, "source").LoadAsync(ct);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Tables.Select(t => t.Name).Should().BeEquivalentTo(["Visible", "Hidden"]);
    }

    /// <summary>
    /// The reader half of the hidden-login rule, against a server that really
    /// applies metadata visibility. <c>sys.server_principals</c> shows a
    /// least-privilege login its own row and nothing else, so every OTHER user
    /// in the database comes back with a NULL login name. Read that NULL as
    /// "no login" and the same database compares Different against itself,
    /// user by user, and the script drops and re-creates principals that were
    /// already correct.
    /// </summary>
    [Fact]
    public async Task A_login_name_hidden_from_the_reader_does_not_make_the_user_different()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string login = "dbdelta_probe_logins";
        const string otherLogin = "dbdelta_other_login";
        const string otherUser = "dbdelta_other_user";
        string dbConn = await SetUpProbeDatabaseAsync(
            "VisibilityLogins",
            login,
            Grants.ViewDefinition | Grants.SelectOnDependencies,
            ct);
        string ownerConn = WithCatalog(fixture.ConnectionString, "VisibilityLogins");

        await using (SqlConnection master = new(fixture.ConnectionString))
        {
            await master.OpenAsync(ct);
            await Exec(master, $"IF SUSER_ID('{otherLogin}') IS NULL CREATE LOGIN {otherLogin} WITH PASSWORD = '{Password}';", ct);
        }
        await using (SqlConnection db = new(ownerConn))
        {
            await db.OpenAsync(ct);
            await Exec(db, $"IF DATABASE_PRINCIPAL_ID('{otherUser}') IS NULL CREATE USER {otherUser} FOR LOGIN {otherLogin};", ct);
        }

        // The mechanism this rule exists for: SQL Server itself blanks the name.
        // Without this the test would pass just as well on a server that showed
        // the probe every login, and would prove nothing about the rule.
        (await ReadLoginNameAsync(dbConn, otherUser, ct)).Should().BeNull(
            "sys.server_principals is filtered by metadata visibility, so the probe sees only its own login");
        (await ReadLoginNameAsync(ownerConn, otherUser, ct)).Should().Be(otherLogin);

        Result<Database> asOwner = await new LiveDbSource(ownerConn, "source").LoadAsync(ct);
        Result<Database> asProbe = await new LiveDbSource(dbConn, "target").LoadAsync(ct);

        asOwner.IsSuccess.Should().BeTrue(asOwner.Error?.Message);
        asProbe.IsSuccess.Should().BeTrue(asProbe.Error?.Message);
        asProbe.Value!.Users.Single(u => u.Name == otherUser).LoginNameIsHidden.Should().BeTrue(
            "authentication_type still says the user is mapped to an instance login");

        ComparisonResult r = new ComparisonEngine().Compare(
            asOwner.Value!, asProbe.Value!, ComparisonOptions.Default);

        r.Differences.Where(p => p.Identity.Kind == "User")
            .Should().OnlyContain(p => p.Status == DifferenceStatus.Identical)
            .And.Contain(p => p.Identity.ObjectName == otherUser);
    }

    private static async Task<string?> ReadLoginNameAsync(
        string connectionString, string user, CancellationToken ct)
    {
        await using SqlConnection c = new(connectionString);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            """
            SELECT sp.name
            FROM sys.database_principals AS p
            LEFT JOIN sys.server_principals AS sp ON sp.sid = p.sid
            WHERE p.name = @name;
            """, c);
        cmd.Parameters.AddWithValue("@name", user);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Creates a database with two tables and a login holding SELECT on exactly
    /// one of them, plus whichever of the preflight's grants
    /// <paramref name="grants"/> asks for. Returns that login's connection
    /// string.
    /// </summary>
    private async Task<string> SetUpProbeDatabaseAsync(
        string database,
        string login,
        Grants grants,
        CancellationToken ct)
    {
        await using (SqlConnection master = new(fixture.ConnectionString))
        {
            await master.OpenAsync(ct);
            await Exec(master, $"IF DB_ID('{database}') IS NULL CREATE DATABASE {database};", ct);
            await Exec(master, $"IF SUSER_ID('{login}') IS NULL CREATE LOGIN {login} WITH PASSWORD = '{Password}';", ct);
        }

        await using (SqlConnection db = new(WithCatalog(fixture.ConnectionString, database)))
        {
            await db.OpenAsync(ct);
            await Exec(db, "IF OBJECT_ID('dbo.Visible', 'U') IS NULL CREATE TABLE dbo.Visible (Id int NOT NULL);", ct);
            await Exec(db, "IF OBJECT_ID('dbo.Hidden', 'U') IS NULL CREATE TABLE dbo.Hidden (Id int NOT NULL);", ct);
            await Exec(db, $"IF DATABASE_PRINCIPAL_ID('{login}') IS NULL CREATE USER {login} FOR LOGIN {login};", ct);
            await Exec(db, $"GRANT SELECT ON dbo.Visible TO {login};", ct);
            if (grants.HasFlag(Grants.ViewDefinition))
            {
                await Exec(db, $"GRANT VIEW DEFINITION TO {login};", ct);
            }
            if (grants.HasFlag(Grants.SelectOnDependencies))
            {
                await Exec(db, $"GRANT SELECT ON sys.sql_expression_dependencies TO {login};", ct);
            }
        }

        return new SqlConnectionStringBuilder(WithCatalog(fixture.ConnectionString, database))
        {
            UserID = login,
            Password = Password,
            IntegratedSecurity = false,
        }.ConnectionString;
    }

    private static string WithCatalog(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;

    private static async Task<int> CountVisibleTablesAsync(string connectionString, CancellationToken ct)
    {
        await using SqlConnection c = new(connectionString);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;", c);
        return (int)await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
