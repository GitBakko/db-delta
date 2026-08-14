using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

    // ── A failed load must not leave one object's SQL under another's name ────

    /// <summary>
    /// The panes are cleared BEFORE the first await, so an object that fails to
    /// resolve shows nothing rather than inheriting the previously selected
    /// object's SQL under its own name.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadAsync_clears_the_previous_object_before_it_can_fail()
    {
        DiffViewerViewModel vm = new(new StubResolver("A\nB\nC", "A\nX\nC"));
        await vm.LoadAsync(MakeRow("Clienti"), CancellationToken.None);
        vm.Rows.Should().NotBeEmpty("precondition: the first object loaded");

        vm.SetResolver(new ThrowingResolver("connessione persa"));
        Func<Task> act = () => vm.LoadAsync(MakeRow("Ordini"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        vm.Rows.Should().BeEmpty("a failed object must not inherit the previous object's diff");
        vm.SourceBody.Should().BeNull();
        vm.TargetBody.Should().BeNull();
        vm.ObjectQualifiedName.Should().Contain("Ordini");
    }

    /// <summary>
    /// The half-populated case: the source side resolves and the target side
    /// throws. Nothing is published unless BOTH sides arrived.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadAsync_leaves_both_panes_empty_when_only_the_target_side_fails()
    {
        DiffViewerViewModel vm = new(new ThrowingResolver("timeout", failOnSource: false));

        Func<Task> act = () => vm.LoadAsync(MakeRow(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        vm.SourceBody.Should().BeNull("a resolved source must not show against a target that never arrived");
        vm.Rows.Should().BeEmpty();
    }

    /// <summary>
    /// The user-visible half: selecting a row whose body cannot be read reports
    /// the failure on the error banner. This was a discarded Task with no catch
    /// anywhere, so the exception was unobserved and the screen never changed.
    /// </summary>
    [AvaloniaFact]
    public void A_failed_body_load_reaches_the_error_banner()
    {
        AppStateViewModel state = new();
        state.DiffViewer.SetResolver(new ThrowingResolver("connessione persa"));

        state.SelectedRow = MakeRow("Ordini");
        Dispatcher.UIThread.RunJobs();

        state.LastError.Should().NotBeNullOrWhiteSpace();
        state.LastError.Should().Contain("Ordini").And.Contain("connessione persa");
        state.DiffViewer.Rows.Should().BeEmpty();
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Resolver that fails the way the live one can: it opens its own SQL
    /// connection per call. With <paramref name="failOnSource"/> false the source
    /// resolves and only the TARGET throws — the half-populated case.
    /// </summary>
    private sealed class ThrowingResolver(string message, bool failOnSource = true) : IObjectBodyResolver
    {
        public Task<string?> ResolveSourceBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct) =>
            failOnSource
                ? Task.FromException<string?>(new InvalidOperationException(message))
                : Task.FromResult<string?>("SELECT 1");

        public Task<string?> ResolveTargetBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct) =>
            Task.FromException<string?>(new InvalidOperationException(message));
    }
}
