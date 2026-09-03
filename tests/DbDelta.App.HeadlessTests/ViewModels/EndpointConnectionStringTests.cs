using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// Every connection string the modals hand onward must survive a password that
/// contains the characters the format itself uses as delimiters.
/// </summary>
/// <remarks>
/// All four builders interpolated their fields into a <c>';'</c>-delimited,
/// <c>'='</c>-keyed format with no quoting, so <c>Password=a;b</c> produced a
/// string whose parse either loses the tail or throws "Keyword not supported" —
/// and the message the user reads names the initialization string, never the
/// password. The damage is not only the failed connection: <c>IsValid</c> never
/// required one, so OK stayed enabled and the broken string reached
/// <c>AppState.SourceConnectionString</c>, where every later comparison
/// inherited it. <see cref="SqlConnectionStringBuilder"/> owns the quoting
/// rules; assigning to its properties is the whole fix.
/// <para>
/// Server names here are <c>.invalid</c> (RFC 2606) on purpose: naming a server
/// arms the panel's 450 ms auto-connect under Windows auth, and a name that
/// cannot resolve keeps that stray attempt off the network.
/// </para>
/// </remarks>
public class EndpointConnectionStringTests
{
    // The password that breaks the format: it carries both delimiters.
    private const string AwkwardPassword = "a;b=c";

    private const string Server = "dbdelta-nonesistente.invalid\\INST";

    // Order matters and the initialiser preserves it: naming the server clears
    // the credentials, so they land after it — exactly as a user types them.
    private static ProjectEndpointPanelViewModel Panel(string password = AwkwardPassword) =>
        new("Sorgente", isTarget: false)
        {
            ServerName = Server,
            DatabaseName = "db",
            AuthMode = AuthenticationMode.SqlServer,
            UserName = "sa",
            Password = password,
        };

    [Fact]
    public void A_password_carrying_both_delimiters_survives_the_panels_own_string()
    {
        string cs = Panel().BuildConnectionString(includeDatabase: true);

        SqlConnectionStringBuilder parsed = new(cs);
        parsed.Password.Should().Be(AwkwardPassword);
        parsed.DataSource.Should().Be(Server);
        parsed.InitialCatalog.Should().Be("db");
        parsed.UserID.Should().Be("sa");
    }

    [Fact]
    public void A_password_carrying_both_delimiters_survives_the_string_that_reaches_AppState()
    {
        // The one that actually persists: ProjectSetupDialog captures these two
        // at OK time and App.axaml.cs writes them into AppState.
        ProjectSetupViewModel vm = new();
        vm.Source.ServerName = Server;
        vm.Source.DatabaseName = "db";
        vm.Source.UserName = "sa";
        vm.Source.Password = AwkwardPassword;

        new SqlConnectionStringBuilder(vm.BuildSourceConnectionString())
            .Password.Should().Be(AwkwardPassword);
    }

    [Fact]
    public void A_password_carrying_both_delimiters_survives_the_connection_managers_string()
    {
        // The fourth copy, and it was reachable again the moment the connection
        // manager got its button back (P3, 2026-08-20): an earlier review had
        // waved this one through as unreachable code.
        ConnectionEntry entry = new(
            Guid.NewGuid(), "n", Server, "db",
            "Server=srv;Database=db;User Id=sa;Password={PASSWORD};TrustServerCertificate=True",
            "Dev", "#0054BD", false, DateTime.UtcNow, DateTime.UtcNow);
        ConnectionEditViewModel vm = new(entry, AwkwardPassword);

        new SqlConnectionStringBuilder(vm.BuildConnectionString(includeDatabase: true))
            .Password.Should().Be(AwkwardPassword);
    }

    [Fact]
    public void Control_an_ordinary_password_still_carries_every_field()
    {
        // Without this, a builder that quietly dropped a field would pass the
        // three above: they only ever ask about the password.
        SqlConnectionStringBuilder parsed =
            new(Panel(password: "Hello").BuildConnectionString(includeDatabase: true));

        parsed.Password.Should().Be("Hello");
        parsed.DataSource.Should().Be(Server);
        parsed.InitialCatalog.Should().Be("db");
        parsed.UserID.Should().Be("sa");
        parsed.TrustServerCertificate.Should().BeTrue();
        parsed.IntegratedSecurity.Should().BeFalse();
    }

    [Fact]
    public void Control_windows_authentication_carries_no_credentials_at_all()
    {
        // The other branch. A builder that always wrote User Id / Password would
        // send an empty SQL login where Windows auth was asked for.
        ProjectEndpointPanelViewModel vm = Panel();
        vm.AuthMode = AuthenticationMode.WindowsIntegrated;

        SqlConnectionStringBuilder parsed = new(vm.BuildConnectionString(includeDatabase: true));
        parsed.IntegratedSecurity.Should().BeTrue();
        parsed.Password.Should().BeEmpty();
        parsed.UserID.Should().BeEmpty();
    }

    [Fact]
    public void Control_the_database_is_left_out_when_it_was_not_asked_for()
    {
        // The panel connects without a catalog first — that is how it lists the
        // databases the user then picks from.
        new SqlConnectionStringBuilder(Panel().BuildConnectionString(includeDatabase: false))
            .InitialCatalog.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "False")]
    [InlineData(true, "True")]
    public void The_panels_encrypt_flag_reaches_the_string_with_the_value_it_has(
        bool encrypt, string expected)
    {
        // Found by a surviving mutation probe: inverting this flag left all 220
        // headless tests green. It is not cosmetic — the panel defaults it to
        // false, and SqlClient 6's own default is Mandatory, so a dropped or
        // flipped value turns every connection to a server without TLS into a
        // failure nobody asked for.
        ProjectEndpointPanelViewModel vm = Panel();
        vm.Encrypt = encrypt;

        new SqlConnectionStringBuilder(vm.BuildConnectionString(includeDatabase: true))
            .Encrypt.ToString().Should().Be(expected);
    }

    [Fact]
    public void Control_the_connection_manager_writes_no_encrypt_keyword_at_all()
    {
        // The other half of the same flag, and the reason Build takes it as a
        // nullable: the connection manager has never written Encrypt, so
        // SqlClient's default applies there. Writing one would be a behaviour
        // change hiding inside a de-duplication.
        ConnectionEntry entry = new(
            Guid.NewGuid(), "n", Server, "db",
            "Server=srv;Database=db;User Id=sa;Password={PASSWORD};TrustServerCertificate=True",
            "Dev", "#0054BD", false, DateTime.UtcNow, DateTime.UtcNow);

        new ConnectionEditViewModel(entry, "Hello")
            .BuildConnectionString(includeDatabase: true)
            .Should().NotContain("Encrypt");
    }
}
