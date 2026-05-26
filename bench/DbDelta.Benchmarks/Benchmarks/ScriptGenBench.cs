using BenchmarkDotNet.Attributes;
using DbDelta.Benchmarks.Fixtures;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;

namespace DbDelta.Benchmarks.Benchmarks;

/// <summary>
/// §6.1 "Generate full deployment script (10k diffs)" budget — target
/// &lt; 5s, stretch &lt; 2s. Drives the script generator across a fully
/// populated <see cref="ComparisonResult"/> (cached in the
/// <c>[GlobalSetup]</c> hook so the diff cost stays out of the per-op
/// measurement). The 1k variant is reported alongside to keep an eye on
/// scaling slope.
/// </summary>
[MemoryDiagnoser]
public class ScriptGenBench
{
    private ComparisonResult _result = null!;
    private string? _targetDefaultCollation;
    private readonly ScriptGenerator _generator = new();

    /// <summary>Number of objects on each side of the diff.</summary>
    [Params(1_000, 10_000)]
    public int ObjectCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Database source = SchemaFixtureBuilder.BuildSource(ObjectCount);
        Database target = SchemaFixtureBuilder.BuildTarget(ObjectCount);
        _result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        _targetDefaultCollation = target.DefaultCollation;
    }

    [Benchmark]
    public string Generate() =>
        _generator.Generate(_result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: _targetDefaultCollation);
}
