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
    public async Task Typing_a_credential_never_fires_a_login_on_its_own()
    {
        // The other half of the same rule. Clearing the fields stops the OLD
        // server's login reaching the new host; this stops a HALF-TYPED one
        // reaching any host. Both fields commit per keystroke, so the 450 ms
        // debounce turned an ordinary pause mid-secret into a real login with
        // the prefix typed so far — "Errore: Login failed for user 'sa'." in
        // the modal, for a connection nobody asked for, repeated at every
        // further pause. Reported from the installed v1.1.0 on 2026-09-03.
        // Order matters and the initialiser preserves it: naming the server
        // clears the credentials, so they land after it, exactly as a user
        // types them. ".invalid" cannot resolve, so an attempt that DID start
        // is guaranteed to leave a trace in ConnectionStatusMessage.
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "dbdelta-nonesistente.invalid",
            UserName = "sa",
            Password = "p4ss",
        };

        // Twice the 450 ms debounce, and then some.
        await Task.Delay(900, TestContext.Current.CancellationToken);

        vm.IsLoadingDatabases.Should().BeFalse("nothing may be sent while the user is still typing");
        vm.ConnectionStatusMessage.Should().BeNull("no attempt means no failure to report");
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
