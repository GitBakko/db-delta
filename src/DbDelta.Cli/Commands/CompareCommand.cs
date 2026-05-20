using System.CommandLine;

namespace DbDelta.Cli.Commands;

/// <summary>
/// `dbdelta compare` — entry point for two-database schema comparison.
/// Wired to the engine in T1.9; this build only validates argument parsing.
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

        Command command = new("compare", "Compare two databases and print the differences")
        {
            source,
            target,
            format
        };

        command.SetAction((_, _) =>
        {
            // T1.9 will wire this to ComparisonEngine.
            return Task.FromResult(ExitCodes.SuccessNoDifferences);
        });

        return command;
    }
}
