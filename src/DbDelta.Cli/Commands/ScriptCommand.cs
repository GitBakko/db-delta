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

            ComparisonResult comparison = new ComparisonEngine()
                .Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default);
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

            return ExitCodes.SuccessNoDifferences;
        });

        return command;
    }
}
