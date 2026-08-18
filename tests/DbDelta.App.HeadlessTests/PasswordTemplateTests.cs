using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests;

/// <summary>
/// The stored connection template must not carry any part of the real password,
/// and the string rebuilt from it must be the one the user typed.
/// </summary>
/// <remarks>
/// The template used to be cut with the regex
/// <c>(password|pwd)\s*=\s*[^;]+</c>. A connection-string value may legally
/// contain a semicolon once it is quoted, so on <c>Password='a;b'</c> the match
/// stopped at the inner semicolon and <c>b'</c> stayed in the template — a
/// fragment of the password written to disk in clear text, at every successful
/// comparison. Both directions now go through
/// <c>SqlConnectionStringBuilder</c>, which owns the quoting rules.
/// </remarks>
public class PasswordTemplateTests
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
        public Task TouchUsageAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public bool IsAvailable => true;
        public Task SetSecretAsync(string key, string secret, CancellationToken ct)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }
        public Task<string?> GetSecretAsync(string key, CancellationToken ct) =>
            Task.FromResult(_secrets.TryGetValue(key, out string? v) ? v : null);
        public Task DeleteSecretAsync(string key, CancellationToken ct)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }
    }

    private static ConnectionStoreViewModel Build() =>
        new(new InMemoryConnectionStore(), new InMemoryCredentialStore());

    // The password that broke the regex: a semicolon inside a quoted value.
    private const string AwkwardPassword = "a;b";

    private static string ConnectionStringWith(string password) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = "srv",
            InitialCatalog = "db",
            UserID = "sa",
            Password = password,
            TrustServerCertificate = true,
        }.ConnectionString;

    [AvaloniaFact]
    public async Task No_fragment_of_a_semicolon_bearing_password_reaches_the_template()
    {
        ConnectionStoreViewModel vm = Build();
        await vm.LoadAsync(CancellationToken.None);

        ConnectionEntry? entry = await vm.AutosaveAsync(
            ConnectionStringWith(AwkwardPassword), CancellationToken.None);

        entry.Should().NotBeNull();
        // "b" alone would be a false alarm — it occurs in "db". The fragment the
        // regex left behind was the tail after the inner semicolon, quote included.
        entry!.ConnectionStringTemplate.Should().NotContain("b\"");
        entry.ConnectionStringTemplate.Should().NotContain("b'");
        entry.ConnectionStringTemplate.Should().Contain("{PASSWORD}");
    }

    [AvaloniaFact]
    public async Task An_awkward_password_survives_the_round_trip_intact()
    {
        // The other half: a template that hides the password is worth nothing if
        // what comes back out no longer connects.
        ConnectionStoreViewModel vm = Build();
        await vm.LoadAsync(CancellationToken.None);
        ConnectionEntry entry = (await vm.AutosaveAsync(
            ConnectionStringWith(AwkwardPassword), CancellationToken.None))!;

        string? materialised = await vm.MaterialiseAsync(entry, CancellationToken.None);

        materialised.Should().NotBeNull();
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(materialised!)
            .Password.Should().Be(AwkwardPassword);
    }

    [AvaloniaFact]
    public async Task An_ordinary_password_still_round_trips()
    {
        // The negative control: the common case must not regress while the
        // awkward one is being handled.
        ConnectionStoreViewModel vm = Build();
        await vm.LoadAsync(CancellationToken.None);
        ConnectionEntry entry = (await vm.AutosaveAsync(
            ConnectionStringWith("Hello"), CancellationToken.None))!;

        entry.ConnectionStringTemplate.Should().NotContain("Hello");
        string? materialised = await vm.MaterialiseAsync(entry, CancellationToken.None);

        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(materialised!)
            .Password.Should().Be("Hello");
    }

    [AvaloniaFact]
    public async Task A_template_written_by_the_old_regex_still_materialises()
    {
        // Entries already on disk were produced by the regex and may be
        // malformed. They must keep working rather than turning into a null.
        InMemoryConnectionStore conns = new();
        InMemoryCredentialStore creds = new();
        ConnectionStoreViewModel vm = new(conns, creds);
        var id = Guid.NewGuid();
        ConnectionEntry legacy = new(
            id, "old", "srv", "db",
            "Server=srv;Database=db;User Id=sa;Password={PASSWORD};TrustServerCertificate=True",
            "Dev", "#0054BD", false, DateTime.UtcNow, DateTime.UtcNow);
        await vm.UpsertExplicitAsync(legacy, "Hello", CancellationToken.None);

        string? materialised = await vm.MaterialiseAsync(legacy, CancellationToken.None);

        materialised.Should().NotBeNull();
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(materialised!)
            .Password.Should().Be("Hello");
    }
}
