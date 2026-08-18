using DbDelta.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// An exception escaping an <c>async void</c> handler or an
/// <c>AsyncRelayCommand</c> must end up in the error banner, not in a closed
/// window.
/// </summary>
/// <remarks>
/// Only the message half is exercised here. The subscription lives in
/// <c>App.OnFrameworkInitializationCompleted</c>, which the headless harness
/// never runs — it builds its own <c>TestApp</c> — so "the handler is actually
/// wired" is checked by inspection and belongs to the live smoke that the
/// backlog still lists as owed.
/// </remarks>
public class UnhandledErrorBannerTests
{
    [Fact]
    public void An_escaped_exception_lands_in_the_error_banner()
    {
        AppStateViewModel appState = new();

        App.ReportUnhandled(appState, new IOException("Il file è in uso da un altro processo."));

        appState.LastError.Should().NotBeNullOrWhiteSpace();
        appState.LastError.Should().Contain("Il file è in uso da un altro processo.",
            "the SQL or OS reason is the only part that tells the user what to change");
        appState.LastError.Should().Contain("non è stato salvato",
            "silence used to be indistinguishable from success on this path");
    }
}
