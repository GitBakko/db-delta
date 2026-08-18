using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbDelta.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Holds the MRU connection list + the search term + filter, and exposes
/// commands to autosave a raw connection string on Compare success and to
/// materialise a stored entry back into a complete connection string at
/// run time.
/// </summary>
public sealed partial class ConnectionStoreViewModel(
    IConnectionStore store,
    ICredentialStore credentials) : ObservableObject
{
    private const string KeyPrefix = "dbdelta:connection:";

    /// <summary>
    /// What stands in for the password inside a stored template. Written by
    /// <see cref="Templatise"/> and read back by <see cref="MaterialiseAsync"/>;
    /// other call sites spell it out inline in their own literals.
    /// </summary>
    private const string PasswordPlaceholder = "{PASSWORD}";

    public ObservableCollection<ConnectionEntry> Entries { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredEntries));

    public IReadOnlyList<ConnectionEntry> FilteredEntries => string.IsNullOrWhiteSpace(SearchText)
        ? [.. Entries]
        : [.. Entries.Where(e =>
            e.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || e.ServerName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || e.DatabaseName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || e.EnvironmentTag.Contains(SearchText, StringComparison.OrdinalIgnoreCase))];

    public async Task LoadAsync(CancellationToken ct)
    {
        IReadOnlyList<ConnectionEntry> list = await store.LoadAsync(ct).ConfigureAwait(true);
        Entries.Clear();
        foreach (ConnectionEntry e in list)
        {
            Entries.Add(e);
        }
        OnPropertyChanged(nameof(FilteredEntries));
    }

    public async Task<ConnectionEntry?> AutosaveAsync(string rawConnectionString, CancellationToken ct)
    {
        SqlConnectionStringBuilder builder;
        try { builder = new SqlConnectionStringBuilder(rawConnectionString); }
        catch { return null; }

        string server = builder.DataSource ?? "";
        string database = builder.InitialCatalog ?? "";
        if (Entries.FirstOrDefault(e =>
            string.Equals(e.ServerName, server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.DatabaseName, database, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            ConnectionEntry touched = existing with { LastUsedUtc = DateTime.UtcNow };
            await store.UpsertAsync(touched, ct).ConfigureAwait(true);
            ReplaceInCollection(touched);
            return touched;
        }

        string password = builder.Password ?? "";
        string template = Templatise(builder);
        var id = Guid.NewGuid();
        ConnectionEntry entry = new(
            Id: id,
            Name: $"{server}.{database} (auto)",
            ServerName: server,
            DatabaseName: database,
            ConnectionStringTemplate: template,
            EnvironmentTag: "(auto)",
            EnvironmentColorHex: "#9097A0",
            IsPinned: false,
            CreatedUtc: DateTime.UtcNow,
            LastUsedUtc: DateTime.UtcNow);

        if (!string.IsNullOrEmpty(password) && credentials.IsAvailable)
        {
            await credentials.SetSecretAsync(KeyPrefix + id.ToString("D"), password, ct).ConfigureAwait(true);
        }
        await store.UpsertAsync(entry, ct).ConfigureAwait(true);
        Entries.Add(entry);
        OnPropertyChanged(nameof(FilteredEntries));
        return entry;
    }

    public async Task<string?> MaterialiseAsync(ConnectionEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.ConnectionStringTemplate.Contains(PasswordPlaceholder, StringComparison.Ordinal))
        {
            return entry.ConnectionStringTemplate;
        }
        if (!credentials.IsAvailable)
        {
            return null;
        }
        string? password = await credentials.GetSecretAsync(KeyPrefix + entry.Id.ToString("D"), ct).ConfigureAwait(true);
        if (password is null) { return null; }

        // Put the secret back through the builder rather than pasting it into
        // the text. A password containing ; or " needs quoting to survive as a
        // value, and a plain Replace produces a string that parses as something
        // else — or refuses to parse at all.
        try
        {
            SqlConnectionStringBuilder materialised = new(entry.ConnectionStringTemplate)
            {
                Password = password,
            };
            return materialised.ConnectionString;
        }
        catch (ArgumentException)
        {
            // A template written by an older build could be malformed — the
            // regex it came from left stray fragments behind. Fall back to the
            // substitution that build used, so those entries keep working.
            return entry.ConnectionStringTemplate.Replace(PasswordPlaceholder, password, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Persist an explicit entry (used by the Edit dialog). When
    /// <paramref name="password"/> is non-null and the credential store
    /// is available, write it under <c>"dbdelta:connection:{Id:D}"</c>.
    /// Replaces any existing entry with the same Id in the local
    /// ObservableCollection.
    /// </summary>
    public async Task<ConnectionEntry> UpsertExplicitAsync(ConnectionEntry entry, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrEmpty(password) && credentials.IsAvailable)
        {
            await credentials.SetSecretAsync(KeyPrefix + entry.Id.ToString("D"), password, ct).ConfigureAwait(true);
        }
        await store.UpsertAsync(entry, ct).ConfigureAwait(true);

        int idx = Entries.ToList().FindIndex(e => e.Id == entry.Id);
        if (idx >= 0)
        {
            Entries[idx] = entry;
        }
        else
        {
            Entries.Add(entry);
        }
        OnPropertyChanged(nameof(FilteredEntries));
        return entry;
    }

    /// <summary>
    /// Delete an entry + its credential. Used by the Edit dialog "Elimina" button.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (credentials.IsAvailable)
        {
            try { await credentials.DeleteSecretAsync(KeyPrefix + id.ToString("D"), ct).ConfigureAwait(true); }
            catch { /* best-effort */ }
        }
        await store.DeleteAsync(id, ct).ConfigureAwait(true);

        int idx = Entries.ToList().FindIndex(e => e.Id == id);
        if (idx >= 0)
        {
            Entries.RemoveAt(idx);
        }
        OnPropertyChanged(nameof(FilteredEntries));
    }

    /// <summary>
    /// Returns the stored password for <paramref name="id"/>, or
    /// <see langword="null"/> when the credential store is unavailable or
    /// no secret has been written for this entry.
    /// </summary>
    public Task<string?> GetSecretPasswordAsync(Guid id, CancellationToken ct) =>
        credentials.IsAvailable
            ? credentials.GetSecretAsync(KeyPrefix + id.ToString("D"), ct)
            : Task.FromResult<string?>(null);

    private void ReplaceInCollection(ConnectionEntry entry)
    {
        int idx = Entries.ToList().FindIndex(e => e.Id == entry.Id);
        if (idx >= 0)
        {
            Entries[idx] = entry;
        }
        OnPropertyChanged(nameof(FilteredEntries));
    }

    /// <summary>
    /// The connection string with its password swapped for
    /// <see cref="PasswordPlaceholder"/>, ready to be written to disk.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="SqlConnectionStringBuilder"/>, not through a
    /// regular expression. The regex this replaces matched
    /// <c>(password|pwd)\s*=\s*[^;]+</c>, and a connection-string value may
    /// legally contain a semicolon when it is quoted — <c>Password='a;b'</c>.
    /// The match stopped at that semicolon, so <c>b'</c> stayed in the template
    /// and a fragment of the real password was persisted in clear text, at every
    /// successful comparison. The builder knows the quoting rules; nothing here
    /// has to.
    /// </remarks>
    private static string Templatise(SqlConnectionStringBuilder builder)
    {
        // A copy, so the caller's builder keeps the real password it still needs
        // to hand to the credential store.
        SqlConnectionStringBuilder template = new(builder.ConnectionString)
        {
            Password = PasswordPlaceholder,
        };
        return template.ConnectionString;
    }
}
