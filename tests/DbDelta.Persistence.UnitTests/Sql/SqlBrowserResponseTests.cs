using System.Text;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Sql;

/// <summary>
/// The SQL Browser reply is a UDP datagram from whoever answers first on a
/// broadcast — a trust boundary, and the only one in this codebase where the
/// bytes are not ours. The names it carries end up in a picker the user clicks,
/// and from there in a connection string.
/// </summary>
/// <remarks>
/// Nothing leaves on its own towards a suggested host any more — the credential
/// fields are cleared when the server changes — so this is hygiene rather than
/// exposure. It is still the one parser where "the packet said so" is not a
/// reason to believe anything.
/// </remarks>
public class SqlBrowserResponseTests
{
    /// <summary>Builds a well-formed reply: 0x05, LE-16 length, then the payload.</summary>
    private static byte[] Packet(string payload, int? declaredLength = null)
    {
        byte[] body = Encoding.ASCII.GetBytes(payload);
        int declared = declaredLength ?? body.Length;
        byte[] packet = new byte[3 + body.Length];
        packet[0] = 0x05;
        packet[1] = (byte)(declared & 0xFF);
        packet[2] = (byte)((declared >> 8) & 0xFF);
        body.CopyTo(packet, 3);
        return packet;
    }

    private static string Block(string server, string instance = "MSSQLSERVER") =>
        $"ServerName;{server};InstanceName;{instance};IsClustered;No;Version;16.0.1000.6;;";

    [Fact]
    public void A_well_formed_reply_yields_its_instances()
    {
        byte[] packet = Packet(Block("SQLPROD") + Block("SQLTEST", "DEV"));

        SqlServerDiscovery.ParseSqlBrowserResponse(packet)
            .Should().Equal("SQLPROD", "SQLTEST\\DEV");
    }

    [Fact]
    public void Bytes_past_the_declared_length_are_not_read()
    {
        // The header says how long the payload is. Reading to the end of the
        // datagram instead means whatever the sender appended is parsed too.
        byte[] packet = Packet(Block("SQLPROD") + Block("SQLEVIL"), declaredLength: Block("SQLPROD").Length);

        SqlServerDiscovery.ParseSqlBrowserResponse(packet).Should().Equal("SQLPROD");
    }

    [Theory]
    [InlineData("SQL PROD")]                       // a space: not a host name
    [InlineData("SQLPROD")]                  // a control character
    [InlineData("SQLPROD'--")]                     // quote, on its way to a connection string
    [InlineData("Server=evil;Database=x")]         // a connection-string fragment, minus the ';'
    public void A_name_that_is_not_a_host_name_is_dropped(string hostile) => SqlServerDiscovery.ParseSqlBrowserResponse(Packet(Block(hostile))).Should().BeEmpty();

    [Fact]
    public void A_name_longer_than_SQL_Server_allows_is_dropped()
    {
        SqlServerDiscovery.ParseSqlBrowserResponse(Packet(Block(new string('a', 129))))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_reply_claiming_hundreds_of_instances_is_cut_short()
    {
        StringBuilder sb = new();
        for (int i = 0; i < 500; i++) { sb.Append(Block($"SQL{i}")); }

        SqlServerDiscovery.ParseSqlBrowserResponse(Packet(sb.ToString()))
            .Should().HaveCountLessThanOrEqualTo(64);
    }

    [Theory]
    [InlineData(new byte[] { 0x04, 0x00, 0x00 })]  // wrong leading byte
    [InlineData(new byte[] { 0x05 })]              // truncated header
    [InlineData(new byte[0])]                      // nothing at all
    public void A_packet_that_is_not_a_browser_reply_yields_nothing(byte[] packet) => SqlServerDiscovery.ParseSqlBrowserResponse(packet).Should().BeEmpty();

    [Fact]
    public void A_dotted_or_hyphenated_host_name_is_still_accepted()
    {
        // The negative control on the allow-list: real names carry dots,
        // hyphens and underscores, and an instance name may hold $ and #.
        SqlServerDiscovery.ParseSqlBrowserResponse(Packet(Block("sql-prod.corp.local", "APP_1$")))
            .Should().Equal("sql-prod.corp.local\\APP_1$");
    }
}
