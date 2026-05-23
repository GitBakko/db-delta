using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

/// <summary>
/// M6 — User + Role + Permission coverage: diff, emit, identity. Same
/// no-Verify assertion style used by M5KindsTests.
/// </summary>
public class M6KindsTests
{
    // ── Database User ──────────────────────────────────────────────────────

    [Fact]
    public void User_create_sql_login_emits_for_login_clause()
    {
        DatabaseUser u = new("appuser", "S", "appuser_login", "app");
        string sql = new UserScriptEmitter().EmitCreate(u);
        sql.Should().Be("CREATE USER [appuser] FOR LOGIN [appuser_login] WITH DEFAULT_SCHEMA = [app];");
    }

    [Fact]
    public void User_create_without_login_emits_without_login_clause()
    {
        DatabaseUser u = new("orphan", "S", null, "dbo");
        new UserScriptEmitter().EmitCreate(u)
            .Should().Be("CREATE USER [orphan] WITHOUT LOGIN;");
    }

    [Fact]
    public void User_create_azure_ad_emits_from_external_provider()
    {
        DatabaseUser u = new("alice@contoso.com", "E", null, "dbo");
        new UserScriptEmitter().EmitCreate(u)
            .Should().Be("CREATE USER [alice@contoso.com] FROM EXTERNAL PROVIDER;");
    }

    [Fact]
    public void User_diff_default_schema_changed_flags_different()
    {
        DatabaseUser a = new("u", "S", "login", "app");
        DatabaseUser b = a with { DefaultSchema = "report" };
        Database dA = new("d", [], [], [], [], [], []) { Users = [a] };
        Database dB = new("d", [], [], [], [], [], []) { Users = [b] };
        ComparisonResult r = new ComparisonEngine().Compare(dA, dB, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(p =>
            p.Identity.Kind == "User" && p.Status == DifferenceStatus.Different);
    }

    // ── Database Role ──────────────────────────────────────────────────────

    [Fact]
    public void Role_create_with_members_emits_add_member_per_member()
    {
        DatabaseRole role = new("app_reader", "dbo", ["alice", "bob"]);
        string sql = new RoleScriptEmitter().EmitCreate(role);
        sql.Should().Contain("CREATE ROLE [app_reader];")
           .And.Contain("ALTER ROLE [app_reader] ADD MEMBER [alice];")
           .And.Contain("ALTER ROLE [app_reader] ADD MEMBER [bob];");
    }

    [Fact]
    public void Role_create_with_custom_owner_emits_authorization()
    {
        DatabaseRole role = new("app_reader", "appuser", []);
        new RoleScriptEmitter().EmitCreate(role)
            .Should().StartWith("CREATE ROLE [app_reader] AUTHORIZATION [appuser]");
    }

    [Fact]
    public void Role_diff_membership_added_flags_different()
    {
        DatabaseRole a = new("r", "dbo", ["alice"]);
        DatabaseRole b = new("r", "dbo", ["alice", "bob"]);
        Database dA = new("d", [], [], [], [], [], []) { Roles = [a] };
        Database dB = new("d", [], [], [], [], [], []) { Roles = [b] };
        ComparisonResult r = new ComparisonEngine().Compare(dA, dB, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(p =>
            p.Identity.Kind == "Role" && p.Status == DifferenceStatus.Different);
    }

    [Fact]
    public void Role_diff_members_order_does_not_matter()
    {
        DatabaseRole a = new("r", "dbo", ["alice", "bob", "charlie"]);
        DatabaseRole b = new("r", "dbo", ["charlie", "alice", "bob"]);
        Database dA = new("d", [], [], [], [], [], []) { Roles = [a] };
        Database dB = new("d", [], [], [], [], [], []) { Roles = [b] };
        ComparisonResult r = new ComparisonEngine().Compare(dA, dB, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(p => p.Identity.Kind == "Role")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    // ── Permissions ────────────────────────────────────────────────────────

    [Fact]
    public void Permission_grant_select_on_table_emits_grant()
    {
        Permission p = new(
            GranteeName: "app_reader",
            Action: "SELECT",
            State: PermissionState.Grant,
            ClassDesc: "OBJECT_OR_COLUMN",
            ObjectSchema: "dbo",
            ObjectName: "Customer",
            ColumnName: null);
        new PermissionScriptEmitter().EmitGrantOrDeny(p)
            .Should().Be("GRANT SELECT ON [dbo].[Customer] TO [app_reader];");
    }

    [Fact]
    public void Permission_grant_with_grant_option_emits_clause()
    {
        Permission p = new("app_admin", "UPDATE", PermissionState.GrantWithGrantOption,
            "OBJECT_OR_COLUMN", "dbo", "Customer", null);
        new PermissionScriptEmitter().EmitGrantOrDeny(p)
            .Should().Be("GRANT UPDATE ON [dbo].[Customer] TO [app_admin] WITH GRANT OPTION;");
    }

    [Fact]
    public void Permission_deny_column_level_emits_paren_column()
    {
        Permission p = new("app_reader", "SELECT", PermissionState.Deny,
            "OBJECT_OR_COLUMN", "dbo", "Customer", "Ssn");
        new PermissionScriptEmitter().EmitGrantOrDeny(p)
            .Should().Be("DENY SELECT ([Ssn]) ON [dbo].[Customer] TO [app_reader];");
    }

    [Fact]
    public void Permission_revoke_emits_from_grantee()
    {
        Permission p = new("app_reader", "SELECT", PermissionState.Grant,
            "OBJECT_OR_COLUMN", "dbo", "Customer", null);
        new PermissionScriptEmitter().EmitRevoke(p)
            .Should().Be("REVOKE SELECT ON [dbo].[Customer] FROM [app_reader];");
    }

    [Fact]
    public void Permission_diff_only_in_source_classifies_as_only_in_a()
    {
        Permission p = new("app", "SELECT", PermissionState.Grant,
            "OBJECT_OR_COLUMN", "dbo", "Customer", null);
        Database dA = new("d", [], [], [], [], [], []) { Permissions = [p] };
        Database dB = new("d", [], [], [], [], [], []);
        ComparisonResult r = new ComparisonEngine().Compare(dA, dB, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(x =>
            x.Identity.Kind == "Permission" && x.Status == DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Permission_schema_grant_targets_schema_double_colon()
    {
        Permission p = new("app", "EXECUTE", PermissionState.Grant,
            "SCHEMA", "app", null, null);
        new PermissionScriptEmitter().EmitGrantOrDeny(p)
            .Should().Be("GRANT EXECUTE ON SCHEMA::[app] TO [app];");
    }

    [Fact]
    public void Permission_database_level_grant_omits_target_object()
    {
        Permission p = new("app", "CONNECT", PermissionState.Grant,
            "DATABASE", null, null, null);
        new PermissionScriptEmitter().EmitGrantOrDeny(p)
            .Should().Be("GRANT CONNECT ON DATABASE TO [app];");
    }
}
