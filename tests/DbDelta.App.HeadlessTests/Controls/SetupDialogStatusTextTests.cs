using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// The status lines of the setup dialog must show the whole message.
/// </summary>
/// <remarks>
/// From the 2026-09-03 sweep of the modal: the typical SqlClient failure —
/// «A network-related or instance-specific error occurred… (provider: TCP
/// Provider, error: 0 - No such host is known.)», ~280 characters — sat on ONE
/// 11-px line inside a panel half the window wide. The four status TextBlocks
/// set neither <c>TextWrapping</c> nor <c>TextTrimming</c>, and the two
/// ScrollViewers around them leave horizontal scrolling disabled, so the text
/// was cut mid-word with nothing to say so: the user read the generic preamble
/// and never the final clause, the only one that names the cause.
/// </remarks>
public class SetupDialogStatusTextTests
{
    private const string TypicalSqlClientFailure =
        "Errore: A network-related or instance-specific error occurred while establishing a "
        + "connection to SQL Server. The server was not found or was not accessible. Verify that "
        + "the instance name is correct and that SQL Server is configured to allow remote "
        + "connections. (provider: TCP Provider, error: 0 - No such host is known.)";

    [AvaloniaFact]
    public void A_long_status_message_is_laid_out_on_more_than_one_line()
    {
        // Opened starts the shared scan, which writes «Scansione in corso…»
        // over both scan lines and its verdict a few seconds later; a scan
        // already "running" cannot start, so the four messages stay ours.
        ProjectSetupViewModel setup = new() { IsScanningServers = true };
        setup.Source.ScanStatusMessage = TypicalSqlClientFailure;
        setup.Source.ConnectionStatusMessage = TypicalSqlClientFailure;
        setup.Target.ScanStatusMessage = TypicalSqlClientFailure;
        setup.Target.ConnectionStatusMessage = TypicalSqlClientFailure;

        ProjectSetupDialog dialog = new() { DataContext = setup };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        List<TextBlock> status = [.. dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == TypicalSqlClientFailure)];

        status.Should().HaveCount(4, "scan and connection status, on both panels");
        foreach (TextBlock t in status)
        {
            t.TextLayout.TextLines.Count.Should().BeGreaterThan(1,
                "a message wider than the panel has to wrap, or its last clause — the one naming the cause — is never seen");
        }
    }
}
