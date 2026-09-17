using System.CommandLine;
using DbDelta.Cli.Output;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Providers.LiveDb;

namespace DbDelta.Cli.Commands;

/// <summary>
/// `dbdelta compare` — load source/target via <see cref="LiveDbSource"/>, run
/// <see cref="ComparisonEngine"/>, emit text or JSON, return spec §4.3 exit code.
/// </summary>
internal static class CompareCommand
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
        Option<string> format = new("--format")
        {
            Description = "Output format: text | json",
            DefaultValueFactory = _ => "text"
        };

        Option<string[]> exclude = ExcludeOption.Create();

        Command command = new("compare", "Compare two databases and print the differences")
        {
            source,
            target,
            format,
            exclude
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string srcConn = parseResult.GetValue(source)!;
            string tgtConn = parseResult.GetValue(target)!;
            string fmt = parseResult.GetValue(format) ?? "text";

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

            ComparisonResult comparison = ExcludeOption.Apply(
                new ComparisonEngine().Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default),
                parseResult.GetValue(exclude));

            string output = fmt.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? JsonFormatter.Format(comparison)
                : TextFormatter.Format(comparison);

            Console.Out.WriteLine(output);

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
}
