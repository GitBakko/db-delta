using DbDelta.Persistence.Json;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Json;

/// <summary>
/// The MRU file holds the paths of every project the user has opened. Losing it
/// is not data loss on the scale of a database, but it is silent and it is
/// theirs.
/// </summary>
/// <remarks>
/// Two gaps, both closed here. The future-schema branch returned an empty
/// document without keeping a copy, so a rollback from a v2 build to a v1 one
/// flattened the list on the next save. Worse, the <c>JsonException</c> branch
/// ignored <c>forWrite</c> entirely — it undercut the guard the branch below it
/// applies, so a truncated file was read as "no entries" even on the write path
/// and the next save wrote that emptiness back.
/// </remarks>
public sealed class JsonRecentProjectsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dbdelta-mru-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "recent-projects.json");

    public JsonRecentProjectsStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private JsonRecentProjectsStore CreateStore() => new(File_);

    [Fact]
    public async Task A_corrupted_file_is_kept_aside_instead_of_being_overwritten()
    {
        await File.WriteAllTextAsync(File_, "{ this is not valid json", TestContext.Current.CancellationToken);

        IReadOnlyList<RecentProject> entries =
            await CreateStore().LoadAsync(TestContext.Current.CancellationToken);

        entries.Should().BeEmpty();
        Directory.GetFiles(_dir).Should().ContainSingle(p => p.Contains(".broken-"),
            "the paths are unrecoverable once the next save replaces them");
    }

    [Fact]
    public async Task A_file_from_a_future_schema_is_kept_aside()
    {
        await File.WriteAllTextAsync(
            File_, /*lang=json,strict*/ """{"schemaVersion":999,"entries":[]}""",
            TestContext.Current.CancellationToken);

        IReadOnlyList<RecentProject> entries =
            await CreateStore().LoadAsync(TestContext.Current.CancellationToken);

        entries.Should().BeEmpty();
        Directory.GetFiles(_dir).Should().ContainSingle(p => p.Contains(".broken-"),
            "a downgrade must not cost the list written by the newer build");
    }

    [Fact]
    public async Task A_healthy_file_is_never_moved_aside()
    {
        // The negative control. If the copy fires on the ordinary path, every
        // save leaves litter in the profile.
        JsonRecentProjectsStore store = CreateStore();
        // The files have to exist: LoadAsync drops entries whose path is gone,
        // so a project deleted from disk stops being offered.
        string a = Path.Combine(_dir, "Progetto.dbd");
        string b = Path.Combine(_dir, "Altro.dbd");
        await File.WriteAllTextAsync(a, "<project />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(b, "<project />", TestContext.Current.CancellationToken);
        await store.AddOrTouchAsync(a, TestContext.Current.CancellationToken);
        await store.AddOrTouchAsync(b, TestContext.Current.CancellationToken);

        IReadOnlyList<RecentProject> entries =
            await store.LoadAsync(TestContext.Current.CancellationToken);

        entries.Should().HaveCount(2);
        Directory.GetFiles(_dir).Should().NotContain(p => p.Contains(".broken-"));
    }
}
