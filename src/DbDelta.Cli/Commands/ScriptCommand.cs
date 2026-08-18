using System.CommandLine;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using DbDelta.Providers.LiveDb;

namespace DbDelta.Cli.Commands;

/// <summary>
/// <c>dbdelta script</c> — load source / target, compare, generate the
/// migration script via <see cref="ScriptGenerator"/>, and write it to the
/// chosen output (file path or <c>-</c> for stdout). The script verb
/// does not touch the target — that is what <see cref="ApplyCommand"/> is for.
/// </summary>
internal static class ScriptCommand
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
        Option<string> outPath = new("--out")
        {
            Description = "Output path for the generated script. Use \"-\" (default) for stdout.",
            DefaultValueFactory = _ => "-"
        };
        Option<bool> includePermissions = new("--include-permissions")
        {
            Description = "Emit GRANT / REVOKE statements. Off by default (Redgate parity)."
        };

        Command command = new("script", "Generate a T-SQL deployment script from source to target")
        {
            source,
            target,
            outPath,
            includePermissions
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string srcConn = parseResult.GetValue(source)!;
            string tgtConn = parseResult.GetValue(target)!;
            string output = parseResult.GetValue(outPath) ?? "-";
            bool emitPerms = parseResult.GetValue(includePermissions);

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

            ComparisonOptions opts = ComparisonOptions.Default;
            if (emitPerms)
            {
                opts &= ~ComparisonOptions.IgnorePermissions;
            }

            // opts, not ComparisonOptions.Default. The two calls used to disagree:
            // Generate got the real options while Compare got the default. It is
            // harmless only for as long as ComparisonEngine keeps ignoring
            // IgnorePermissions — the flag is read in exactly one place,
            // ScriptGenerator, and nothing says so anywhere. The day the engine
            // starts honouring it, --include-permissions would go quiet here and
            // the reason would be two lines apart.
            ComparisonResult comparison = new ComparisonEngine()
                .Compare(srcResult.Value!, tgtResult.Value!, opts);
            string script = new ScriptGenerator().Generate(
                comparison,
                selection: null,
                options: opts,
                dependencies: srcResult.Value!.Dependencies,
                dropDependencies: tgtResult.Value!.Dependencies);

            if (string.Equals(output, "-", StringComparison.Ordinal))
            {
                await Console.Out.WriteAsync(script).ConfigureAwait(false);
            }
            else
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(output));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(output, script, ct).ConfigureAwait(false);
            }

            // Same rule as compare and report, and for the same reason: a
            // pipeline that gates on the exit code has to be able to tell "the
            // script is empty because the two are aligned" from "the script
            // carries work". This verb used to return 0 either way, so a
            // CI step that trusted it never saw a pending difference.
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
