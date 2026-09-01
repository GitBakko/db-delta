using System.Diagnostics;
using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// The two things a big or slow catalog was needed to say, and nothing else had
/// ever said: how far the 300 s read timeout actually is from being reached,
/// and what a read cancelled in flight really does.
/// </summary>
/// <remarks>
/// <para>
/// Both answers came back different from the entries that asked for them, so
/// this summary is written from the measurement and not from the expectation
/// that preceded it.
/// </para>
/// <para>
/// A cancelled read RAISES — <c>TaskCanceledException</c> straight out of
/// <c>TableReader</c> — it does not come back as a <c>CannotConnect</c> result.
/// <c>SqlException -2</c> is what a TIMEOUT gives, and the two had been
/// conflated in a comment nothing tested.
/// </para>
/// <para>
/// And SIZE cannot reach the 300 s bound: 2000 tables / 30000 columns / 6000
/// indexes / 500 views read in about three seconds, with no single command near
/// a second. What reaches the bound is a read blocked behind someone else's
/// schema lock — which is exactly what <c>ConnectionFactory</c>'s doc-comment
/// always said the bound was for, and what nothing had ever exercised.
/// </para>
/// <para>
/// Sizing: the seed is deliberately modest so CI pays seconds, not a minute.
/// The first assertion is a SHAPE — it finishes, far inside the bound — not a
/// benchmark, because a CI runner's timings are not a server's.
/// </para>
/// </remarks>
[Collection(nameof(LiveDbCollection))]
public class LargeCatalogTests(LiveDbFixture fixture)
{
    private const int Tables = 300;
    private const string DbName = "DbDeltaLarge";

    [Fact]
    public async Task A_catalog_read_finishes_far_inside_the_command_timeout()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await SeedAsync(ct);

        var sw = Stopwatch.StartNew();
        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        sw.Stop();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Tables.Should().HaveCount(Tables);

        // Not a benchmark — a ceiling with two orders of magnitude of room. If
        // this ever fails, something has become quadratic in the object count.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60),
            $"reading {Tables} tables took {sw.ElapsedMilliseconds} ms and the read timeout is "
            + $"{ConnectionFactory.ReadCommandTimeoutSeconds} s");
    }

    /// <summary>
    /// Cancelling a read in flight RAISES. It does not come back as a failure,
    /// whatever the comment in AppStateViewModel says.
    /// </summary>
    /// <remarks>
    /// MEASURED, and it corrects a claim that had been sitting in the code with
    /// no test under it: "the driver reports an aborted command as SqlException
    /// -2, which LiveDbSource turns into a CannotConnect Result". Not for a
    /// cancellation it does not — Microsoft.Data.SqlClient honours the token on
    /// ReadAsync and throws TaskCanceledException, which is an
    /// OperationCanceledException and matches none of LoadAsync's SqlException
    /// filters, so it flies straight out. -2 is what a TIMEOUT gives, which the
    /// test below covers separately. The two were conflated.
    /// </remarks>
    [Fact]
    public async Task A_read_cancelled_in_flight_raises_rather_than_returning_a_failure()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await SeedAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Long enough to be inside a reader, short enough that the read is not
        // done. The seed above is what makes that window exist at all.
        cts.CancelAfter(TimeSpan.FromMilliseconds(40));

        Func<Task> read = async () => await new LiveDbSource(conn).LoadAsync(cts.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
        cts.Token.IsCancellationRequested.Should().BeTrue("the window was too small to prove anything otherwise");
    }

    /// <summary>
    /// A read that runs out of time comes back as a failure naming the timeout —
    /// and what makes it run out is a LOCK, not a big catalog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the path SqlException -2 actually takes, and it is the scenario
    /// <c>ConnectionFactory</c>'s own doc-comment gives as the reason for having
    /// a bound at all: "a read blocked behind someone else's schema lock has to
    /// end by itself, because nothing else would end it". Nothing had ever
    /// exercised it.
    /// </para>
    /// <para>
    /// MEASURED, and it settles the backlog entry the other way round from how
    /// it was framed: SIZE cannot reach the 300 s bound. A catalog of 2000
    /// tables / 30000 columns / 6000 indexes / 500 views reads in about three
    /// seconds — two orders of magnitude of headroom — and no single command
    /// comes near a second. A held Sch-M lock reaches it in one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_read_blocked_behind_a_schema_lock_ends_by_itself_and_says_why()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await SeedAsync(ct);

        // Another session holds Sch-M on a table the catalog read has to see.
        await using SqlConnection blocker = new(conn);
        await blocker.OpenAsync(ct);
        await ExecAsync(blocker, "BEGIN TRANSACTION; ALTER TABLE dbo.T1 ADD ZZZ_Blocker int NULL;", ct);
        try
        {
            string tight = new SqlConnectionStringBuilder(conn) { CommandTimeout = 1 }.ConnectionString;

            Result<Database> result = await new LiveDbSource(tight).LoadAsync(ct);

            result.IsSuccess.Should().BeFalse("the read is blocked and the bound is what ends it");
            result.Error!.Code.Should().Be(ErrorCode.CannotConnect,
                "-2 is both 'cannot reach the server' and 'the query ran out of time'");
            result.Error!.Remediation.Should().Contain("Command Timeout",
                "the remediation has to name the way out, or the user goes to the firewall over a blocked read");
        }
        finally
        {
            await ExecAsync(blocker, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", ct);
        }
    }

    private async Task<string> SeedAsync(CancellationToken ct)
    {
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, $"IF DB_ID('{DbName}') IS NULL CREATE DATABASE {DbName};", ct);
        }

        string conn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = DbName
        }.ConnectionString;

        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await ExecAsync(c, $"""
            SET NOCOUNT ON;
            IF OBJECT_ID('dbo.T1','U') IS NOT NULL RETURN;
            DECLARE @i int = 1, @sql nvarchar(max);
            WHILE @i <= {Tables}
            BEGIN
                SET @sql = N'CREATE TABLE dbo.T' + CAST(@i AS nvarchar(9)) + N' ('
                         + N'Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, '
                         + N'A nvarchar(50) NOT NULL, B nvarchar(50) NULL, C int NOT NULL, '
                         + N'D decimal(9,2) NULL, E datetime2 NULL, F bit NOT NULL DEFAULT(0), '
                         + N'G uniqueidentifier NULL, H varchar(20) NULL, I bigint NULL);';
                EXEC sp_executesql @sql;
                SET @sql = N'CREATE INDEX IX_A_' + CAST(@i AS nvarchar(9)) + N' ON dbo.T'
                         + CAST(@i AS nvarchar(9)) + N' (A) INCLUDE (B, C);';
                EXEC sp_executesql @sql;
                IF @i % 4 = 0
                BEGIN
                    SET @sql = N'CREATE VIEW dbo.V' + CAST(@i AS nvarchar(9))
                             + N' AS SELECT Id, A, C FROM dbo.T' + CAST(@i AS nvarchar(9)) + N';';
                    EXEC sp_executesql @sql;
                END
                SET @i += 1;
            END
            """, ct);

        return conn;
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
