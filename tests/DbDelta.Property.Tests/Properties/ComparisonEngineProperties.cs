using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.PropertyTests.Arbitraries;
using FluentAssertions;
using FsCheck.Fluent;
using Xunit;

namespace DbDelta.PropertyTests.Properties;

/// <summary>
/// Universal-quantification properties for <see cref="ComparisonEngine"/>.
/// FsCheck.Xunit 3.0 still binds against xunit.core 2.x — which is
/// incompatible with the xunit.v3 runner the rest of the test matrix
/// uses. So we sample the generators manually from inside plain
/// <see cref="FactAttribute"/> tests; we lose FsCheck's automatic
/// shrinking on failure but keep the universal-quantification coverage
/// the suite was designed for, and the failure stays inside the same
/// xunit.v3 process as every other test.
/// </summary>
public class ComparisonEngineProperties
{
    private const int SampleCount = 100;
    private const int Size = 8;

    private static readonly ComparisonEngine _engine = new();

    [Fact]
    public void Compare_With_Itself_Is_Identical()
    {
        foreach (Database db in Sample())
        {
            ComparisonResult result = _engine.Compare(db, db, ComparisonOptions.Default);
            bool allIdentical = result.Differences.All(d => d.Status == DifferenceStatus.Identical);
            allIdentical.Should().BeTrue("Compare(x, x) must classify every object as Identical");
        }
    }

    [Fact]
    public void Compare_Against_Empty_Reports_OnlyInA()
    {
        Database empty = Empty();
        foreach (Database db in Sample())
        {
            ComparisonResult result = _engine.Compare(db, empty, ComparisonOptions.Default);
            bool clean = result.Differences.All(d =>
                d.Status is DifferenceStatus.Identical or DifferenceStatus.OnlyInA);
            clean.Should().BeTrue("source-only objects must classify as OnlyInA (never OnlyInB / Different)");
        }
    }

    [Fact]
    public void Compare_Empty_Against_Db_Reports_OnlyInB()
    {
        Database empty = Empty();
        foreach (Database db in Sample())
        {
            ComparisonResult result = _engine.Compare(empty, db, ComparisonOptions.Default);
            bool clean = result.Differences.All(d =>
                d.Status is DifferenceStatus.Identical or DifferenceStatus.OnlyInB);
            clean.Should().BeTrue("target-only objects must classify as OnlyInB (never OnlyInA / Different)");
        }
    }

    [Fact]
    public void Compare_Is_Deterministic()
    {
        IReadOnlyList<Database> samples = [.. Sample()];
        for (int i = 0; i < samples.Count - 1; i += 2)
        {
            Database a = samples[i];
            Database b = samples[i + 1];
            ComparisonResult r1 = _engine.Compare(a, b, ComparisonOptions.Default);
            ComparisonResult r2 = _engine.Compare(a, b, ComparisonOptions.Default);
            bool sameSequence = r1.Differences
                .Select(d => (d.Identity, d.Status))
                .SequenceEqual(r2.Differences.Select(d => (d.Identity, d.Status)));
            sameSequence.Should().BeTrue("Compare must be a pure function of its inputs");
        }
    }

    [Fact]
    public void Side_Flip_Is_Antisymmetric()
    {
        IReadOnlyList<Database> samples = [.. Sample()];
        for (int i = 0; i < samples.Count - 1; i += 2)
        {
            Database a = samples[i];
            Database b = samples[i + 1];
            ComparisonResult forward = _engine.Compare(a, b, ComparisonOptions.Default);
            ComparisonResult reverse = _engine.Compare(b, a, ComparisonOptions.Default);

            Dictionary<ObjectIdentity, DifferenceStatus> reverseByIdentity =
                reverse.Differences.ToDictionary(d => d.Identity, d => d.Status);

            foreach (DifferencePair fwd in forward.Differences)
            {
                reverseByIdentity.TryGetValue(fwd.Identity, out DifferenceStatus revStatus)
                    .Should().BeTrue($"identity {fwd.Identity.Kind}/{fwd.Identity.ObjectName} must round-trip");
                DifferenceStatus expected = fwd.Status switch
                {
                    DifferenceStatus.OnlyInA => DifferenceStatus.OnlyInB,
                    DifferenceStatus.OnlyInB => DifferenceStatus.OnlyInA,
                    DifferenceStatus.Identical => DifferenceStatus.Identical,
                    DifferenceStatus.Different => DifferenceStatus.Different,
                    _ => fwd.Status,
                };
                revStatus.Should().Be(expected);
            }
        }
    }

    [Fact]
    public void Identity_Is_Well_Formed()
    {
        IReadOnlyList<Database> samples = [.. Sample()];
        for (int i = 0; i < samples.Count - 1; i += 2)
        {
            Database a = samples[i];
            Database b = samples[i + 1];
            ComparisonResult result = _engine.Compare(a, b, ComparisonOptions.Default);
            bool wellFormed = result.Differences.All(d =>
                !string.IsNullOrWhiteSpace(d.Identity.Kind)
                && !string.IsNullOrWhiteSpace(d.Identity.ObjectName));
            wellFormed.Should().BeTrue("every diff identity must carry a non-empty kind + name");
        }
    }

    private static IEnumerable<Database> Sample() =>
        Gen.Sample(SchemaArbitraries.Database().Generator, SampleCount, size: Size);

    private static Database Empty() => new(
        Name: "Empty",
        Schemas: [new Schema("dbo")],
        Tables: [],
        Views: [],
        Procedures: [],
        Functions: [],
        Triggers: []);
}
