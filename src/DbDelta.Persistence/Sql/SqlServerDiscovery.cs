using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DbDelta.Persistence.Sql;

/// <summary>One SQL Server reachable via the Browser service. <see cref="IpAddress"/>
/// is the source IPv4 of the UDP response and is <c>null</c> for local aliases.</summary>
public sealed record DiscoveredServer(string Name, string? IpAddress);

/// <summary>
/// SQL Server discovery utilities.
///
/// <see cref="EnumerateServersAsync"/> implements the SQL Server Resolution
/// Protocol (UDP/1434): broadcasts a single 0x02 byte to 255.255.255.255 and
/// to every IPv4 broadcast address derived from local network interfaces,
/// then collects responses for up to the timeout window. Responses
/// from the SQL Browser service are ASCII payloads of the form
/// <c>ServerName;HOST;InstanceName;INST;IsClustered;No;Version;15.0.2000.5;tcp;1433;;</c>.
/// Servers without the Browser service running (or behind a firewall blocking
/// UDP/1434) will simply not respond — the result is best-effort. The picker
/// UI always allows free-text entry.
///
/// <see cref="ListDatabasesAsync"/> queries <c>sys.databases</c> on a
/// connected server.
/// </summary>
public static class SqlServerDiscovery
{
    private const int SqlBrowserPort = 1434;
    private static readonly byte[] s_browserQuery = [0x02];
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(3);

    public static async Task<IReadOnlyList<DiscoveredServer>> EnumerateServersAsync(
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        TimeSpan window = timeout ?? s_defaultTimeout;
        // Captured server name → IP (last write wins, but multiple NICs will
        // typically return identical results so this is fine).
        Dictionary<string, string?> seen = new(StringComparer.OrdinalIgnoreCase)
        {
            ["(local)"] = null,
            ["localhost"] = null,
            ["127.0.0.1"] = null,
        };

        // Best-effort send + collect. Each task owns its own UdpClient
        // because UDP is connectionless and concurrent sends from a
        // single socket would compete with the receive loop.
        List<Task> sendTasks = [];

        // 1) Global broadcast
        sendTasks.Add(BroadcastAsync(IPAddress.Broadcast, ct));

        // 2) Per-interface directed broadcast
        foreach (IPAddress bcast in GetInterfaceBroadcastAddresses())
        {
            sendTasks.Add(BroadcastAsync(bcast, ct));
        }

        // 3) Localhost — picks up local named instances even when Browser
        //    refuses to respond to network broadcasts.
        sendTasks.Add(BroadcastAsync(IPAddress.Loopback, ct));

        // The receive socket must listen on ANY:0 with broadcast enabled.
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Any, 0))
        {
            EnableBroadcast = true,
        };
        receiver.Client.ReceiveTimeout = (int)Math.Min(window.TotalMilliseconds, int.MaxValue);

        // Send the query on the same socket too — some firewalls only
        // allow responses to the sending port.
        try
        {
            await receiver.SendAsync(s_browserQuery, s_browserQuery.Length,
                new IPEndPoint(IPAddress.Broadcast, SqlBrowserPort)).ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        DateTime deadline = DateTime.UtcNow + window;
        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                cts.CancelAfter(remaining);
                UdpReceiveResult result;
                try
                {
                    result = await receiver.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                string sourceIp = result.RemoteEndPoint.Address.ToString();
                foreach (string entry in ParseSqlBrowserResponse(result.Buffer))
                {
                    seen[entry] = sourceIp;
                }
            }
        }
        finally
        {
            try { await Task.WhenAll(sendTasks).ConfigureAwait(false); } catch { /* swallow */ }
        }

        // Stable order: localhost-like aliases first, then alphabetical.
        List<DiscoveredServer> ordered = [.. seen.OrderBy(kv =>
            kv.Key.Equals("(local)", StringComparison.OrdinalIgnoreCase) ? 0 :
            kv.Key.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? 1 :
            kv.Key.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new DiscoveredServer(kv.Key, kv.Value))];
        return ordered;
    }

    private static async Task BroadcastAsync(IPAddress destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using UdpClient sender = new() { EnableBroadcast = true };
            await sender.SendAsync(s_browserQuery, s_browserQuery.Length,
                new IPEndPoint(destination, SqlBrowserPort)).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    private static IEnumerable<IPAddress> GetInterfaceBroadcastAddresses()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            foreach (UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                IPAddress mask = ip.IPv4Mask;
                if (mask is null || mask.Equals(IPAddress.Any))
                {
                    continue;
                }
                byte[] addr = ip.Address.GetAddressBytes();
                byte[] m = mask.GetAddressBytes();
                byte[] bc = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    bc[i] = (byte)(addr[i] | (~m[i] & 0xff));
                }
                yield return new IPAddress(bc);
            }
        }
    }

    /// <summary>
    /// Parses a SQL Browser response packet. The payload starts with a 3-byte
    /// header (0x05 followed by little-endian 16-bit length), followed by an
    /// ASCII string with key/value pairs separated by ';' and ending with ";;"
    /// before the next instance block.
    /// </summary>
    private static IEnumerable<string> ParseSqlBrowserResponse(byte[] buffer)
    {
        if (buffer.Length < 3 || buffer[0] != 0x05)
        {
            yield break;
        }
        string payload = Encoding.ASCII.GetString(buffer, 3, buffer.Length - 3);
        // Split on ";;" to separate instance blocks
        foreach (string block in payload.Split(";;", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = block.Split(';');
            string? server = null;
            string? instance = null;
            for (int i = 0; i + 1 < tokens.Length; i += 2)
            {
                string key = tokens[i];
                string value = tokens[i + 1];
                if (string.Equals(key, "ServerName", StringComparison.OrdinalIgnoreCase))
                {
                    server = value;
                }
                else if (string.Equals(key, "InstanceName", StringComparison.OrdinalIgnoreCase))
                {
                    instance = value;
                }
            }
            if (string.IsNullOrWhiteSpace(server))
            {
                continue;
            }
            yield return string.IsNullOrWhiteSpace(instance) || instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                ? server
                : $"{server}\\{instance}";
        }
    }

    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionString, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlConnectionStringBuilder builder = new(connectionString)
        {
            ConnectTimeout = 10,
            InitialCatalog = "master",
        };
        List<string> result = [];
        await using SqlConnection cn = new(builder.ConnectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state_desc = 'ONLINE'
            ORDER BY name;
            """;
        await using SqlCommand cmd = new(sql, cn);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(r.GetString(0));
        }
        return result;
    }

    /// <summary>Queries SERVERPROPERTY('ProductMajorVersion') + ('Edition') and
    /// returns a friendly string like "SQL Server 2022 Developer Edition", or
    /// null if the connection fails.</summary>
    public static async Task<string?> GetServerVersionAsync(string connectionString, CancellationToken ct)
    {
        SqlConnectionStringBuilder b = new(connectionString) { ConnectTimeout = 5 };
        await using SqlConnection cn = new(b.ConnectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        const string sql = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT), CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128));";
        await using SqlCommand cmd = new(sql, cn);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) { return null; }
        int major = r.IsDBNull(0) ? 0 : r.GetInt32(0);
        string edition = r.IsDBNull(1) ? "" : r.GetString(1);
        string product = major switch
        {
            16 => "SQL Server 2022",
            15 => "SQL Server 2019",
            14 => "SQL Server 2017",
            13 => "SQL Server 2016",
            12 => "SQL Server 2014",
            11 => "SQL Server 2012",
            10 => "SQL Server 2008",
            _ => $"SQL Server (v{major})",
        };
        return $"{product} — {edition}";
    }
}
