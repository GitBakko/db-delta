using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DbDelta.Cli.AcceptanceTests;

[Collection(nameof(CliCollection))]
public class ReportCommandTests(CliFixture fixture)
{
    [Fact]
    public async Task Writes_html_file_when_only_html_is_requested()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaReportHtmlSrc";
        const string tgtDb = "DbDeltaReportHtmlTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerTable(srcDb, ct);

        using TempFile htmlOut = TempFile.Html();
        int exit = await RunCli(["report",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--html", htmlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
        File.Exists(htmlOut.Path).Should().BeTrue();
        string content = await File.ReadAllTextAsync(htmlOut.Path, ct);
        content.Should().StartWith("<!DOCTYPE html>");
        content.Should().Contain("Tabelle");
        content.Should().Contain("Customer");
    }

    [Fact]
    public async Task Writes_json_file_with_camelCase_differences_array_when_only_json_is_requested()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaReportJsonSrc";
        const string tgtDb = "DbDeltaReportJsonTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerTable(srcDb, ct);

        using TempFile jsonOut = TempFile.Json();
        int exit = await RunCli(["report",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--json", jsonOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
        File.Exists(jsonOut.Path).Should().BeTrue();
        string content = await File.ReadAllTextAsync(jsonOut.Path, ct);
        using JsonDocument doc = JsonDocument.Parse(content);
        JsonElement differences = doc.RootElement.GetProperty("differences");
        differences.GetArrayLength().Should().BeGreaterThan(0);
        differences[0].GetProperty("kind").GetString().Should().NotBeNull();
        differences[0].GetProperty("schemaName").GetString().Should().NotBeNull();
        differences[0].GetProperty("objectName").GetString().Should().NotBeNull();
        differences[0].GetProperty("status").GetString().Should().NotBeNull();
    }

    [Fact]
    public async Task Writes_both_files_when_html_and_json_are_requested_together()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaReportBothSrc";
        const string tgtDb = "DbDeltaReportBothTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);

        using TempFile htmlOut = TempFile.Html();
        using TempFile jsonOut = TempFile.Json();
        int exit = await RunCli(["report",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--html", htmlOut.Path,
            "--json", jsonOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        File.Exists(htmlOut.Path).Should().BeTrue();
        File.Exists(jsonOut.Path).Should().BeTrue();
    }

    [Fact]
    public async Task Returns_internal_error_when_neither_html_nor_json_path_is_supplied()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaReportNoOutSrc";
        const string tgtDb = "DbDeltaReportNoOutTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);

        int exit = await RunCli(["report",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb)], ct);

        exit.Should().Be(ExpectedExitCodes.InternalError);
    }

    private string ConnectionFor(string db) => CliRunner.ConnectionFor(fixture.ConnectionString, db);
    private Task CreateDb(string db, CancellationToken ct) => CliRunner.CreateDb(fixture.ConnectionString, db, ct);
    private Task CreateCustomerTable(string db, CancellationToken ct) => CliRunner.CreateCustomerTable(fixture.ConnectionString, db, ct);
    private static Task<int> RunCli(string[] args, CancellationToken ct) => CliRunner.Run(args, ct);
}

/// <summary>
/// Auto-cleaning scratch path for an output report file produced by the CLI.
/// </summary>
internal sealed class TempFile : IDisposable
{
    public string Path { get; }
    private TempFile(string path) { Path = path; }
    public static TempFile Html() => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dbdelta-{Guid.NewGuid():N}.html"));
    public static TempFile Json() => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dbdelta-{Guid.NewGuid():N}.json"));

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            try { File.Delete(Path); } catch { /* best effort cleanup */ }
        }
    }
}
