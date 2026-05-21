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
            Target: new ProjectEndpoint(tgtConn, auth),
            OwnerMappings: [new OwnerMappingEntry("dbo", "dbo")],
            TableMappings: [],
            ProjectOptions: new ProjectOptions(
                IgnoreFillFactor: true,
                IgnoreCollation: true,
                IgnoreWhitespace: true,
                IgnoreCommentBlocks: false,
                TreatExtendedPropertiesAsObjects: false));
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

    // ── Test 4: Build_produces_DbDeltaProject_with_both_endpoints_and_options ─

    [AvaloniaFact]
    public void Build_produces_DbDeltaProject_with_both_endpoints_and_options()
    {
        ProjectSetupViewModel vm = new()
        {
            ProjectName = "My project",
            Options = new ProjectOptions(
                IgnoreFillFactor: true,
                IgnoreCollation: false,
                IgnoreWhitespace: true,
                IgnoreCommentBlocks: false,
                TreatExtendedPropertiesAsObjects: false),
        };

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
        project.ProjectOptions.IgnoreFillFactor.Should().BeTrue();
        project.ProjectOptions.IgnoreWhitespace.Should().BeTrue();
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
        rebuilt.ProjectOptions.IgnoreFillFactor.Should().Be(original.ProjectOptions.IgnoreFillFactor);
        rebuilt.ProjectOptions.IgnoreCollation.Should().Be(original.ProjectOptions.IgnoreCollation);
        rebuilt.OwnerMappings.Should().HaveCount(original.OwnerMappings.Count);
    }
}
