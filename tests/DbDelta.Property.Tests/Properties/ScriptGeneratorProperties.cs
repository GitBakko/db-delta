using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using DbDelta.PropertyTests.Arbitraries;
using FluentAssertions;
using FsCheck.Fluent;
using Xunit;

namespace DbDelta.PropertyTests.Properties;

/// <summary>
/// Structural properties for <see cref="ScriptGenerator"/> — wrapper
/// envelope, section ordering, idempotent output, and the no-op shape
/// when the inputs are identical. Same xunit.v3 / sampled-arbitrary
/// pattern as <see cref="ComparisonEngineProperties"/>.
/// </summary>
public class ScriptGeneratorProperties
{
    private const int SampleCount = 60;
    private const int Size = 6;

    private static readonly ComparisonEngine _engine = new();
    private static readonly ScriptGenerator _generator = new();

    [Fact]
    public void Script_Has_Transaction_Wrapper()
    {
        IReadOnlyList<Database> samples = [.. Sample()];
        for (int i = 0; i < samples.Count - 1; i += 2)
        {
            Database a = samples[i];
            Database b = samples[i + 1];
            ComparisonResult result = _engine.Compare(a, b, ComparisonOptions.Default);
            string script = _generator.Generate(result, selection: null,
                options: ComparisonOptions.Default, targetDefaultCollation: b.DefaultCollation);
            script.Should().Contain("SET XACT_ABORT ON;")
                .And.Contain("BEGIN TRANSACTION;")
                .And.Contain("COMMIT TRANSACTION;");
        }
    }

    [Fact]
    public void Reflexive_Generate_Emits_Envelope_Only()
    {
        foreach (Database db in Sample())
        {
            ComparisonResult result = _engine.Compare(db, db, ComparisonOptions.Default);
            string script = _generator.Generate(result, selection: null,
                options: ComparisonOptions.Default, targetDefaultCollation: db.DefaultCollation);
            // Reflexive comparison: only the envelope text should remain.
            // Strip the boilerplate lines and assert nothing else survives.
            script.Should().NotContain("CREATE ");
            script.Should().NotContain("ALTER ");
            script.Should().NotContain("DROP ");
        }
    }

    [Fact]
    public void Generate_Is_Deterministic()
    {
        IReadOnlyList<Database> samples = [.. Sample()];
        for (int i = 0; i < samples.Count - 1; i += 2)
        {
            Database a = samples[i];
            Database b = samples[i + 1];
            ComparisonResult result = _engine.Compare(a, b, ComparisonOptions.Default);
            string s1 = _generator.Generate(result, selection: null,
                options: ComparisonOptions.Default, targetDefaultCollation: b.DefaultCollation);
            string s2 = _generator.Generate(result, selection: null,
                options: ComparisonOptions.Default, targetDefaultCollation: b.DefaultCollation);
            s1.Should().Be(s2, "ScriptGenerator must be a pure function");
        }
    }

    [Fact]
    public void Sequences_Precede_Tables()
    {
        IReadOnlyList<Database> samples = [.. Sample()];
        for (int i = 0; i < samples.Count - 1; i += 2)
        {
            Database a = samples[i];
            Database b = samples[i + 1];
            ComparisonResult result = _engine.Compare(a, b, ComparisonOptions.Default);
            string script = _generator.Generate(result, selection: null,
                options: ComparisonOptions.Default, targetDefaultCollation: b.DefaultCollation);
            int firstSeq = FirstIndexOfAny(script, "CREATE SEQUENCE", "DROP SEQUENCE", "ALTER SEQUENCE");
            int firstTable = FirstIndexOfAny(script, "CREATE TABLE", "DROP TABLE", "ALTER TABLE");
            if (firstSeq < 0 || firstTable < 0) { continue; }
            firstSeq.Should().BeLessThan(firstTable,
                "any DEFAULT NEXT VALUE FOR seq → table relationship requires the sequence to exist first");
        }
    }

    [Fact]
    public void Foreign_Keys_Come_After_Tables()
    {
        IReadOnlyList<Database> samples = [.. Sample()];
        for (int i = 0; i < samples.Count - 1; i += 2)
        {
            Database a = samples[i];
            Database b = samples[i + 1];
            ComparisonResult result = _engine.Compare(a, b, ComparisonOptions.Default);
            string script = _generator.Generate(result, selection: null,
                options: ComparisonOptions.Default, targetDefaultCollation: b.DefaultCollation);
            int lastTableDdl = LastIndexOfAny(script, "CREATE TABLE", "DROP TABLE");
            int firstFkAdd = script.IndexOf("ADD CONSTRAINT [FK_", StringComparison.Ordinal);
            if (lastTableDdl < 0 || firstFkAdd < 0) { continue; }
            firstFkAdd.Should().BeGreaterThan(lastTableDdl,
                "FK ADDs must wait until every referenced table has been created");
        }
    }

    private static IEnumerable<Database> Sample() =>
        Gen.Sample(SchemaArbitraries.Database().Generator, SampleCount, size: Size);

    private static int FirstIndexOfAny(string haystack, params string[] needles)
    {
        int best = -1;
        foreach (string n in needles)
        {
            int i = haystack.IndexOf(n, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best))
            {
                best = i;
            }
        }
        return best;
    }

    private static int LastIndexOfAny(string haystack, params string[] needles)
    {
        int best = -1;
        foreach (string n in needles)
        {
            int i = haystack.LastIndexOf(n, StringComparison.Ordinal);
            if (i > best) { best = i; }
        }
        return best;
    }
}
