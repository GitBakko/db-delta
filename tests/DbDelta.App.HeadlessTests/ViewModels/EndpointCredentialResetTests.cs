using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// Credentials belong to the server that was named when they were typed. When
/// the server name changes they must not survive into the next connection.
/// </summary>
/// <remarks>
/// The panel auto-connects 450 ms after the last edit. With the credentials
/// left in place, changing the server sent the previous server's login to the
/// new host without anyone pressing anything — and the host may have come from
/// the scan, i.e. from an unauthenticated UDP reply, over a connection string
/// that carries <c>TrustServerCertificate</c> from the panel. That is
/// credential disclosure, not friction.
/// </remarks>
public class EndpointCredentialResetTests
{
    [Fact]
    public void Changing_the_server_clears_the_credentials()
    {
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "sql-a",
            UserName = "sa",
            Password = "p4ssw0rd",
        };

        vm.ServerName = "sql-b";

        vm.UserName.Should().BeEmpty();
        vm.Password.Should().BeEmpty();
    }

    [Fact]
    public void With_the_credentials_gone_the_panel_is_no_longer_ready_to_connect()
    {
        // The property that matters is not the emptiness itself but what it
        // denies: under SQL auth both fields are required, so the debounced
        // auto-connect finds nothing to send. IsValid is the same predicate the
        // eligibility check uses.
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "sql-a",
            DatabaseName = "AdventureWorks",
            AuthMode = AuthenticationMode.SqlServer,
            UserName = "sa",
            Password = "p4ssw0rd",
        };
        vm.IsValid.Should().BeTrue("the starting point is a panel that would connect");

        vm.ServerName = "sql-b";

        vm.IsValid.Should().BeFalse("nothing may be sent to the new host until it is re-entered");
    }

    [Fact]
    public void Windows_authentication_is_unaffected()
    {
        // The negative control. There are no credentials to leak under
        // integrated auth, and the panel must stay ready to connect.
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "sql-a",
            DatabaseName = "AdventureWorks",
            AuthMode = AuthenticationMode.WindowsIntegrated,
        };

        vm.ServerName = "sql-b";

        vm.IsValid.Should().BeTrue();
    }
}
