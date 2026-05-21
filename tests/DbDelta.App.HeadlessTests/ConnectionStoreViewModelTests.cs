using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests;

public class ConnectionStoreViewModelTests
{
    private sealed class InMemoryConnectionStore : IConnectionStore
    {
        private readonly List<ConnectionEntry> _entries = [];
        public Task<IReadOnlyList<ConnectionEntry>> LoadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConnectionEntry>>([.. _entries]);
        public Task<ConnectionEntry> UpsertAsync(ConnectionEntry entry, CancellationToken ct)
        {
            _entries.RemoveAll(e => e.Id == entry.Id);
            _entries.Add(entry);
            return Task.FromResult(entry);
        }
        public Task DeleteAsync(Guid id, CancellationToken ct)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }
        public Task TouchUsageAsync(Guid id, CancellationToken ct)
        {
            int i = _entries.FindIndex(e => e.Id == id);
            if (i >= 0)
            {
                _entries[i] = _entries[i] with { LastUsedUtc = DateTime.UtcNow };
            }
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _map = [];
        public bool IsAvailable => true;
        public Task<string?> GetSecretAsync(string key, CancellationToken ct) =>
            Task.FromResult(_map.GetValueOrDefault(key));
        public Task SetSecretAsync(string key, string secret, CancellationToken ct)
        {
            _map[key] = secret;
            return Task.CompletedTask;
        }
        public Task DeleteSecretAsync(string key, CancellationToken ct)
        {
            _map.Remove(key);
            return Task.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task Autosave_creates_one_entry_per_connection_string()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        await vm.LoadAsync(CancellationToken.None);

        await vm.AutosaveAsync(
            "Server=192.168.1.1;Database=Demo;User Id=sa;Password=Hello;TrustServerCertificate=True",
            CancellationToken.None);

        vm.Entries.Should().ContainSingle(e => e.ServerName == "192.168.1.1" && e.DatabaseName == "Demo");
    }

    [AvaloniaFact]
    public async Task Autosave_is_idempotent_for_the_same_connection_string()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        await vm.LoadAsync(CancellationToken.None);
        string cs = "Server=srv;Database=db;User Id=sa;Password=p;TrustServerCertificate=True";
        await vm.AutosaveAsync(cs, CancellationToken.None);
        await vm.AutosaveAsync(cs, CancellationToken.None);
        vm.Entries.Should().HaveCount(1);
    }

    [AvaloniaFact]
    public async Task Filter_by_search_term_matches_Name_or_Server()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        await vm.AutosaveAsync("Server=ProdHost;Database=Demo;User Id=sa;Password=p;", CancellationToken.None);
        await vm.AutosaveAsync("Server=DevHost;Database=Demo;User Id=sa;Password=p;", CancellationToken.None);

        vm.SearchText = "prod";
        vm.FilteredEntries.Should().ContainSingle();
        vm.FilteredEntries[0].ServerName.Should().Be("ProdHost");
    }

    [AvaloniaFact]
    public async Task UpsertExplicitAsync_replaces_existing_by_Id()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        Guid id = Guid.NewGuid();
        DbDelta.Core.Abstractions.ConnectionEntry first = new(
            id, "First", "srv", "db", "Server=srv;Database=db;Password={PASSWORD};", "Dev", "#0054BD", false,
            DateTime.UtcNow, DateTime.UtcNow);
        await vm.UpsertExplicitAsync(first, "pwd", CancellationToken.None);
        DbDelta.Core.Abstractions.ConnectionEntry second = first with { Name = "Renamed" };
        await vm.UpsertExplicitAsync(second, "pwd2", CancellationToken.None);
        vm.Entries.Should().ContainSingle().Which.Name.Should().Be("Renamed");
    }

    [AvaloniaFact]
    public async Task DeleteAsync_removes_entry_and_secret()
    {
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        Guid id = Guid.NewGuid();
        DbDelta.Core.Abstractions.ConnectionEntry e = new(
            id, "x", "srv", "db", "Server=srv;Database=db;Password={PASSWORD};", "Dev", "#0054BD", false,
            DateTime.UtcNow, DateTime.UtcNow);
        await vm.UpsertExplicitAsync(e, "pwd", CancellationToken.None);
        await vm.DeleteAsync(id, CancellationToken.None);
        vm.Entries.Should().BeEmpty();
    }
}
