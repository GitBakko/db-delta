using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Persistence.Sql;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// What the operator is left with once the execute dialog is gone. Until this
/// existed the outcome panel died with the dialog and took every error the
/// server had raised with it.
/// </summary>
public class LastRunViewModelTests
{
    private static readonly DateTime At = new(2026, 8, 1, 10, 42, 17, DateTimeKind.Local);

    private static SqlServerMessage Print(string text) => new(0, 10, 1, 1, null, text);

    private static SqlServerMessage Error(int number, string text) => new(number, 16, 1, 3, null, text);

    [AvaloniaFact]
    public void A_successful_run_reads_as_succeeded_with_its_time()
    {
        LastRunViewModel run = new(
            new SqlBatchResult(true, null, 12, 480, Messages: [Print("Creating [dbo].[A]")]), At);

        run.Succeeded.Should().BeTrue();
        run.PillLabel.Should().Be("Ultima esecuzione 10:42: riuscita");
        run.Headline.Should().Be("Esecuzione completata — 12 batch in 480 ms");
        run.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// The count on the pill is what makes a failed run worth clicking. It
    /// counts what the SERVER called an error — severity above 10 — not what
    /// arrived on the error channel: a RAISERROR at severity 10 is delivered as
    /// an info message and is not a failure.
    /// </summary>
    [AvaloniaFact]
    public void A_failed_run_carries_the_error_count_onto_the_pill()
    {
        LastRunViewModel run = new(
            new SqlBatchResult(
                false, "Msg 207", 4, 260, RolledBack: true,
                Messages: [Print("Altering [dbo].[T]"), Error(207, "Invalid column name 'X'."), Error(4901, "ALTER TABLE only allows…")]),
            At);

        run.Succeeded.Should().BeFalse();
        run.ErrorCount.Should().Be(2, "the PRINT is not an error");
        run.PillLabel.Should().Be("Ultima esecuzione 10:42: fallita · 2 errori");
        run.Subline.Should().Contain("rollback eseguito");
    }

    /// <summary>
    /// "Rolled back" and "we could not find out" are different facts and the
    /// operator has to act differently on them — the flag is false for both.
    /// </summary>
    [AvaloniaFact]
    public void An_unconfirmed_rollback_says_so_rather_than_claiming_the_target_is_clean()
    {
        LastRunViewModel run = new(
            new SqlBatchResult(false, "timeout", 2, 60_000, RolledBack: false), At);

        run.Subline.Should().Contain("non è stato possibile confermare");
    }

    [AvaloniaFact]
    public void A_single_error_is_not_pluralised()
    {
        LastRunViewModel run = new(
            new SqlBatchResult(false, "x", 1, 10, Messages: [Error(207, "boom")]), At);

        run.PillLabel.Should().EndWith("1 errore");
    }

    /// <summary>
    /// A run that never reached the server has nothing to list, and an empty
    /// panel would read as "it went fine". The connection failure takes its
    /// place.
    /// </summary>
    [AvaloniaFact]
    public void A_run_with_no_server_messages_shows_the_client_side_failure_instead()
    {
        LastRunViewModel run = new(
            new SqlBatchResult(false, "Login failed for user 'sa'.", 0, 30), At);

        run.HasMessages.Should().BeFalse();
        run.EmptyText.Should().Be("Login failed for user 'sa'.");
    }

    /// <summary>
    /// The transcript is how the run reaches a ticket or a DBA, so it has to
    /// carry the SSMS header line — a message without its Msg number is not
    /// searchable and not quotable.
    /// </summary>
    [AvaloniaFact]
    public void The_transcript_heads_each_error_the_way_ssms_does()
    {
        LastRunViewModel run = new(
            new SqlBatchResult(
                false, "boom", 1, 10,
                Messages: [Print("Altering [dbo].[T]"), Error(4901, "ALTER TABLE only allows…")]),
            At);

        string text = LastRunDialog.Transcribe(run);

        text.Should().Contain("Altering [dbo].[T]")
            .And.Contain("Msg 4901, Livello 16, Stato 1, Riga 3")
            .And.Contain("ALTER TABLE only allows…");
        text.Should().NotContain("Msg 0", "a PRINT has no number to quote");
    }
}
