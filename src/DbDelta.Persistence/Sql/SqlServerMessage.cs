using Microsoft.Data.SqlClient;
using DbDelta.Persistence.Util;

namespace DbDelta.Persistence.Sql;

/// <summary>
/// One message SQL Server sent while a script ran — an error, or a PRINT.
/// </summary>
/// <param name="Number">The <c>Msg</c> number. 0 for a bare PRINT.</param>
/// <param name="Severity">Its class. Above 10 the server raised it as an error.</param>
/// <param name="State">The state SQL Server reports alongside the number.</param>
/// <param name="LineNumber">Line WITHIN the batch, which is what the server counts.</param>
/// <param name="Procedure">The routine that raised it, when it came from one.</param>
/// <param name="Text">The message itself.</param>
/// <remarks>
/// Both channels are worth keeping and only one of them throws. Errors arrive
/// on a <see cref="SqlException"/>, which carries ALL of them in
/// <c>Errors</c> — and the app used to keep only <c>ex.Message</c>. Everything
/// at severity 10 or below never throws at all: it arrives on
/// <c>SqlConnection.InfoMessage</c>, and that is where every <c>PRINT</c> in a
/// deploy script lives, i.e. the running commentary of which object was being
/// created when the thing died. Dropping it is why the app could show two lines
/// where SSMS shows a hundred.
/// </remarks>
public sealed record SqlServerMessage(
    int Number,
    byte Severity,
    byte State,
    int LineNumber,
    string? Procedure,
    string Text)
{
    /// <summary>
    /// True when the server raised this as an error rather than as information.
    /// </summary>
    /// <remarks>
    /// Severity is the discriminant, not the channel it arrived on: a
    /// <c>RAISERROR</c> at severity 10 or below is delivered as an info message,
    /// and an error can be reported through both.
    /// </remarks>
    public bool IsError => Severity > 10;

    /// <summary>
    /// Renders the message the way SSMS heads one, so an operator can match it
    /// against what they would have seen there.
    /// </summary>
    public string Header => Number == 0
        ? string.Empty
        : $"Msg {Number}, Livello {Severity}, Stato {State}"
          + (string.IsNullOrEmpty(Procedure) ? string.Empty : $", Routine {Procedure}")
          + $", Riga {LineNumber}";

    /// <summary>
    /// Converts a <see cref="SqlError"/> — from either channel — redacting the
    /// text, since a connection-level failure can name the endpoint.
    /// </summary>
    public static SqlServerMessage From(SqlError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SqlServerMessage(
            error.Number,
            error.Class,
            error.State,
            error.LineNumber,
            string.IsNullOrEmpty(error.Procedure) ? null : error.Procedure,
            ConnectionStringRedactor.Redact(error.Message));
    }
}
