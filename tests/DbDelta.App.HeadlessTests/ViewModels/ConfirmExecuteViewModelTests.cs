using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Persistence.Sql;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

public class ConfirmExecuteViewModelTests
{
    private static ConfirmExecuteViewModel MakeVm(SqlBatchResult result) =>
        new(
            objectCount: 3,
            differentCount: 1,
            onlyInTargetCount: 1,
            onlyInSourceCount: 1,
            sourceSummary: "Server=src;Database=A",
            targetSummary: "Server=tgt;Database=B",
            executeAsync: () => Task.FromResult(result));

    [AvaloniaFact]
    public void Starts_in_idle_confirmation_phase()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(true, null, 1, 10));

        vm.IsIdle.Should().BeTrue();
        vm.IsRunning.Should().BeFalse();
        vm.IsDone.Should().BeFalse();
        vm.Result.Should().BeNull();
        vm.ExecuteCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Successful_run_transitions_to_done_success_with_outcome_message()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(true, null, 7, 123));

        await vm.ExecuteCommand.ExecuteAsync(null);

        vm.IsDone.Should().BeTrue();
        vm.IsDoneSuccess.Should().BeTrue();
        vm.IsDoneFailure.Should().BeFalse();
        vm.IsRunning.Should().BeFalse();
        vm.Result.Should().NotBeNull();
        vm.ResultMessage.Should().Be("Esecuzione completata — 7 batch in 123 ms.");
        vm.ExecuteCommand.CanExecute(null).Should().BeFalse(); // no re-run from the outcome phase
    }

    [AvaloniaFact]
    public async Task Failed_run_transitions_to_done_failure_with_sql_error()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(false, "Invalid object name 'dbo.X'.", 2, 50));

        await vm.ExecuteCommand.ExecuteAsync(null);

        vm.IsDoneFailure.Should().BeTrue();
        vm.IsDoneSuccess.Should().BeFalse();
        vm.ResultMessage.Should().Be("Esecuzione fallita: Invalid object name 'dbo.X'.");
    }

    [AvaloniaFact]
    public void Exposes_selection_breakdown_and_redacted_endpoints()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(true, null, 1, 1));

        vm.ObjectCount.Should().Be(3);
        vm.DifferentCount.Should().Be(1);
        vm.OnlyInTargetCount.Should().Be(1);
        vm.OnlyInSourceCount.Should().Be(1);
        vm.SourceSummary.Should().Be("Server=src;Database=A");
        vm.TargetSummary.Should().Be("Server=tgt;Database=B");
    }
}
