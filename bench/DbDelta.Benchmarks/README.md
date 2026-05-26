# DbDelta.Benchmarks

BenchmarkDotNet micro-bench suite that exercises the two CPU-bound hot
paths in the DbDelta core — comparison and script generation — against
the **§6.1 Performance Budget (v1)** defined in
[`docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md`](../../docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md):

| Operation | Target | Stretch |
|-----------|-------:|--------:|
| Diff 10k-pair result | < 3 s | < 1 s |
| Generate full deployment script (10k diffs) | < 5 s | < 2 s |
| (1k variants) | proportional — kept for trend tracking | — |

Live DB read benches (`Connect + read schema`) are intentionally **not**
in this suite — they're I/O-bound and live in
`DbDelta.Providers.LiveDb.IntegrationTests`. This suite is pure CPU.

## Running the suite

The bench app is a self-contained BenchmarkDotNet harness — invoke it
through `dotnet run` so the runner can spawn its own worker process per
configuration.

```pwsh
# Full run (default jobs — takes ~10–15 min on a typical dev box)
dotnet run -c Release --project bench/DbDelta.Benchmarks -- --filter "*"

# Smoke run (3 iterations, 3 warmups — takes ~40 s)
dotnet run -c Release --project bench/DbDelta.Benchmarks -- --filter "*" --job short

# Single bench
dotnet run -c Release --project bench/DbDelta.Benchmarks -- --filter "*ComparisonBench*" --job short
dotnet run -c Release --project bench/DbDelta.Benchmarks -- --filter "*ScriptGenBench*"  --job short

# List discovered benches without running them
dotnet run -c Release --project bench/DbDelta.Benchmarks -- --list flat
```

Reports drop into `BenchmarkDotNet.Artifacts/` next to the project — both
markdown summaries and CSV exports. Commit the markdown alongside a perf
PR when you want a baseline for regression tracking.

## Benches included

### `ComparisonBench.Compare`
Pure `ComparisonEngine.Compare` cost over a synthetic schema pair
(`ObjectCount` ∈ {1 000, 10 000}). Allocates two `Database` snapshots
in `[GlobalSetup]` so the diff cost stays uncontaminated by fixture
build.

### `ScriptGenBench.Generate`
End-to-end `ScriptGenerator.Generate` cost on a fully populated
`ComparisonResult` (the result is cached in `[GlobalSetup]` so the
benchmark measures only the emission pipeline — sequences, tables,
indexes, views, functions, procedures, synonyms, foreign keys, etc.).

## Calibration baseline — 2026-05-26 (ShortRun)

Captured on a 12th Gen Intel Core i7-12700H (Windows 11, .NET 10
preview). Both targets clear §6.1 by orders of magnitude on this box —
the suite primarily serves as regression detection.

| Bench                       | Objects | Mean    | §6.1 target | Margin |
|-----------------------------|--------:|--------:|------------:|-------:|
| `ComparisonBench.Compare`   |   1 000 |  1.4 ms | (proportional) | — |
| `ComparisonBench.Compare`   |  10 000 | 17.8 ms |        3 000 ms | ≈ 170× |
| `ScriptGenBench.Generate`   |   1 000 | — (run locally) | — | — |
| `ScriptGenBench.Generate`   |  10 000 | — (run locally) | 5 000 ms | — |

Re-run the suite when a code change might shift either hot path and
commit an updated table here if the regression is intentional. The
recommended cadence (per the spec §4 "Quality Bar" table) is **nightly**;
the suite is plain enough that a GitHub-Actions workflow can call it
verbatim once `Performance.Bench` lands in CI.
