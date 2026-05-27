using System.CommandLine;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.Reports;
using DbDelta.Providers.LiveDb;
using DbDelta.Shared.Reports;

namespace DbDelta.Cli.Commands;

/// <summary>
/// <c>dbdelta report</c> — load source/target, compare, and emit an HTML
/// and/or JSON report to disk. At least one of <c>--html</c> / <c>--json</c>
/// is required; both can be supplied at once.
/// </summary>
internal static class ReportCommand
{
    public static Command Build()
    {
        Option<string> source = new("--source")
        {
            Description = "Source SQL Server connection string",
            Required = true
        };
        Option<string> target = new("--target")
        {
            Description = "Target SQL Server connection string",
            Required = true
        };
        Option<string?> htmlPath = new("--html")
        {
            Description = "Output path for the self-contained HTML report"
        };
        Option<string?> jsonPath = new("--json")
        {
            Description = "Output path for the JSON report"
        };

        Command command = new("report", "Run a comparison and write an HTML and/or JSON report to disk")
        {
            source,
            target,
            htmlPath,
            jsonPath
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string srcConn = parseResult.GetValue(source)!;
            string tgtConn = parseResult.GetValue(target)!;
            string? html = parseResult.GetValue(htmlPath);
            string? json = parseResult.GetValue(jsonPath);

            if (string.IsNullOrWhiteSpace(html) && string.IsNullOrWhiteSpace(json))
            {
                Console.Error.WriteLine(/*lang=json,strict*/ "{\"code\":\"missing_output\",\"message\":\"At least one of --html or --json must be specified.\",\"remediation\":\"Pass --html=<path> and/or --json=<path>.\"}");
                return ExitCodes.InternalError;
            }

            LiveDbSource srcSource = new(srcConn, "source");
            LiveDbSource tgtSource = new(tgtConn, "target");

            Result<Database> srcResult = await srcSource.LoadAsync(ct);
            if (!srcResult.IsSuccess)
            {
                CliErrorMapper.WriteError(srcResult.Error!);
                return CliErrorMapper.MapErrorToExitCode(srcResult.Error!);
            }

            Result<Database> tgtResult = await tgtSource.LoadAsync(ct);
            if (!tgtResult.IsSuccess)
            {
                CliErrorMapper.WriteError(tgtResult.Error!);
                return CliErrorMapper.MapErrorToExitCode(tgtResult.Error!);
            }

            ComparisonResult comparison = new ComparisonEngine()
                .Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default);

            if (!string.IsNullOrWhiteSpace(html))
            {
                string htmlText = new HtmlReportGenerator().Generate(comparison);
                EnsureParentDirectoryExists(html);
                await File.WriteAllTextAsync(html, htmlText, ct);
            }
            if (!string.IsNullOrWhiteSpace(json))
            {
                string jsonText = new JsonReportGenerator().Generate(comparison);
                EnsureParentDirectoryExists(json);
                await File.WriteAllTextAsync(json, jsonText, ct);
            }

            bool hasDifferences = comparison.Differences
                .Any(d => d.Status is DifferenceStatus.Different
                                   or DifferenceStatus.OnlyInA
                                   or DifferenceStatus.OnlyInB);
            return hasDifferences
                ? ExitCodes.SuccessDifferencesFound
                : ExitCodes.SuccessNoDifferences;
        });

        return command;
    }

    private static void EnsureParentDirectoryExists(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

}
