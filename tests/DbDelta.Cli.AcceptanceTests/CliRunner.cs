using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace DbDelta.Cli.AcceptanceTests;

/// <summary>
/// Shared spawn helper for the <c>dbdelta</c> binary plus the handful of SQL
/// fixtures (create DB, create Customer table) reused across acceptance tests.
/// </summary>
internal static class CliRunner
{
    public static string ConnectionFor(string baseConnectionString, string db) =>
        new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = db }.ConnectionString;

    public static async Task CreateDb(string baseConnectionString, string db, CancellationToken ct)
    {
        await using SqlConnection c = new(baseConnectionString);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new($"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task CreateCustomerTable(string baseConnectionString, string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(baseConnectionString, db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            "IF OBJECT_ID('dbo.Customer') IS NULL CREATE TABLE dbo.Customer(Id int);", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<int> Run(string[] args, CancellationToken ct)
    {
        (int exitCode, _) = await RunCapturing(args, ct);
        return exitCode;
    }

    /// <summary>
    /// Same spawn, but hands back stdout as well — the CLI reports the outcome
    /// (success, rolledBack, transaction mode) as JSON on stdout, and an exit
    /// code alone cannot tell a rolled-back target from a half-migrated one.
    /// </summary>
    public static async Task<(int ExitCode, string StdOut)> RunCapturing(string[] args, CancellationToken ct)
    {
        // AppContext.BaseDirectory => <repo>/tests/DbDelta.Cli.AcceptanceTests/bin/<Config>/net10.0/
        // Walk up to repo root, then locate the CLI DLL under the SAME configuration.
        string testBin = AppContext.BaseDirectory;
        string configuration = new DirectoryInfo(testBin).Parent!.Name;
        string repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", "..", ".."));
        string cliDll = Path.Combine(repoRoot, "src", "DbDelta.Cli", "bin", configuration, "net10.0", "dbdelta.dll");

        ProcessStartInfo psi = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        // Both pipes have to be drained while the child runs, or a chatty
        // process blocks on a full buffer and WaitForExitAsync never returns.
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await stderr;
        return (p.ExitCode, await stdout);
    }
}
