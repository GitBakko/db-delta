using System.Globalization;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.ViewModels;

/// <summary>
/// The outcome of the most recent direct execution, kept after its dialog is
/// gone: the status-bar pill reads the summary, and
/// <see cref="Views.LastRunDialog"/> reads the messages.
/// </summary>
/// <remarks>
/// Immutable and replaced wholesale on each run, so the pill cannot show the
/// verdict of one run beside the messages of another.
/// </remarks>
public sealed class LastRunViewModel
{
    /// <summary>Captures a completed run and the local time it finished.</summary>
    public LastRunViewModel(SqlBatchResult result, DateTime finishedAtLocal)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
        FinishedAtLocal = finishedAtLocal;
    }

    /// <summary>The raw outcome, messages included.</summary>
    public SqlBatchResult Result { get; }

    /// <summary>When the run finished, on the operator's clock.</summary>
    public DateTime FinishedAtLocal { get; }

    /// <summary>True when every batch ran without error.</summary>
    public bool Succeeded => Result.Success;

    /// <summary>Everything the server said, in the order it said it.</summary>
    public IReadOnlyList<SqlServerMessage> Messages => Result.Messages;

    /// <summary>How many of those the server raised as errors.</summary>
    public int ErrorCount => Result.Errors.Count;

    /// <summary>
    /// The status-bar pill: verdict, time, and — when there are any — how many
    /// errors are waiting behind it.
    /// </summary>
    public string PillLabel
    {
        get
        {
            string when = FinishedAtLocal.ToString("HH:mm", CultureInfo.CurrentCulture);
            string verdict = Succeeded ? "riuscita" : "fallita";
            string errors = ErrorCount > 0
                ? $" · {ErrorCount} error{(ErrorCount == 1 ? "e" : "i")}"
                : string.Empty;
            return $"Ultima esecuzione {when}: {verdict}{errors}";
        }
    }

    /// <summary>Headline of the dialog.</summary>
    public string Headline => Succeeded
        ? $"Esecuzione completata — {Result.BatchesExecuted} batch in {Result.TotalDurationMs} ms"
        : $"Esecuzione fallita dopo {Result.BatchesExecuted} batch";

    /// <summary>
    /// The line under it: when it ran, and whether the target is known to be
    /// untouched. "Non è stato possibile confermare" is not pedantry — the flag
    /// is false both when nothing was rolled back and when the client could not
    /// find out, and an operator has to be able to tell those apart.
    /// </summary>
    public string Subline
    {
        get
        {
            string when = FinishedAtLocal.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
            return Succeeded
                ? when
                : Result.RolledBack
                    ? $"{when} · rollback eseguito, la destinazione è invariata"
                    : $"{when} · non è stato possibile confermare il rollback";
        }
    }

    /// <summary>True when the server said anything at all worth opening.</summary>
    public bool HasMessages => Messages.Count > 0;

    /// <summary>
    /// Shown in place of the list when the server said nothing — which happens
    /// when the run never reached it, e.g. a connection that would not open.
    /// </summary>
    public string EmptyText => Result.ErrorMessage ?? "Il server non ha inviato messaggi.";
}
