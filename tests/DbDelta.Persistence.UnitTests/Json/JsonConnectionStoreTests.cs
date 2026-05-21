using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Json;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Json;

public class JsonConnectionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public JsonConnectionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dbdelta-conn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "connections.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private JsonConnectionStore CreateStore() => new(_file);

    private static ConnectionEntry MakeEntry(string name, Guid? id = null, bool pinned = false) =>
        new(
            Id: id ?? Guid.NewGuid(),
            Name: name,
            ServerName: "srv",
            DatabaseName: "db",
            ConnectionStringTemplate: "Server=srv;Database=db;User Id=sa;Password={PASSWORD};TrustServerCertificate=True",
            EnvironmentTag: "Dev",
            EnvironmentColorHex: "#0054BD",
            IsPinned: pinned,
            CreatedUtc: DateTime.UtcNow,
            LastUsedUtc: DateTime.UtcNow);

    [Fact]
    public async Task Load_returns_empty_when_file_absent()
    {
        IReadOnlyList<ConnectionEntry> entries = await CreateStore().LoadAsync(CancellationToken.None);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_then_Load_round_trips_one_entry()
    {
        JsonConnectionStore store = CreateStore();
        ConnectionEntry e = MakeEntry("Dev DB");
        await store.UpsertAsync(e, CancellationToken.None);
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().ContainSingle().Which.Should().Be(e);
    }

    [Fact]
    public async Task Upsert_with_same_Id_replaces_existing_entry()
    {
        JsonConnectionStore store = CreateStore();
        var id = Guid.NewGuid();
        await store.UpsertAsync(MakeEntry("Before", id), CancellationToken.None);
        await store.UpsertAsync(MakeEntry("After", id), CancellationToken.None);
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().ContainSingle().Which.Name.Should().Be("After");
    }

    [Fact]
    public async Task Delete_removes_by_Id()
    {
        JsonConnectionStore store = CreateStore();
        ConnectionEntry e = MakeEntry("ToDelete");
        await store.UpsertAsync(e, CancellationToken.None);
        await store.DeleteAsync(e.Id, CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Touch_bumps_LastUsedUtc()
    {
        JsonConnectionStore store = CreateStore();
        var id = Guid.NewGuid();
        DateTime t0 = DateTime.UtcNow.AddMinutes(-30);
        await store.UpsertAsync(MakeEntry("E", id) with { CreatedUtc = t0, LastUsedUtc = t0 }, CancellationToken.None);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        await store.TouchUsageAsync(id, CancellationToken.None);
        ConnectionEntry back = (await store.LoadAsync(CancellationToken.None)).Single();
        back.LastUsedUtc.Should().BeAfter(t0);
    }

    [Fact]
    public async Task Atomic_write_does_not_leave_tmp_file()
    {
        JsonConnectionStore store = CreateStore();
        await store.UpsertAsync(MakeEntry("E"), CancellationToken.None);
        Directory.GetFiles(_dir).Should().NotContain(p => p.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Load_returns_pinned_first_then_LastUsed_desc()
    {
        JsonConnectionStore store = CreateStore();
        DateTime now = DateTime.UtcNow;
        await store.UpsertAsync(MakeEntry("Old") with { LastUsedUtc = now.AddDays(-7) }, CancellationToken.None);
        await store.UpsertAsync(MakeEntry("Recent") with { LastUsedUtc = now }, CancellationToken.None);
        await store.UpsertAsync(MakeEntry("Pinned", pinned: true) with { LastUsedUtc = now.AddDays(-30) }, CancellationToken.None);
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Select(e => e.Name).Should().ContainInOrder("Pinned", "Recent", "Old");
    }

    [Fact]
    public async Task Corrupted_file_is_renamed_aside_and_load_returns_empty()
    {
        await File.WriteAllTextAsync(_file, "{ this is not valid json", CancellationToken.None);
        JsonConnectionStore store = CreateStore();
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().BeEmpty();
        Directory.GetFiles(_dir).Should().ContainSingle(p => p.Contains(".broken-"));
    }

    [Fact]
    public async Task Future_schema_version_renames_aside_and_returns_empty()
    {
        await File.WriteAllTextAsync(_file, """{"schemaVersion":999,"entries":[]}""", CancellationToken.None);
        JsonConnectionStore store = CreateStore();
        IReadOnlyList<ConnectionEntry> entries = await store.LoadAsync(CancellationToken.None);
        entries.Should().BeEmpty();
        Directory.GetFiles(_dir).Should().ContainSingle(p => p.Contains(".broken-"));
    }
}
