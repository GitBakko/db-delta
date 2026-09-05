using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.Abstractions;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// Closing the setup dialog must stop what the dialog started.
/// </summary>
/// <remarks>
/// The dialog had one lifecycle hook, <c>Opened</c>. Nothing cancelled the
/// 450 ms auto-connect, the shared scan, or a load already under way, and every
/// SQL call passed <see cref="CancellationToken.None"/> — so up to ~20 s of work
/// ran against a server on behalf of a window the user had dismissed, and on the
/// success path a Credential Manager entry was written or DELETED after
/// «Annulla».
/// <para>
/// Measured while fixing it: <c>DpapiCredentialStore</c> ignores the token it is
/// given — both writes are synchronous and unconditional. Threading the token
/// into those calls therefore stops nothing on its own; the explicit
/// <c>ThrowIfCancellationRequested</c> before them is what does.
/// </para>
/// </remarks>
public class ModalLifetimeTests
{
    /// <summary>Records what was asked of it, and honours no token — like the real one.</summary>
    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public List<string> Writes { get; } = [];
        public List<string> Deletes { get; } = [];
        public string? Stored { get; set; }

        public bool IsAvailable => true;

        public Task SetSecretAsync(string key, string secret, CancellationToken ct)
        {
            Writes.Add(key);
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string key, CancellationToken ct) =>
            Task.FromResult(Stored);

        public Task DeleteSecretAsync(string key, CancellationToken ct)
        {
            Deletes.Add(key);
            return Task.CompletedTask;
        }
    }

    // Cannot resolve (RFC 2606), so an attempt that DID start is guaranteed to
    // leave a trace in ConnectionStatusMessage instead of hanging on a real host.
    private const string Server = "dbdelta-nonesistente.invalid";

    private static ProjectEndpointPanelViewModel PanelWithARememberedLogin(
        RecordingCredentialStore store)
    {
        // A stored pair is the one path that legitimately arms the auto-connect:
        // TryAutoFillCredentialsAsync puts back a credential it knows is complete.
        store.Stored = "sa|p4ss";
        return new ProjectEndpointPanelViewModel("Sorgente", isTarget: false, store)
        {
            ServerName = Server,
        };
    }

    [Fact]
    public async Task Closing_the_dialog_stops_the_pending_auto_connect()
    {
        RecordingCredentialStore store = new();
        ProjectEndpointPanelViewModel vm = PanelWithARememberedLogin(store);

        vm.CancelPendingWork();

        // Twice the 450 ms debounce, and then some.
        await Task.Delay(900, TestContext.Current.CancellationToken);

        vm.IsLoadingDatabases.Should().BeFalse();
        vm.ConnectionStatusMessage.Should().BeNull("no attempt means no failure to report");
    }

    [Fact]
    public async Task Control_without_the_close_that_same_auto_connect_does_fire()
    {
        // The control that makes the test above worth anything: without it, a
        // panel that never armed the auto-connect in the first place would pass
        // it for the wrong reason. It caught exactly that on the first run —
        // the first version asserted ConnectionStatusMessage and failed, because
        // ListDatabasesAsync sets ConnectTimeout = 10 and the attempt is still
        // IN FLIGHT at 900 ms. In-flight is the signal, not the error.
        RecordingCredentialStore store = new();
        ProjectEndpointPanelViewModel vm = PanelWithARememberedLogin(store);

        await Task.Delay(900, TestContext.Current.CancellationToken);

        vm.IsLoadingDatabases.Should().BeTrue(
            "the remembered credential arms the auto-connect, which is still waiting on .invalid");
    }

    [Fact]
    public async Task A_closed_dialog_writes_nothing_to_the_credential_store()
    {
        // The half that survives a cancelled network call: TryPersistCredentials
        // runs on the success path of a load, and its else-branch DELETES.
        RecordingCredentialStore store = new();
        ProjectEndpointPanelViewModel vm = PanelWithARememberedLogin(store);
        vm.RememberCredentials = true;

        vm.CancelPendingWork();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);

        store.Writes.Should().BeEmpty();
        store.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_credential_write_that_starts_after_the_close_is_refused()
    {
        // The narrow half a cancelled network call does NOT cover: the load
        // succeeds, and the close lands between its last await and the persist.
        // Only an explicit check stops this one — DpapiCredentialStore ignores
        // the token it is handed, both of its writes being synchronous and
        // unconditional, so threading the token there stops nothing.
        RecordingCredentialStore store = new();
        ProjectEndpointPanelViewModel vm = PanelWithARememberedLogin(store);
        vm.RememberCredentials = true;
        vm.CancelPendingWork();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(vm.TryPersistCredentialsAsync);

        store.Writes.Should().BeEmpty();
        store.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Control_the_same_write_goes_through_while_the_dialog_is_open()
    {
        // Without this, a persist that had stopped working for any other reason
        // would read as the guard doing its job.
        RecordingCredentialStore store = new();
        ProjectEndpointPanelViewModel vm = PanelWithARememberedLogin(store);
        vm.RememberCredentials = true;

        await vm.TryPersistCredentialsAsync();

        store.Writes.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task The_dialog_itself_cancels_its_view_model_when_it_closes()
    {
        // The WIRING, not the mechanism. Every test above calls
        // CancelPendingWork by hand, so the Closed hook could be deleted and all
        // of them would stay green — the dialog's only lifecycle hook used to be
        // Opened, which is how this shipped in the first place.
        RecordingCredentialStore store = new();
        ProjectSetupViewModel setup = new(store);
        setup.Source.ServerName = Server;
        setup.Source.RememberCredentials = true;

        ProjectSetupDialog dialog = new() { DataContext = setup };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            setup.Source.TryPersistCredentialsAsync);
    }

    [Fact]
    public async Task Closing_the_dialog_stops_a_load_already_in_flight()
    {
        // The headline of the fix — "every SQL call ran on a token that could
        // not be cancelled" — and the 2026-09-05 review found it pinned by
        // nothing: against .invalid a load never reaches the persist whether the
        // token is honoured or not, so the test above stays green with
        // CancellationToken.None put back. This one starts the load, waits until
        // it is in flight, then closes: only an honoured token brings it back
        // within the deadline, instead of ~10 s later at ConnectTimeout.
        // Its own password on purpose: the physical attempt outlives the
        // cancellation and fails ~10 s later, and SqlClient then blocks that
        // pool for 5 s — a pool is keyed on the whole string, so sharing the
        // control's pair would make the control's outcome depend on the clock.
        RecordingCredentialStore store = new();
        ProjectEndpointPanelViewModel vm = new("Sorgente", isTarget: false, store)
        {
            ServerName = Server,
            UserName = "sa",
            Password = "in-flight-and-then-closed",
            RememberCredentials = true,
        };

        Task load = vm.LoadDatabasesCommand.ExecuteAsync(null);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        vm.IsLoadingDatabases.Should().BeTrue("the control: the load is in flight against .invalid");

        vm.CancelPendingWork();
        await load.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        vm.IsLoadingDatabases.Should().BeFalse();
        vm.ConnectionStatusMessage.Should().BeNull("a load stopped by the close is not an error");
        store.Writes.Should().BeEmpty();
        store.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_the_dialog_stops_both_panels_and_the_shared_scan()
    {
        // The parent's own token plus both children, in one call — the dialog
        // has exactly one hook to make it from. IsLoadingDatabases is the
        // assertion that discriminates: at 900 ms an auto-connect that DID fire
        // is still in flight with a null message (see the control above), so
        // asserting the message alone passed with the panel fan-out deleted.
        // The scan half is not exercised here — it would broadcast on UDP — and
        // is declared uncovered.
        RecordingCredentialStore store = new();
        ProjectSetupViewModel vm = new(store);
        store.Stored = "sa|p4ss";
        vm.Source.ServerName = Server;
        vm.Target.ServerName = Server;

        vm.CancelPendingWork();
        await Task.Delay(900, TestContext.Current.CancellationToken);

        vm.Source.IsLoadingDatabases.Should().BeFalse();
        vm.Target.IsLoadingDatabases.Should().BeFalse();
        vm.Source.ConnectionStatusMessage.Should().BeNull();
        vm.Target.ConnectionStatusMessage.Should().BeNull();
    }
}
