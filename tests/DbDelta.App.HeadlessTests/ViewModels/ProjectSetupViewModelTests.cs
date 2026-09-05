using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

public class ProjectSetupViewModelTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void FillEndpoint(ProjectEndpointPanelViewModel ep, string server, string db)
    {
        ep.ServerName = server;
        ep.DatabaseName = db;
        ep.AuthMode = AuthenticationMode.SqlServer;
        ep.UserName = "sa";
        ep.Password = "p4ssw0rd";
    }

    private static ConnectionEntry Entry(string server, string db, int day) => new(
        Id: Guid.NewGuid(),
        Name: $"{server}.{db}",
        ServerName: server,
        DatabaseName: db,
        ConnectionStringTemplate: "",
        EnvironmentTag: "Dev",
        EnvironmentColorHex: "#0054BD",
        IsPinned: false,
        CreatedUtc: new DateTime(2026, 8, day, 0, 0, 0, DateTimeKind.Utc),
        LastUsedUtc: new DateTime(2026, 8, day, 0, 0, 0, DateTimeKind.Utc));

    private static DbDeltaProject BuildFullProject()
    {
        ProjectConnectionRef srcConn = new(
            Id: Guid.NewGuid(),
            Name: "src",
            ServerName: "srv-src",
            DatabaseName: "db-src",
            EnvironmentTag: "Dev",
            EnvironmentColorHex: "#0054BD");

        ProjectConnectionRef tgtConn = new(
            Id: Guid.NewGuid(),
            Name: "tgt",
            ServerName: "srv-tgt",
            DatabaseName: "db-tgt",
            EnvironmentTag: "Dev",
            EnvironmentColorHex: "#0054BD");

        ProjectAuthentication auth = new(
            Mode: AuthenticationMode.SqlServer,
            UserName: "sa",
            RememberCredentials: true,
            Encrypt: false,
            TrustServerCertificate: true);

        return new DbDeltaProject(
            Name: "Test project",
            CreatedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastModifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Source: new ProjectEndpoint(srcConn, auth),
            Target: new ProjectEndpoint(tgtConn, auth));
    }

    // ── Test 1: IsValid_false_when_either_endpoint_missing_database ───────────

    [AvaloniaFact]
    public void IsValid_false_when_either_endpoint_missing_database()
    {
        ProjectSetupViewModel vm = new();

        // Source missing database
        FillEndpoint(vm.Source, "srv", "");
        FillEndpoint(vm.Target, "srv", "db");
        vm.IsValid.Should().BeFalse("Source has no database");

        // Target missing database
        FillEndpoint(vm.Source, "srv", "db");
        FillEndpoint(vm.Target, "srv", "");
        vm.IsValid.Should().BeFalse("Target has no database");
    }

    // ── Test 2: IsValid_true_when_both_endpoints_have_server+db+credentials ──

    [AvaloniaFact]
    public void IsValid_true_when_both_endpoints_have_server_db_and_credentials()
    {
        ProjectSetupViewModel vm = new();
        FillEndpoint(vm.Source, "srv-a", "dbA");
        FillEndpoint(vm.Target, "srv-b", "dbB");
        vm.IsValid.Should().BeTrue();
    }

    // ── Test 3: SwapEndpoints_exchanges_all_endpoint_fields ──────────────────

    [AvaloniaFact]
    public void SwapEndpoints_exchanges_all_endpoint_fields()
    {
        ProjectSetupViewModel vm = new();

        vm.Source.ServerName = "srv-src";
        vm.Source.DatabaseName = "db-src";
        vm.Source.UserName = "user-src";
        vm.Source.Password = "pw-src";
        vm.Source.AuthMode = AuthenticationMode.SqlServer;
        vm.Source.Encrypt = false;
        vm.Source.TrustServerCertificate = true;
        vm.Source.RememberCredentials = true;

        vm.Target.ServerName = "srv-tgt";
        vm.Target.DatabaseName = "db-tgt";
        vm.Target.UserName = "user-tgt";
        vm.Target.Password = "pw-tgt";
        vm.Target.AuthMode = AuthenticationMode.WindowsIntegrated;
        vm.Target.Encrypt = true;
        vm.Target.TrustServerCertificate = false;
        vm.Target.RememberCredentials = false;

        vm.SwapEndpointsCommand.Execute(null);

        // Source now holds what was Target
        vm.Source.ServerName.Should().Be("srv-tgt");
        vm.Source.DatabaseName.Should().Be("db-tgt");
        vm.Source.UserName.Should().Be("user-tgt");
        vm.Source.Password.Should().Be("pw-tgt");
        vm.Source.AuthMode.Should().Be(AuthenticationMode.WindowsIntegrated);
        vm.Source.Encrypt.Should().BeTrue();
        vm.Source.TrustServerCertificate.Should().BeFalse();
        vm.Source.RememberCredentials.Should().BeFalse();

        // Target now holds what was Source
        vm.Target.ServerName.Should().Be("srv-src");
        vm.Target.DatabaseName.Should().Be("db-src");
        vm.Target.UserName.Should().Be("user-src");
        vm.Target.Password.Should().Be("pw-src");
        vm.Target.AuthMode.Should().Be(AuthenticationMode.SqlServer);
        vm.Target.Encrypt.Should().BeFalse();
        vm.Target.TrustServerCertificate.Should().BeTrue();
        vm.Target.RememberCredentials.Should().BeTrue();
    }

    [AvaloniaFact]
    public void CloneSourceToTarget_lands_the_database_after_the_server_that_clears_it()
    {
        // Naming a server clears the catalog (2026-09-05), so every bulk copy
        // must write DatabaseName after ServerName. Swap, FromProject and
        // LoadFrom were pinned by the tests around this one; «Clona» was the
        // sixth call site and had no test at all.
        ProjectSetupViewModel vm = new();
        FillEndpoint(vm.Source, "srv-src", "db-src");
        FillEndpoint(vm.Target, "srv-old", "db-old");

        vm.CloneSourceToTargetCommand.Execute(null);

        vm.Target.ServerName.Should().Be("srv-src");
        vm.Target.DatabaseName.Should().Be("db-src");
        vm.Target.Password.Should().Be("p4ssw0rd");
        vm.IsValid.Should().BeTrue();
    }

    // ── Test 4: Build_produces_DbDeltaProject_with_both_endpoints ────────────

    [AvaloniaFact]
    public void Build_produces_DbDeltaProject_with_both_endpoints()
    {
        ProjectSetupViewModel vm = new() { ProjectName = "My project" };

        FillEndpoint(vm.Source, "srv-src", "dbSrc");
        FillEndpoint(vm.Target, "srv-tgt", "dbTgt");

        DbDeltaProject project = vm.Build();

        project.Name.Should().Be("My project");
        project.Source.Should().NotBeNull();
        project.Target.Should().NotBeNull();
        project.Source!.Connection.ServerName.Should().Be("srv-src");
        project.Source.Connection.DatabaseName.Should().Be("dbSrc");
        project.Target!.Connection.ServerName.Should().Be("srv-tgt");
        project.Target.Connection.DatabaseName.Should().Be("dbTgt");
    }

    // ── Test 5: FromProject_round_trips_through_Build ─────────────────────────

    [AvaloniaFact]
    public void FromProject_round_trips_through_Build()
    {
        DbDeltaProject original = BuildFullProject();
        var vm = ProjectSetupViewModel.FromProject(original);
        DbDeltaProject rebuilt = vm.Build();

        rebuilt.Name.Should().Be(original.Name);
        rebuilt.Source.Should().NotBeNull();
        rebuilt.Target.Should().NotBeNull();
        rebuilt.Source!.Connection.ServerName.Should().Be(original.Source!.Connection.ServerName);
        rebuilt.Source.Connection.DatabaseName.Should().Be(original.Source.Connection.DatabaseName);
        rebuilt.Target!.Connection.ServerName.Should().Be(original.Target!.Connection.ServerName);
        rebuilt.Target.Connection.DatabaseName.Should().Be(original.Target.Connection.DatabaseName);
        rebuilt.Source.Authentication.Mode.Should().Be(original.Source.Authentication.Mode);
        rebuilt.Source.Authentication.TrustServerCertificate.Should()
               .Be(original.Source.Authentication.TrustServerCertificate);
    }

    // ── Test 6: DisplayBandName_returns_placeholder_when_server_empty ─────────

    [AvaloniaFact]
    public void DisplayBandName_returns_placeholder_when_server_name_empty()
    {
        ProjectEndpointPanelViewModel src = new("Source", isTarget: false);
        ProjectEndpointPanelViewModel tgt = new("Target", isTarget: true);

        src.ServerName = "";
        tgt.ServerName = "";

        src.DisplayBandName.Should().Be("Seleziona una provenienza…");
        tgt.DisplayBandName.Should().Be("Seleziona una destinazione…");
    }

    [AvaloniaFact]
    public void DisplayBandName_returns_server_name_when_set()
    {
        ProjectEndpointPanelViewModel ep = new("Source", isTarget: false) { ServerName = "MY-SERVER" };
        ep.DisplayBandName.Should().Be("MY-SERVER");
    }

    // ── Test 7: ServerCountText and DatabaseCountText reflect collection size ──

    [AvaloniaFact]
    public void ServerCountText_reflects_suggestions_count()
    {
        ProjectEndpointPanelViewModel ep = new("Source", isTarget: false);
        ep.ServerCountText.Should().Be("(0 trovati)");

        ep.ServerSuggestions.Add(new Persistence.Sql.DiscoveredServer("SRV1", null));
        ep.ServerSuggestions.Add(new Persistence.Sql.DiscoveredServer("SRV2", "10.0.0.1"));
        ep.ServerCountText.Should().Be("(2 trovati)");
    }

    [AvaloniaFact]
    public void DatabaseCountText_reflects_available_databases_count()
    {
        ProjectEndpointPanelViewModel ep = new("Target", isTarget: true);
        ep.DatabaseCountText.Should().Be("(0 trovati)");

        ep.AvailableDatabases.Add("AdventureWorks");
        ep.AvailableDatabases.Add("Northwind");
        ep.DatabaseCountText.Should().Be("(2 trovati)");
    }

    // ── Test 8: LoadFrom_populates_vm_from_project ────────────────────────────

    [AvaloniaFact]
    public void LoadFrom_populates_vm_fields_from_project()
    {
        DbDeltaProject original = BuildFullProject();
        ProjectSetupViewModel vm = new();
        vm.LoadFrom(original);

        vm.ProjectName.Should().Be(original.Name);
        vm.Source.ServerName.Should().Be(original.Source!.Connection.ServerName);
        vm.Source.DatabaseName.Should().Be(original.Source.Connection.DatabaseName);
        vm.Target.ServerName.Should().Be(original.Target!.Connection.ServerName);
        vm.Target.DatabaseName.Should().Be(original.Target.Connection.DatabaseName);
    }

    [AvaloniaFact]
    public void LoadFrom_clears_the_state_that_belongs_to_the_old_connection()
    {
        ProjectSetupViewModel vm = new();
        vm.Source.AvailableDatabases.Add("OLD_DB");
        vm.Source.HasDatabases = true;
        vm.Source.ServerVersion = "SQL Server 2016";

        vm.LoadFrom(BuildFullProject());

        vm.Source.AvailableDatabases.Should().BeEmpty();
        vm.Source.HasDatabases.Should().BeFalse();
        vm.Source.ServerVersion.Should().BeNull();
    }

    /// <summary>
    /// Discovered servers come from the network and the connection store, not
    /// from the project — wiping them on load left the picker empty exactly
    /// when the user was about to change endpoint.
    /// </summary>
    [AvaloniaFact]
    public void LoadFrom_keeps_the_discovered_servers()
    {
        ProjectSetupViewModel vm = new();
        vm.Source.ServerSuggestions.Add(new Persistence.Sql.DiscoveredServer("SCANNED", null));
        vm.Source.HasServerSuggestions = true;

        vm.LoadFrom(BuildFullProject());

        vm.Source.ServerSuggestions.Should().ContainSingle(s => s.Name == "SCANNED");
        vm.Source.HasServerSuggestions.Should().BeTrue();
    }

    [AvaloniaFact]
    public void SeedRecentServersFrom_lists_each_server_once_most_recent_first()
    {
        ProjectSetupViewModel vm = new();
        vm.SeedRecentServersFrom(
            [Entry("SRV-A", "one", 1), Entry("SRV-A", "two", 3), Entry("SRV-B", "three", 2)]);

        string[] servers = [.. vm.Source.ServerSuggestions.Where(s => !s.IsHeaderOnly).Select(s => s.Name)];
        servers.Should().Equal("SRV-A", "SRV-B");
        vm.Target.ServerSuggestions.Should().NotBeEmpty("both panels share the picker");
    }
}
