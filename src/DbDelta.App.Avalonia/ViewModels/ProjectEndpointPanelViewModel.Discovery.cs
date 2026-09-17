using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.ViewModels;

// The picker's world: the network scan, the suggestions list and the
// sections (recents, scan results) it is split into, the recents seeded
// from the connection store, the band name and the counters derived from
// it, and the DNS lookup that fills in the IP the band shows.
public sealed partial class ProjectEndpointPanelViewModel
{
    /// <summary>
    /// IPv4 address of the currently-selected server, when it matches a row
    /// in <see cref="ServerSuggestions"/>. Used to show the IP next to the
    /// server name in the band header.
    /// </summary>
    [ObservableProperty] private string? _serverIpAddress;

    // ── Server scan state ────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<DiscoveredServer> _serverSuggestions = [];
    [ObservableProperty] private bool _hasServerSuggestions;
    [ObservableProperty] private bool _isScanningServers;
    [ObservableProperty] private string? _scanStatusMessage;

    // ── Derived display properties ────────────────────────────────────────────

    /// <summary>
    /// Friendly display name shown in the Source/Target band header.
    /// Falls back to a placeholder when ServerName is empty. Appends the IP
    /// in parentheses when known (resolved from <see cref="ServerSuggestions"/>).
    /// </summary>
    public string DisplayBandName => string.IsNullOrWhiteSpace(ServerName)
        ? (IsTarget ? "Seleziona una destinazione…" : "Seleziona una provenienza…")
        : (string.IsNullOrWhiteSpace(ServerIpAddress)
            ? ServerName
            : $"{ServerName}  ({ServerIpAddress})");

    /// <summary>Inline counter text shown next to the "Server" section label.</summary>
    // Rows that can be picked: the list also carries the IsHeaderOnly
    // sentinels that draw the section dividers, and counting them said one
    // more than the user could choose, two with both sections on screen.
    public string ServerCountText => $"({ServerSuggestions.Count(s => !s.IsHeaderOnly)} trovati)";

    /// <summary>Inline counter text shown next to the "Database" section label.</summary>
    public string DatabaseCountText => $"({AvailableDatabases.Count} trovati)";

    partial void OnServerIpAddressChanged(string? value)
        => OnPropertyChanged(nameof(DisplayBandName));

    partial void OnIsScanningServersChanged(bool value) =>
        ScanServersCommand.NotifyCanExecuteChanged();

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanScanServers))]
    public async Task ScanServersAsync()
    {
        IsScanningServers = true;
        HasServerSuggestions = false;
        ScanStatusMessage = "Scansione in corso…";
        try
        {
            IReadOnlyList<DiscoveredServer> list =
                await SqlServerDiscovery.EnumerateServersAsync(_lifetime.Token)
                                        .ConfigureAwait(true);
            ApplyScanResults(list);
        }
        catch (Exception ex)
        {
            ScanStatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsScanningServers = false;
        }
    }

    private bool CanScanServers() => !IsScanningServers;

    /// <summary>
    /// Replaces <see cref="ServerSuggestions"/> with the given list and refreshes
    /// <see cref="ServerIpAddress"/> for the currently-typed <see cref="ServerName"/>
    /// (if any). Public so the parent VM can push a single shared scan into
    /// both source and target panels at once.
    /// </summary>
    public void ApplyScanResults(IReadOnlyList<DiscoveredServer> list)
    {
        ArgumentNullException.ThrowIfNull(list);

        // Preserve any "Usati di recente" prefix (header + items) when
        // refreshing — only replace the "Risultati scansione" tail.
        List<DiscoveredServer> recents = [..
            ServerSuggestions.Where(s => string.Equals(s.Section, RecentSection, StringComparison.Ordinal))];

        ServerSuggestions.Clear();
        foreach (DiscoveredServer r in recents)
        {
            ServerSuggestions.Add(r);
        }

        if (list.Count > 0)
        {
            // Dedicated non-selectable header sentinel — visually a divider
            // strip, never returned by AutoCompleteBox text selection.
            ServerSuggestions.Add(new DiscoveredServer(
                Name: string.Empty,
                IpAddress: null,
                Section: ScanSection,
                IsSectionFirst: true,
                IsHeaderOnly: true));

            foreach (DiscoveredServer s in list)
            {
                ServerSuggestions.Add(s with { Section = ScanSection, IsSectionFirst = false });
            }
        }

        HasServerSuggestions = ServerSuggestions.Count > 0;
        ScanStatusMessage = list.Count == 0
            ? "Nessun server rilevato (SQL Browser potrebbe essere disabilitato)."
            : null;

        // Refresh IP for the currently-typed server name, if it matches a row.
        if (!string.IsNullOrWhiteSpace(ServerName))
        {
            ServerIpAddress = list
                .FirstOrDefault(s => string.Equals(s.Name, ServerName, StringComparison.OrdinalIgnoreCase))?
                .IpAddress;
        }
    }

    /// <summary>Section label for previously-used server entries.</summary>
    public const string RecentSection = "Usati di recente";

    /// <summary>Section label for network scan results.</summary>
    public const string ScanSection = "Risultati scansione";

    /// <summary>
    /// Prepends a set of "recently used" servers to <see cref="ServerSuggestions"/>.
    /// Called once from <c>App</c> right after the connection-store load so the
    /// modal opens with the user's known servers already visible — even before
    /// a network scan completes.
    /// </summary>
    public void SeedRecentServers(IReadOnlyList<(string Name, string? IpAddress)> recents)
    {
        if (recents is null || recents.Count == 0) { return; }

        // Strip any previous "recent" entries (header + data) so we don't
        // duplicate them across multiple seedings.
        for (int i = ServerSuggestions.Count - 1; i >= 0; i--)
        {
            if (string.Equals(ServerSuggestions[i].Section, RecentSection, StringComparison.Ordinal))
            {
                ServerSuggestions.RemoveAt(i);
            }
        }

        // Build the new "Usati di recente" block: one header sentinel + the
        // recent server data items.
        List<DiscoveredServer> block =
        [
            new DiscoveredServer(
                Name: string.Empty,
                IpAddress: null,
                Section: RecentSection,
                IsSectionFirst: true,
                IsHeaderOnly: true),
        ];
        foreach ((string name, string? ip) in recents)
        {
            block.Add(new DiscoveredServer(
                Name: name,
                IpAddress: ip,
                Section: RecentSection,
                IsSectionFirst: false));
        }

        // Prepend the recents block at the top of the suggestions list.
        for (int i = 0; i < block.Count; i++)
        {
            ServerSuggestions.Insert(i, block[i]);
        }

        HasServerSuggestions = ServerSuggestions.Count > 0;
    }

    [RelayCommand]
    public void PickServer(DiscoveredServer? server)
    {
        if (server is not null && !string.IsNullOrWhiteSpace(server.Name))
        {
            ServerName = server.Name;
        }
    }

    // ── DNS resolution (IP fallback) ─────────────────────────────────────────

    /// <summary>
    /// Best-effort DNS lookup to surface the IPv4 of a server typed manually
    /// (i.e. not picked from the scan list). Strips any "\instance" suffix
    /// and any "tcp:" prefix. Returns null on lookup failure.
    /// </summary>
    private static async Task<string?> TryResolveIpAsync(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) { return null; }
        string host = serverName.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) { host = host[4..]; }
        int comma = host.IndexOf(',');           // strip ",port"
        if (comma > 0) { host = host[..comma]; }
        int slash = host.IndexOf('\\');          // strip "\instance"
        if (slash > 0) { host = host[..slash]; }
        if (string.IsNullOrWhiteSpace(host)) { return null; }

        // If the user already typed an IP literal, just keep it.
        if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? literal))
        {
            return literal.ToString();
        }

        try
        {
            System.Net.IPHostEntry entry =
                await System.Net.Dns.GetHostEntryAsync(host).ConfigureAwait(true);
            System.Net.IPAddress? v4 = entry.AddressList.FirstOrDefault(
                a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return v4?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
