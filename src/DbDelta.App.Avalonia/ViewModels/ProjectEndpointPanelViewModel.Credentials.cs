using DbDelta.Core.Abstractions;

namespace DbDelta.App.ViewModels;

// The credential pair: what the store puts into the boxes and what it takes
// back out, who is allowed to vouch that the pair on screen belongs to the
// server now named, and the one thing a vouched pair may do on its own — the
// debounced auto-connect.
public sealed partial class ProjectEndpointPanelViewModel
{
    private readonly ICredentialStore? _credentialStore;
    private bool _autoFillFromCredentialsInFlight;
    private CancellationTokenSource? _autoConnectCts;
    private const int AutoConnectDebounceMs = 450;

    /// <summary>
    /// The server whose stored pair is the one now in the boxes, or null when
    /// nobody can say that: set by <see cref="TryAutoFillCredentialsAsync"/> right
    /// after it puts the pair there, cleared by any edit to the user, the
    /// password or the server name. It is the whole answer to "may this pair be
    /// sent unasked" under SQL auth — asked when an arm is made and again when
    /// it fires — and it is what lets an <see cref="AuthMode"/> change re-arm:
    /// every bulk assignment makes that change right after the server name, and
    /// it used to cancel the auto-fill's arm without replacing it.
    /// </summary>
    private string? _vouchedServer;

    // ── Auto-connect debounce ────────────────────────────────────────────────

    /// <summary>
    /// Debounced auto-connect. When all required connection fields are filled
    /// (server + auth credentials) AND no databases have been loaded yet, fire
    /// <see cref="LoadDatabasesAsync"/> after a short quiet period so the user
    /// doesn't have to click "Connetti" manually. Each change cancels the
    /// previous pending attempt — a single connection is fired per quiescent
    /// burst of edits.
    /// </summary>
    /// <remarks>
    /// Under SQL auth it arms only when <see cref="MayAutoConnect"/> says the pair
    /// in the boxes is the one the credential store filed under the server now
    /// named — which only <see cref="TryAutoFillCredentialsAsync"/> can establish.
    /// Anywhere else the pair may still belong to the server named a moment ago,
    /// and sending it onward unasked is credential disclosure: the host may have
    /// come from an unauthenticated UDP scan reply, over a string that carries
    /// this panel's TrustServerCertificate. This one guard replaces wiping the two
    /// fields on every server-name keystroke: it denies the same thing without
    /// destroying what the user typed. The same predicate is asked again when the
    /// debounce fires, because the boxes may have been edited in between.
    /// </remarks>
    private void ScheduleAutoConnect()
    {
        // Unconditional, and it must stay above the guard below: a pending
        // attempt re-reads ServerName when it fires, so one left running would
        // aim the previous server's login at the host just named. Disposed as
        // well as cancelled: a linked source holds a registration on _lifetime
        // that only Dispose releases, and this runs once per keystroke.
        _autoConnectCts?.Cancel();
        _autoConnectCts?.Dispose();
        _autoConnectCts = null;

        if (!MayAutoConnect() || !IsAutoConnectEligible()) { return; }

        // Linked, not standalone: the debounce has two reasons to die — a newer
        // edit, and the dialog closing.
        var cts =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _autoConnectCts = cts;
        _ = AutoConnectAfterDelayAsync(cts.Token);
    }

    /// <summary>
    /// Stops everything this panel started. The dialog calls it once, on Closed.
    /// </summary>
    /// <remarks>
    /// Cancelling the debounce alone was never enough: every SQL call here used
    /// to pass <see cref="CancellationToken.None"/>, so up to ~20 s of work ran
    /// against a server on behalf of a window that was already gone — and on the
    /// success path <see cref="TryPersistCredentialsAsync"/> wrote, or DELETED,
    /// a Credential Manager entry after the user had pressed «Annulla».
    /// Idempotent; the panel is not reused after it.
    /// </remarks>
    public void CancelPendingWork() => _lifetime.Cancel();

    private async Task AutoConnectAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoConnectDebounceMs, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Asked again at fire time, not only at arm time: the user/password
        // setters do not cancel a pending arm, so a remembered pair retyped
        // within the 450 ms would otherwise go out as the prefix typed so far —
        // the 2026-09-03 rule is that anything typed waits for «Connetti». It
        // also closes a path only a store that answers late could open: an arm
        // made for a name that has since moved. Found by the 2026-09-05 review.
        if (ct.IsCancellationRequested || !MayAutoConnect() || !IsAutoConnectEligible())
        {
            return;
        }

        // LoadDatabasesAsync already handles the IsLoadingDatabases flag and
        // surfaces errors via ConnectionStatusMessage, so we can fire-and-forget.
        try
        {
            await LoadDatabasesAsync().ConfigureAwait(true);
        }
        catch
        {
            // Swallow — manual Connetti remains available if auto-connect fails.
        }
    }

    private bool IsAutoConnectEligible() =>
        !IsLoadingDatabases
        && !HasDatabases
        && !string.IsNullOrWhiteSpace(ServerName)
        && (AuthMode == AuthenticationMode.WindowsIntegrated
            || (!string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Password)));

    // Windows auth has no secret to send and is exempt. Under SQL auth the pair
    // in the boxes must be the store's pair for the server now named, with
    // nobody having touched either since. Compared against the live ServerName
    // on purpose: the setters clear the field anyway, but a compare that cannot
    // be bypassed is one fewer thing to trust.
    private bool MayAutoConnect() =>
        AuthMode == AuthenticationMode.WindowsIntegrated
        || (_vouchedServer is not null
            && string.Equals(_vouchedServer, ServerName, StringComparison.Ordinal));

    // ── DPAPI credential persistence ─────────────────────────────────────────

    /// <summary>
    /// Builds the per-server credential key used by <see cref="ICredentialStore"/>.
    /// Server name is normalised to lower-case so different casings collapse to
    /// the same entry.
    /// </summary>
    private static string CredentialKey(string serverName)
        => $"dbdelta:server:{serverName.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Attempts to read previously-saved credentials for the given server and
    /// populate <see cref="UserName"/> + <see cref="Password"/>. Triggered on
    /// every <see cref="ServerName"/> change. Idempotent and silent on failure.
    /// </summary>
    private async Task TryAutoFillCredentialsAsync(string serverName)
    {
        if (_credentialStore is null
            || !_credentialStore.IsAvailable
            || string.IsNullOrWhiteSpace(serverName)
            || _autoFillFromCredentialsInFlight)
        {
            return;
        }

        _autoFillFromCredentialsInFlight = true;
        try
        {
            string? blob = await _credentialStore
                .GetSecretAsync(CredentialKey(serverName), _lifetime.Token)
                .ConfigureAwait(true);

            // The shipped store answers synchronously, so this never trips
            // today; a store that yields — the v2 Keychain and Secret Service
            // ones, or a Task.Run wrapper — could answer for a name the user
            // has already moved away from, and filling the boxes then would put
            // one server's pair under another's name. Not ours to place.
            if (!string.Equals(serverName, ServerName, StringComparison.Ordinal)) { return; }

            if (string.IsNullOrEmpty(blob)) { return; }

            // Format: "user|password" — '|' is forbidden in SQL Server logins so
            // it's a safe separator. Older single-field secrets land in Password.
            int sep = blob.IndexOf('|');
            if (sep < 0)
            {
                Password = blob;
                return;
            }

            string user = blob[..sep];
            string pwd = blob[(sep + 1)..];
            if (!string.IsNullOrWhiteSpace(user)) { UserName = user; }
            Password = pwd;
            RememberCredentials = true;

            // The one place a credential pair is known-complete AND known to
            // belong to the server now named: we put it there, from the store's
            // entry for this very server. The field setters no longer arm the
            // auto-connect — see the comment on OnUserNameChanged — so it is
            // armed here instead, which keeps "pick a remembered server and it
            // connects itself" working without ever sending a half-typed secret,
            // or one that belongs to a different host. Vouched AFTER the two
            // setters above, which clear the vouching as any edit does; the
            // vouching is what lets ScheduleAutoConnect arm, here and again on
            // an AuthMode change.
            _vouchedServer = serverName;
            ScheduleAutoConnect();
        }
        catch
        {
            // Credential store failures are non-fatal — fall back to manual entry.
        }
        finally
        {
            _autoFillFromCredentialsInFlight = false;
        }
    }

    /// <summary>
    /// Persists the current <see cref="UserName"/>/<see cref="Password"/> to
    /// <see cref="ICredentialStore"/> under the server-specific key when
    /// <see cref="RememberCredentials"/> is enabled; otherwise removes any
    /// existing entry so unchecking the flag actively forgets the secret.
    /// </summary>
    internal async Task TryPersistCredentialsAsync()
    {
        if (_credentialStore is null
            || !_credentialStore.IsAvailable
            || string.IsNullOrWhiteSpace(ServerName)
            || AuthMode != AuthenticationMode.SqlServer)
        {
            return;
        }

        // Both branches write to the Credential Manager — the else-branch DELETES
        // — so a closed dialog must not reach either.
        _lifetime.Token.ThrowIfCancellationRequested();

        string key = CredentialKey(ServerName);
        try
        {
            if (RememberCredentials && !string.IsNullOrEmpty(Password))
            {
                await _credentialStore
                    .SetSecretAsync(key, $"{UserName}|{Password}", _lifetime.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                await _credentialStore
                    .DeleteSecretAsync(key, _lifetime.Token)
                    .ConfigureAwait(true);
            }
        }
        catch
        {
            // Non-fatal — secret persistence is best-effort.
        }
    }
}
