using DbDelta.Core.Diff;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class LineDifferTests
{
    // -------------------------------------------------------------------------
    // 1. Both null → empty
    // -------------------------------------------------------------------------
    [Fact]
    public void Both_null_returns_empty()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute(null, null);
        result.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // 2. Equal single line → one Unchanged row
    // -------------------------------------------------------------------------
    [Fact]
    public void Equal_single_line_returns_one_unchanged_row()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("SELECT 1", "SELECT 1");

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineDiff(0, 1, 1, "SELECT 1", "SELECT 1", LineStatus.Unchanged));
    }

    // -------------------------------------------------------------------------
    // 3. Pure addition at end
    //    source "A\nB" vs target "A\nB\nC" → Unchanged, Unchanged, Added
    // -------------------------------------------------------------------------
    [Fact]
    public void Pure_addition_at_end()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\nB", "A\nB\nC");

        result.Should().HaveCount(3);
        result[0].Should().Be(new LineDiff(0, 1, 1, "A", "A", LineStatus.Unchanged));
        result[1].Should().Be(new LineDiff(1, 2, 2, "B", "B", LineStatus.Unchanged));
        result[2].Should().Be(new LineDiff(2, null, 3, null, "C", LineStatus.Added));
    }

    // -------------------------------------------------------------------------
    // 4. Pure removal at end
    //    source "A\nB\nC" vs target "A\nB" → Unchanged, Unchanged, Removed
    // -------------------------------------------------------------------------
    [Fact]
    public void Pure_removal_at_end()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\nB\nC", "A\nB");

        result.Should().HaveCount(3);
        result[0].Should().Be(new LineDiff(0, 1, 1, "A", "A", LineStatus.Unchanged));
        result[1].Should().Be(new LineDiff(1, 2, 2, "B", "B", LineStatus.Unchanged));
        result[2].Should().Be(new LineDiff(2, 3, null, "C", null, LineStatus.Removed));
    }

    // -------------------------------------------------------------------------
    // 5. Single-line modification
    //    source "SELECT 1" vs target "SELECT 2" → one Modified row
    // -------------------------------------------------------------------------
    [Fact]
    public void Single_line_modification()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("SELECT 1", "SELECT 2");

        result.Should().HaveCount(1);
        LineDiff row = result[0];
        row.Status.Should().Be(LineStatus.Modified);
        row.SourceText.Should().Be("SELECT 1");
        row.TargetText.Should().Be("SELECT 2");
        row.SourceLineNumber.Should().Be(1);
        row.TargetLineNumber.Should().Be(1);
        row.Index.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // 6. Mixed add+remove pair-up
    //    source "A\nB" vs target "A\nX" → Unchanged, Modified(B, X)
    // -------------------------------------------------------------------------
    [Fact]
    public void Mixed_delete_and_insert_produces_modified()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\nB", "A\nX");

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineDiff(0, 1, 1, "A", "A", LineStatus.Unchanged));
        result[1].Should().Be(new LineDiff(1, 2, 2, "B", "X", LineStatus.Modified));
    }

    // -------------------------------------------------------------------------
    // 7. Excess delete after pair-up
    //    source "A\nB\nC" vs target "A\nX"
    //    → Unchanged, Modified(B,X), Removed(C, null)
    // -------------------------------------------------------------------------
    [Fact]
    public void Excess_delete_becomes_removed_after_pairing()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\nB\nC", "A\nX");

        result.Should().HaveCount(3);
        result[0].Should().Be(new LineDiff(0, 1, 1, "A", "A", LineStatus.Unchanged));
        result[1].Should().Be(new LineDiff(1, 2, 2, "B", "X", LineStatus.Modified));
        result[2].Should().Be(new LineDiff(2, 3, null, "C", null, LineStatus.Removed));
    }

    // -------------------------------------------------------------------------
    // 8. Excess insert after pair-up
    //    source "A\nB" vs target "A\nX\nY"
    //    → Unchanged, Modified(B,X), Added(null, Y)
    // -------------------------------------------------------------------------
    [Fact]
    public void Excess_insert_becomes_added_after_pairing()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\nB", "A\nX\nY");

        result.Should().HaveCount(3);
        result[0].Should().Be(new LineDiff(0, 1, 1, "A", "A", LineStatus.Unchanged));
        result[1].Should().Be(new LineDiff(1, 2, 2, "B", "X", LineStatus.Modified));
        result[2].Should().Be(new LineDiff(2, null, 3, null, "Y", LineStatus.Added));
    }

    // -------------------------------------------------------------------------
    // 9. CRLF normalisation
    //    "A\r\nB" vs "A\r\nB" → 2 Unchanged rows
    // -------------------------------------------------------------------------
    [Fact]
    public void Crlf_is_normalised_before_comparison()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\r\nB", "A\r\nB");

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineDiff(0, 1, 1, "A", "A", LineStatus.Unchanged));
        result[1].Should().Be(new LineDiff(1, 2, 2, "B", "B", LineStatus.Unchanged));
    }

    // -------------------------------------------------------------------------
    // 10. SectionsFrom: rows 1, 4, 5 non-Unchanged → [(1,1),(4,2)]
    // -------------------------------------------------------------------------
    [Fact]
    public void SectionsFrom_groups_non_unchanged_runs_correctly()
    {
        // Build a 6-row diff where indices 1, 4, 5 are non-Unchanged.
        var rows = new List<LineDiff>
        {
            new(0, 1, 1, "A", "A", LineStatus.Unchanged),
            new(1, 2, 2, "B", "X", LineStatus.Modified),
            new(2, 3, 3, "C", "C", LineStatus.Unchanged),
            new(3, 4, 4, "D", "D", LineStatus.Unchanged),
            new(4, 5, null, "E", null, LineStatus.Removed),
            new(5, null, 5, null, "F", LineStatus.Added),
        };

        IReadOnlyList<DiffSection> sections = LineDiffer.SectionsFrom(rows);

        sections.Should().HaveCount(2);
        sections[0].Should().Be(new DiffSection(1, 1));
        sections[1].Should().Be(new DiffSection(4, 2));
    }

    // -------------------------------------------------------------------------
    // Additional tests for robustness and coverage
    // -------------------------------------------------------------------------

    [Fact]
    public void Empty_string_source_and_null_target_returns_empty()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("", null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Source_null_target_non_empty_returns_all_added()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute(null, "X\nY");

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineDiff(0, null, 1, null, "X", LineStatus.Added));
        result[1].Should().Be(new LineDiff(1, null, 2, null, "Y", LineStatus.Added));
    }

    [Fact]
    public void Source_non_empty_target_null_returns_all_removed()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("X\nY", null);

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineDiff(0, 1, null, "X", null, LineStatus.Removed));
        result[1].Should().Be(new LineDiff(1, 2, null, "Y", null, LineStatus.Removed));
    }

    [Fact]
    public void Trailing_newline_is_dropped()
    {
        // "A\nB\n" should produce same result as "A\nB"
        IReadOnlyList<LineDiff> withTrailing = LineDiffer.Compute("A\nB\n", "A\nB\n");
        IReadOnlyList<LineDiff> withoutTrailing = LineDiffer.Compute("A\nB", "A\nB");

        withTrailing.Should().HaveCount(withoutTrailing.Count);
        for (int i = 0; i < withoutTrailing.Count; i++)
        {
            withTrailing[i].Should().Be(withoutTrailing[i]);
        }
    }

    [Fact]
    public void Indices_are_zero_based_and_sequential()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\nB\nC", "A\nX\nC");

        result.Should().HaveCount(3);
        result[0].Index.Should().Be(0);
        result[1].Index.Should().Be(1);
        result[2].Index.Should().Be(2);
    }

    [Fact]
    public void SectionsFrom_empty_input_returns_empty()
    {
        IReadOnlyList<DiffSection> sections = LineDiffer.SectionsFrom([]);
        sections.Should().BeEmpty();
    }

    [Fact]
    public void SectionsFrom_all_unchanged_returns_empty()
    {
        var rows = new List<LineDiff>
        {
            new(0, 1, 1, "A", "A", LineStatus.Unchanged),
            new(1, 2, 2, "B", "B", LineStatus.Unchanged),
        };

        IReadOnlyList<DiffSection> sections = LineDiffer.SectionsFrom(rows);
        sections.Should().BeEmpty();
    }

    [Fact]
    public void Cr_only_line_endings_are_normalised()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("A\rB", "A\rB");

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(r => r.Status.Should().Be(LineStatus.Unchanged));
    }

    // -------------------------------------------------------------------------
    // Common head and tail are trimmed before the DP table is allocated.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A body far larger than anything the other tests use, changed in one line.
    /// </summary>
    /// <remarks>
    /// The table is <c>int[m+1, n+1]</c>, so before the trim two 30.000-line
    /// bodies asked for roughly 3,6 GB in one allocation and threw
    /// OutOfMemoryException — on a procedure a user can plausibly own, from a
    /// double-click in the diff viewer. The largest input in this file was three
    /// lines, which is why nothing here ever noticed.
    /// </remarks>
    [Fact]
    public void A_huge_body_changed_in_one_line_is_diffed_without_a_giant_table()
    {
        string[] lines = [.. Enumerable.Range(0, 30_000).Select(i => $"    SELECT {i};")];
        string source = string.Join('\n', lines);
        lines[15_000] = "    SELECT 999999; -- cambiata";
        string target = string.Join('\n', lines);

        IReadOnlyList<LineDiff> result = LineDiffer.Compute(source, target);

        result.Should().HaveCount(30_000);
        result.Count(r => r.Status != LineStatus.Unchanged)
            .Should().Be(1, "exactly one line moved");
        result[15_000].Status.Should().Be(LineStatus.Modified);
    }

    /// <summary>
    /// The trim must not change any verdict — it only decides what the DP table
    /// has to cover.
    /// </summary>
    [Fact]
    public void Trimming_the_head_and_tail_leaves_the_same_verdicts()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute(
            "uguale-1\nuguale-2\nSOLO-SORGENTE\nuguale-3\nuguale-4",
            "uguale-1\nuguale-2\nSOLO-TARGET\nuguale-3\nuguale-4");

        result.Should().HaveCount(5);
        result[0].Status.Should().Be(LineStatus.Unchanged);
        result[1].Status.Should().Be(LineStatus.Unchanged);
        result[2].Status.Should().Be(LineStatus.Modified);
        result[3].Status.Should().Be(LineStatus.Unchanged);
        result[4].Status.Should().Be(LineStatus.Unchanged);
        result[2].SourceText.Should().Be("SOLO-SORGENTE");
        result[2].TargetText.Should().Be("SOLO-TARGET");
    }

    /// <summary>Two bodies that share nothing still diff — the trim finds no head or tail.</summary>
    [Fact]
    public void Two_bodies_with_nothing_in_common_still_diff()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("a\nb", "c\nd");

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(r => r.Status.Should().Be(LineStatus.Modified));
    }

    /// <summary>Identical bodies are all head, and nothing is left for the table.</summary>
    [Fact]
    public void Identical_bodies_are_entirely_head()
    {
        IReadOnlyList<LineDiff> result = LineDiffer.Compute("a\nb\nc", "a\nb\nc");

        result.Should().HaveCount(3);
        result.Should().AllSatisfy(r => r.Status.Should().Be(LineStatus.Unchanged));
    }

    [Fact]
    public void DiffSection_EndIndex_equals_start_plus_length_minus_one()
    {
        var section = new DiffSection(4, 2);
        section.EndIndex.Should().Be(5);
    }
}
