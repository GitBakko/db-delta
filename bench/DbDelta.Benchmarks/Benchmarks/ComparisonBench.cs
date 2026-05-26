using BenchmarkDotNet.Attributes;
using DbDelta.Benchmarks.Fixtures;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Benchmarks.Benchmarks;

/// <summary>
/// §6.1 "Diff 10k-pair result" budget — target &lt; 3s, stretch &lt; 1s.
/// Also reports the 1k variant for trend comparison; the 1k target is
/// implicit (proportionally &lt; 300ms / 100ms). The benchmark drives a
/// pure in-memory pair of <see cref="Database"/> snapshots through
/// <see cref="ComparisonEngine.Compare"/>; no I/O is involved.
/// </summary>
[MemoryDiagnoser]
public class ComparisonBench
{
    private Database _source = null!;
    private Database _target = null!;
    private readonly ComparisonEngine _engine = new();

    /// <summary>Number of objects in the synthetic schema pair.</summary>
    [Params(1_000, 10_000)]
    public int ObjectCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _source = SchemaFixtureBuilder.BuildSource(ObjectCount);
        _target = SchemaFixtureBuilder.BuildTarget(ObjectCount);
    }

    [Benchmark]
    public ComparisonResult Compare() => _engine.Compare(_source, _target, ComparisonOptions.Default);
}
