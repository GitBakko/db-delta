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
        IReadOnlyList<string> droppedObjects,
        int onlyInSourceCount,
        string sourceSummary,
        string targetSummary,
        string script,
        Func<Task<SqlBatchResult>> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(droppedObjects);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ObjectCount = objectCount;
        DifferentCount = differentCount;
        DroppedObjects = droppedObjects;
        OnlyInSourceCount = onlyInSourceCount;
        SourceSummary = sourceSummary;
        TargetSummary = targetSummary;
        Script = script ?? string.Empty;
        _executeAsync = executeAsync;
    }

    /// <summary>Total number of selected objects that will be aligned.</summary>
    public int ObjectCount { get; }

    /// <summary>Selected rows whose objects differ on the two sides.</summary>
    public int DifferentCount { get; }

    /// <summary>
    /// Qualified names of the selected objects that exist only on the target —
    /// the ones this run DROPS.
    /// </summary>
    /// <remarks>
    /// The names, not just a count. With undo deferred this dialog is the last
    /// gate before an irreversible change, and "3 solo destinazione" is a tally
    /// that reads as neutral inventory; the objects behind it are being deleted.
    /// </remarks>
    public IReadOnlyList<string> DroppedObjects { get; }

    /// <summary>Selected rows that exist only on the target (will be dropped).</summary>
    public int OnlyInTargetCount => DroppedObjects.Count;

    /// <summary>True when this run deletes something.</summary>
    public bool HasDrops => DroppedObjects.Count > 0;

    /// <summary>The deletion warning, in the danger band above the names.</summary>
    public string DropWarning => OnlyInTargetCount == 1
        ? "1 oggetto verrà ELIMINATO dalla destinazione. L'operazione non è annullabile."
        : $"{OnlyInTargetCount} oggetti verranno ELIMINATI dalla destinazione. L'operazione non è annullabile.";

    /// <summary>The dropped names, one per line, for the monospace list.</summary>
    public string DroppedObjectsText => string.Join(Environment.NewLine, DroppedObjects);

    /// <summary>Selected rows that exist only on the source (will be created).</summary>
    public int OnlyInSourceCount { get; }

    /// <summary>
    /// The exact SQL this dialog will run. Already built by the caller before the
    /// dialog opens — reading it used to cost a cancel, a second trip through
    /// "Genera script", a second answer to the backfill preflight, a file save and
    /// an external editor.
    /// </summary>
    public string Script { get; }

    /// <summary>Line count of <see cref="Script"/>, for the disclosure header.</summary>
    public int ScriptLineCount =>
        Script.Length == 0 ? 0 : Script.AsSpan().Count('\n') + (Script.EndsWith('\n') ? 0 : 1);

    /// <summary>Header of the collapsed script pane.</summary>
    public string ScriptToggleLabel => $"Mostra lo script ({ScriptLineCount} righe)";

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
    /// outcome phase. The generated script self-manages its transaction, so a
    /// failed run leaves the target untouched and the error message is the real
    /// SQL Server error. What keeps it untouched is that the script's
    /// <c>COMMIT</c> is never reached — the executor stops at the batch that
    /// throws, so the script's own gate and closing rollback never run, and
    /// <c>XACT_ABORT</c> is not the lever either (measured ON and OFF, identical).
    /// See the <c>&lt;remarks&gt;</c> on <c>SqlExecutor.TryRollbackAsync</c>.
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
