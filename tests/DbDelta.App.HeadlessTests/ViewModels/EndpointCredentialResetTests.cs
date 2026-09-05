using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// A login typed for one server must never reach another one by itself — and
/// the user must not have to retype it to get that guarantee.
/// </summary>
/// <remarks>
/// From 2026-08-18 to 2026-09-03 this file pinned the opposite arrangement:
/// changing the server name WIPED both credential fields. The threat was real —
/// the panel auto-connects 450 ms after the last edit, and the host may have
/// arrived from an unauthenticated UDP scan reply, over a connection string
/// carrying this panel's <c>TrustServerCertificate</c> — but the price was paid
/// by the wrong party. The commit fires per keystroke, so someone going back to
/// append <c>\SQLSTERI</c> lost the secret they were halfway through, silently;
/// and the ordinary gesture — fill the credentials while the scan is still
/// running, then pick the server from «Risultati scansione» when it appears —
/// went through the same setter and cleared them too.
/// <para>
/// What actually had to be denied was the AUTO-CONNECT, not the typing. It is
/// denied at its own door now: <c>ScheduleAutoConnect</c> refuses to arm under
/// SQL auth unless the caller can say the pair belongs to the server now named,
/// which only <c>TryAutoFillCredentialsAsync</c> can. The fields survive;
/// nothing is sent until «Connetti», with the server name on screen.
/// </para>
/// <para>
/// <c>DatabaseName</c> is cleared in their place, and that is not a swap of one
/// annoyance for another: a catalog provably belongs to a server, the list
/// beside it was already being cleared, and it is what keeps OK from lighting up
/// against a target nobody confirmed. Measured before writing: all six call
/// sites that assign both fields write <c>DatabaseName</c> after
/// <c>ServerName</c>, so none of them loses a value it was about to set.
/// </para>
/// </remarks>
public class EndpointCredentialResetTests
{
    private sealed class StoreWithOneRememberedServer : ICredentialStore
    {
        public string? RememberedFor { get; init; }
        public bool IsAvailable => true;

        public Task SetSecretAsync(string key, string secret, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string?> GetSecretAsync(string key, CancellationToken ct) =>
            Task.FromResult(
                RememberedFor is not null && key.Contains(RememberedFor, StringComparison.Ordinal)
                    ? "stored-user|stored-pass"
                    : null);

        public Task DeleteSecretAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void Changing_the_server_keeps_the_credentials_the_user_typed()
    {
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "sql-a",
            UserName = "sa",
            Password = "p4ssw0rd",
        };

        vm.ServerName = "sql-b";

        vm.UserName.Should().Be("sa");
        vm.Password.Should().Be("p4ssw0rd");
    }

    [Fact]
    public async Task But_they_are_not_sent_to_the_new_server_on_their_own()
    {
        // The half that matters, and the reason the fields could be spared:
        // surviving in the boxes is not the same as being sent. ".invalid"
        // cannot resolve, so an attempt that DID start leaves IsLoadingDatabases
        // true for the ten seconds of ListDatabasesAsync's ConnectTimeout.
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "sql-a",
            UserName = "sa",
            Password = "p4ssw0rd",
        };

        vm.ServerName = "dbdelta-nonesistente.invalid";
        await Task.Delay(900, TestContext.Current.CancellationToken);

        vm.IsLoadingDatabases.Should().BeFalse("nothing may reach the new host unasked");
        vm.ConnectionStatusMessage.Should().BeNull();
    }

    [Fact]
    public void Changing_the_server_clears_the_database_instead()
    {
        // What now keeps OK from lighting up against a target nobody confirmed.
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

        vm.DatabaseName.Should().BeEmpty();
        vm.IsValid.Should().BeFalse("the catalog has to be re-picked on the new host");
    }

    [Fact]
    public async Task A_remembered_pair_still_connects_the_moment_its_server_is_picked()
    {
        // The negative control on the guard: it must deny the pair that belongs
        // to another server WITHOUT denying the one that belongs to this one.
        // Without this, a guard that simply never armed would pass every test
        // above and quietly kill the feature.
        StoreWithOneRememberedServer store = new() { RememberedFor = "nonesistente" };
        ProjectEndpointPanelViewModel vm =
            new("Sorgente", isTarget: false, store) { ServerName = "dbdelta-nonesistente.invalid" };

        await Task.Delay(900, TestContext.Current.CancellationToken);

        vm.UserName.Should().Be("stored-user");
        vm.IsLoadingDatabases.Should().BeTrue("a pair the store filed under THIS server may connect");
    }

    [Fact]
    public async Task Typing_a_credential_never_fires_a_login_on_its_own()
    {
        // The third rule, unchanged by any of the above and still the one the
        // 2026-09-03 report was about. Both fields commit per keystroke, so the
        // 450 ms debounce turned an ordinary pause mid-secret into a real login
        // with the prefix typed so far — "Errore: Login failed for user 'sa'."
        // in the modal, for a connection nobody asked for, repeated at every
        // further pause.
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "dbdelta-nonesistente.invalid",
            UserName = "sa",
            Password = "p4ss",
        };

        await Task.Delay(900, TestContext.Current.CancellationToken);

        vm.IsLoadingDatabases.Should().BeFalse("nothing may be sent while the user is still typing");
        vm.ConnectionStatusMessage.Should().BeNull("no attempt means no failure to report");
    }

    [Fact]
    public void Windows_authentication_is_unaffected()
    {
        // There are no credentials to leak under integrated auth, so "pick a
        // server and it connects itself" stays. Only the catalog is re-asked.
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false)
        {
            ServerName = "sql-a",
            DatabaseName = "AdventureWorks",
            AuthMode = AuthenticationMode.WindowsIntegrated,
        };

        vm.ServerName = "sql-b";

        vm.DatabaseName.Should().BeEmpty();
        vm.ServerName.Should().Be("sql-b");
    }
}
