using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Data context for <see cref="Views.ConfirmExecuteDialog"/> — drives the full
/// direct-execution flow: the confirmation summary (what runs, from where, to
/// where), the in-dialog execution with busy state, and the success / failure
/// outcome panel. The actual SQL execution is injected as a delegate so the
/// view-model stays testable without a live connection.
/// </summary>
public sealed partial class ConfirmExecuteViewModel : ObservableObject
{
    private readonly Func<Task<SqlBatchResult>> _executeAsync;

    public ConfirmExecuteViewModel(
        int objectCount,
        int differentCount,
        int onlyInTargetCount,
        int onlyInSourceCount,
        string sourceSummary,
        string targetSummary,
        Func<Task<SqlBatchResult>> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        ObjectCount = objectCount;
        DifferentCount = differentCount;
        OnlyInTargetCount = onlyInTargetCount;
        OnlyInSourceCount = onlyInSourceCount;
        SourceSummary = sourceSummary;
        TargetSummary = targetSummary;
        _executeAsync = executeAsync;
    }

    /// <summary>Total number of selected objects that will be aligned.</summary>
    public int ObjectCount { get; }

    /// <summary>Selected rows whose objects differ on the two sides.</summary>
    public int DifferentCount { get; }

    /// <summary>Selected rows that exist only on the target (will be dropped).</summary>
    public int OnlyInTargetCount { get; }

    /// <summary>Selected rows that exist only on the source (will be created).</summary>
    public int OnlyInSourceCount { get; }

    /// <summary>Redacted source connection summary.</summary>
    public string SourceSummary { get; }

    /// <summary>Redacted target connection summary.</summary>
    public string TargetSummary { get; }

    /// <summary>
    /// Outcome of the execution — <c>null</c> until the script ran (or when the
    /// user cancelled). The owning window reads this after the dialog closes.
    /// </summary>
    public SqlBatchResult? Result { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsDoneSuccess))]
    [NotifyPropertyChangedFor(nameof(IsDoneFailure))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoneSuccess))]
    [NotifyPropertyChangedFor(nameof(IsDoneFailure))]
    private bool _succeeded;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    /// <summary>Confirmation phase: execution not started yet.</summary>
    public bool IsIdle => !IsRunning && !IsDone;

    /// <summary>Outcome phase, success — drives the emerald outcome panel.</summary>
    public bool IsDoneSuccess => IsDone && Succeeded;

    /// <summary>Outcome phase, failure — drives the crimson outcome panel.</summary>
    public bool IsDoneFailure => IsDone && !Succeeded;

    private bool CanExecute() => IsIdle;

    /// <summary>
    /// Runs the injected execution delegate and transitions the dialog to the
    /// outcome phase. The generated script self-manages its transaction
    /// (XACT_ABORT + rollback on failure), so a failed run leaves the target
    /// untouched and the error message is the real SQL Server error.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        IsRunning = true;
        try
        {
            SqlBatchResult res = await _executeAsync().ConfigureAwait(true);
            Result = res;
            Succeeded = res.Success;
            ResultMessage = res.Success
                ? $"Esecuzione completata — {res.BatchesExecuted} batch in {res.TotalDurationMs} ms."
                : $"Esecuzione fallita: {res.ErrorMessage}";
        }
        finally
        {
            IsRunning = false;
            IsDone = true;
        }
    }
}
