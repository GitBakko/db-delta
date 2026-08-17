using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Persistence.Sql;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// Roadmap item 2, GUI half. A compare against a server that stopped answering
/// used to be unstoppable: the overlay had no way out.
/// </summary>
/// <remarks>
/// The Annulla button can only cancel a run <c>CompareCommand</c> itself
/// started — the generated cancel command follows the command's own token
/// source. That makes "does this call site go through the command" the thing
/// worth asserting: it is invisible in the UI until the day someone needs it.
/// </remarks>
public class CompareCancellationTests
{
    private const string Staging = "Server=staging;Database=App;Integrated Security=true";

    // Fails the SqlConnectionStringBuilder parse guard, so CompareAsync returns
    // before any I/O — the command still ran, which is all these tests measure.
    private const string Unparseable = "not==a==connection==string";

    private static MainWindowViewModel VmWith(out AppStateViewModel state)
    {
        state = new AppStateViewModel
        {
            SourceConnectionString = Unparseable,
            TargetConnectionString = Staging,
        };
        return new MainWindowViewModel(state);
    }

    [AvaloniaFact]
    public async Task Aggiorna_runs_the_compare_through_the_cancellable_command()
    {
        MainWindowViewModel vm = VmWith(out AppStateViewModel state);

        await vm.RefreshAsync();

        state.CompareCommand.ExecutionTask.Should()
            .NotBeNull("Annulla can only stop a compare the command itself started");
    }

    [AvaloniaFact]
    public async Task The_refresh_after_a_successful_execute_runs_through_the_command_too()
    {
        MainWindowViewModel vm = VmWith(out AppStateViewModel state);

        await vm.AfterExecuteAsync(new SqlBatchResult(true, null, 1, 5), "Eseguito.");

        state.CompareCommand.ExecutionTask.Should().NotBeNull();
    }

    /// <summary>
    /// Cancelling is a decision, not a failure: no red banner, and the overlay
    /// closes.
    /// </summary>
    /// <remarks>
    /// The token is cancelled before the call, so <c>SqlConnection.OpenAsync</c>
    /// hands back a cancelled task without touching the network — deterministic
    /// and offline. What this cannot reach is the other route into the same
    /// catch: a read aborted mid-flight, which the driver reports as
    /// <c>SqlException -2</c> and only a live server can produce.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_cancelled_compare_reports_no_error()
    {
        AppStateViewModel state = new()
        {
            SourceConnectionString = Staging,
            TargetConnectionString = Staging,
        };
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await state.CompareAsync(cts.Token);

        state.LastError.Should().BeNull("the user asked for this, and an error banner would blame the server");
        state.IsBusy.Should().BeFalse("the overlay must close on the way out");
    }

    [AvaloniaFact]
    public void The_cancel_command_stays_idle_while_no_compare_runs()
    {
        AppStateViewModel state = new();

        state.CompareCancelCommand.CanExecute(null).Should().BeFalse();
    }
}
