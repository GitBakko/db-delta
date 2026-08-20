using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// DbDelta's half of the Redgate parity run, checked before anyone opens
/// Redgate.
/// </summary>
/// <remarks>
/// <para>
/// The fixture under <c>tests/Fixtures/Parity</c> is applied by hand in SSMS
/// for the parity audit itself, which needs a licensed Redgate GUI. What does
/// NOT need one is the question "does DbDelta do what the scenario says it
/// does" — and asking that here means the audit starts from a known-good side
/// rather than discovering our own bugs while diffing someone else's output.
/// </para>
/// <para>
/// The four scenarios added on 2026-08-20 are the ones the audit had never
/// reached: a DROP that has to run in reverse dependency order through a
/// SCHEMABINDING chain, a filtered index, a CHECK that reads another table
/// through a function, and extended properties — which DbDelta declares rather
/// than scripts.
/// </para>
/// </remarks>
[Collection(nameof(LiveDbCollection))]
public class ParityFixtureTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task The_parity_fixture_deploys_and_converges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string src = await ApplyAsync("ParitySrc", "01-source.sql", ct);
        string tgt = await ApplyAsync("ParityTgt", "02-target.sql", ct);

        Database source = (await new LiveDbSource(src, "source").LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt, "target").LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default, dependencies: source.Dependencies);
        // ── 18: the SCHEMABINDING edge is the one the server enforces. The
        //    function binds to the table, so it has to be dropped first or
        //    the DROP TABLE is Msg 3729. The view merely CALLS the function
        //    and is not bound to it, so its own position is free — asserting
        //    view-before-function would have demanded an order SQL Server
        //    does not ask for, and the first run of this test did.
        int function = script.IndexOf("DROP FUNCTION IF EXISTS [dbo].[fnLegacyTotal]", StringComparison.Ordinal);
        int table = script.IndexOf("DROP TABLE [dbo].[LegacyStock]", StringComparison.Ordinal);
        script.Should().Contain("DROP VIEW IF EXISTS [dbo].[vLegacyReport]");
        function.Should().BeGreaterThan(-1, "the function is target-only and has to go");
        function.Should().BeLessThan(table, "a schemabound function goes before the table it binds to");

        // ── 19: the filter is the difference, so it has to reach the CREATE.
        script.Should().Contain("DROP INDEX [IX_Subscriber_Email] ON [dbo].[Subscriber]");
        script.Should().Contain("CREATE NONCLUSTERED INDEX [IX_Subscriber_Email]")
              .And.Contain("WHERE ([IsActive]=(1))");

        // ── 20: a CHECK reading another table through a function is a
        //    cross-object dependency — the function has to exist first.
        int checkFn = script.IndexOf("FUNCTION dbo.fnCreditLimit", StringComparison.Ordinal);
        int checkUse = script.IndexOf("CK_CustomerOrder_WithinLimit", StringComparison.Ordinal);
        checkFn.Should().BeGreaterThan(-1);
        checkFn.Should().BeLessThan(checkUse, "the constraint calls the function");

        // ── 21: DbDelta does not script extended properties. It says so.
        script.Should().NotContain("sp_addextendedproperty");
        diff.Unexamined.Summary.Should().Contain("propriet");

        // ── And the invariant the whole fixture rests on.
        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "the parity script did not apply");

        Database after = (await new LiveDbSource(tgt, "target").LoadAsync(ct)).Value!;
        new ComparisonEngine().Compare(source, after, ComparisonOptions.Default)
            .Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Select(d => $"{d.Identity.Kind} {d.Identity.SchemaName}.{d.Identity.ObjectName} = {d.Status}")
            .Should().BeEmpty("a parity fixture that does not converge measures our bugs, not Redgate's shape");
    }

    [Fact]
    public async Task The_refusal_fixture_refuses()
    {
        // Scenario 22, in its own databases because a refusal stops the whole
        // run: a columnstore in the main fixture would make the other twenty-one
        // unmeasurable.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string src = await ApplyAsync("ParityRefSrc", "03-refusals.sql", ct, section: "RefusalSource");
        string tgt = await ApplyAsync("ParityRefTgt", "03-refusals.sql", ct, section: "RefusalTarget");

        Database source = (await new LiveDbSource(src, "source").LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt, "target").LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        // Read, therefore reported: the difference is visible before it is refused.
        diff.Differences.Single(d => d.Identity.ObjectName == "Metric")
            .Status.Should().Be(DifferenceStatus.Different);

        Action generate = () => new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default, dependencies: source.Dependencies);

        generate.Should().Throw<UnscriptableIndexException>()
                .Which.IndexName.Should().Be("IX_Metric_Columnstore");
    }

    /// <summary>
    /// Creates a database and runs the fixture file into it, minus the
    /// <c>USE</c> statements — the file names the audit's databases and the
    /// container's are named differently.
    /// </summary>
    private async Task<string> ApplyAsync(string db, string file, CancellationToken ct, string? section = null)
    {
        await using (SqlConnection master = new(fixture.ConnectionString))
        {
            await master.OpenAsync(ct);
            await Exec(master, $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", ct);
        }

        string conn = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = db }.ConnectionString;
        string sql = await File.ReadAllTextAsync(Path.Combine(FixtureDirectory(), file), ct);
        if (section is not null) { sql = SectionFor(sql, section); }

        SqlBatchResult applied = await SqlExecutor.ExecuteAsync(conn, StripUse(sql), ct, useOwnTransaction: false);
        applied.Success.Should().BeTrue(applied.ErrorMessage ?? $"{file} did not apply to {db}");
        return conn;
    }

    /// <summary>The half of the refusal file that follows its own USE.</summary>
    private static string SectionFor(string sql, string section)
    {
        int start = sql.IndexOf($"USE [DbDeltaParity_{section}];", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"the fixture has to carry a {section} section");
        int next = sql.IndexOf("USE [DbDeltaParity_", start + 1, StringComparison.Ordinal);
        return next < 0 ? sql[start..] : sql[start..next];
    }

    private static string StripUse(string sql) =>
        string.Join('\n', sql.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("USE [", StringComparison.OrdinalIgnoreCase)));

    /// <summary>bin/&lt;cfg&gt;/net10.0 → repo root → tests/Fixtures/Parity.</summary>
    private static string FixtureDirectory() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "Fixtures", "Parity"));

    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
