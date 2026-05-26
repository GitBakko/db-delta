using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// Entry point for the DbDelta micro-benchmark suite. Run from the repo
// root with: `dotnet run -c Release --project bench/DbDelta.Benchmarks --
//                                     --filter "*"`. Pass `--short` to
// halve the iteration count when smoke-checking that the suite still
// compiles after a refactor.
//
// The defaults below add a column ordering that keeps the §6.1 budget
// table (1k / 10k object scans + diff + script-gen) directly comparable
// across runs.
BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.JoinSummary));

internal sealed partial class Program
{
}
