using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Shared.Dtos;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// Unit tests for <see cref="DiffViewerViewModel"/>. Uses a hand-rolled
/// stub for <see cref="IObjectBodyResolver"/> — no Moq dependency.
/// </summary>
public class DiffViewerViewModelTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DifferenceRowViewModel MakeRow(string name = "Orders", string schema = "dbo", string kind = "Table")
    {
        DifferenceDto dto = new(Kind: kind, SchemaName: schema, ObjectName: name, Status: "Different");
        DifferencePair pair = new(
            Identity: new ObjectIdentity(schema, name, kind),
            Status: DifferenceStatus.Different,
            SideA: null,
            SideB: null);
        return new(pair, dto, "#000");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Given a resolver returning "A\nB\nC" / "A\nX\nC", LoadAsync must
    /// produce 3 rows (A=Unchanged, B→X=Modified, C=Unchanged),
    /// exactly 1 section (covering the Modified row), and
    /// CurrentSectionIndex == 0.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadAsync_populates_rows_and_sections()
    {
        StubResolver resolver = new("A\nB\nC", "A\nX\nC");
        DiffViewerViewModel vm = new(resolver);

        await vm.LoadAsync(MakeRow(), CancellationToken.None);

        vm.Rows.Count.Should().Be(3);
        vm.Rows.Count(r => r.Status == LineStatus.Modified).Should().Be(1);
        vm.Sections.Count.Should().Be(1);
        vm.CurrentSectionIndex.Should().Be(0);
    }

    /// <summary>
    /// NextSection advances the index forward and clamps at Sections.Count - 1.
    /// </summary>
    [AvaloniaFact]
    public async Task NextSection_advances_index_and_clamps_at_end()
    {
        // Three distinct change sections: lines 1, 3, 5 differ
        StubResolver resolver = new("a\nb\nc\nd\ne", "X\nb\nX\nd\nX");
        DiffViewerViewModel vm = new(resolver);
        await vm.LoadAsync(MakeRow(), CancellationToken.None);

        int initialIndex = vm.CurrentSectionIndex; // 0
        vm.NextSection();
        vm.CurrentSectionIndex.Should().BeGreaterThan(initialIndex);

        // Keep pressing until clamped
        int sectionCount = vm.Sections.Count;
        for (int i = 0; i < sectionCount + 5; i++)
        {
            vm.NextSection();
        }

        vm.CurrentSectionIndex.Should().Be(sectionCount - 1);
    }

    /// <summary>
    /// PreviousSection decrements the index and clamps at 0.
    /// </summary>
    [AvaloniaFact]
    public async Task PreviousSection_decrements_and_clamps_at_start()
    {
        StubResolver resolver = new("a\nb\nc", "X\nb\nX");
        DiffViewerViewModel vm = new(resolver);
        await vm.LoadAsync(MakeRow(), CancellationToken.None);

        // Advance to the last section first
        for (int i = 0; i < vm.Sections.Count; i++) { vm.NextSection(); }
        int lastIndex = vm.CurrentSectionIndex;

        vm.PreviousSection();
        if (lastIndex > 0)
        {
            vm.CurrentSectionIndex.Should().BeLessThan(lastIndex);
        }

        // Keep pressing until clamped at 0
        for (int i = 0; i < vm.Sections.Count + 5; i++)
        {
            vm.PreviousSection();
        }

        vm.CurrentSectionIndex.Should().Be(0);
    }

    /// <summary>
    /// When both resolver sides return null (object absent from both sides),
    /// HasContent must be false.
    /// </summary>
    [AvaloniaFact]
    public async Task HasContent_false_when_resolver_returns_nulls()
    {
        StubResolver resolver = new(null, null);
        DiffViewerViewModel vm = new(resolver);

        await vm.LoadAsync(MakeRow(), CancellationToken.None);

        vm.HasContent.Should().BeFalse();
        vm.Rows.Count.Should().Be(0);
    }

    // ── Stub ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hand-rolled stub for <see cref="IObjectBodyResolver"/>.
    /// Returns fixed source/target bodies regardless of the requested object.
    /// </summary>
    private sealed class StubResolver(string? sourceBody, string? targetBody) : IObjectBodyResolver
    {
        public Task<string?> ResolveSourceBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct) =>
            Task.FromResult(sourceBody);

        public Task<string?> ResolveTargetBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct) =>
            Task.FromResult(targetBody);
    }
}
