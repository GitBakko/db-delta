using Serilog;
using Serilog.Events;

namespace DbDelta.Cli.Logging;

/// <summary>
/// Configures Serilog with console + rolling-file sinks for the CLI host.
/// </summary>
internal static class SerilogBootstrap
{
    public static ILogger Build(LogEventLevel minimumLevel, string? logFile)
    {
        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.Console();

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logFile)!);
            configuration = configuration.WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7);
        }

        return configuration.CreateLogger();
    }
}
