using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Verifies the six previously-orphaned object kinds (Sequence, Synonym,
/// UserDefinedType, User, Role, Permission) are wired into
/// <see cref="ScriptGenerator.Generate"/>. Until M13-FIX.1 the emitters
/// existed but were never called by the pipeline — a script for an
/// only-source Sequence was silently empty.
/// </summary>
public class ScriptGeneratorOrphanedKindsTests
{
    private static readonly ScriptGenerator Sut = new();

    // ── Sequence ────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyInA_Sequence_emits_CREATE_SEQUENCE()
    {
        Sequence seq = StubSeq("dbo", "OrderNo");
        ComparisonResult result = new([Pair(seq.Identity, DifferenceStatus.OnlyInA, seq, null)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE SEQUENCE [dbo].[OrderNo]");
    }

    [Fact]
    public void OnlyInB_Sequence_emits_DROP_SEQUENCE()
    {
        Sequence seq = StubSeq("dbo", "OrderNo");
        ComparisonResult result = new([Pair(seq.Identity, DifferenceStatus.OnlyInB, null, seq)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP SEQUENCE [dbo].[OrderNo]");
    }

    [Fact]
    public void Different_Sequence_emits_DROP_then_CREATE()
    {
        Sequence src = StubSeq("dbo", "OrderNo", start: 100);
        Sequence tgt = StubSeq("dbo", "OrderNo", start: 1);
        ComparisonResult result = new([Pair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP SEQUENCE [dbo].[OrderNo]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE SEQUENCE [dbo].[OrderNo]", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
    }

    // ── UserDefinedType (alias) ─────────────────────────────────────────────

    [Fact]
    public void OnlyInA_UDT_emits_CREATE_TYPE()
    {
        UserDefinedType udt = new("dbo", "ShortDescription", "nvarchar", MaxLength: 200, Precision: 0, Scale: 0, IsNullable: true);
        ComparisonResult result = new([Pair(udt.Identity, DifferenceStatus.OnlyInA, udt, null)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE TYPE [dbo].[ShortDescription] FROM nvarchar");
    }

    [Fact]
    public void OnlyInB_UDT_emits_DROP_TYPE()
    {
        UserDefinedType udt = new("dbo", "ShortDescription", "nvarchar", 200, 0, 0, true);
        ComparisonResult result = new([Pair(udt.Identity, DifferenceStatus.OnlyInB, null, udt)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP TYPE [dbo].[ShortDescription]");
    }

    [Fact]
    public void Different_UDT_emits_DROP_then_CREATE()
    {
        UserDefinedType src = new("dbo", "ShortDescription", "nvarchar", 400, 0, 0, true);
        UserDefinedType tgt = new("dbo", "ShortDescription", "nvarchar", 200, 0, 0, true);
        ComparisonResult result = new([Pair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP TYPE [dbo].[ShortDescription]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE TYPE [dbo].[ShortDescription]", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
    }

    // ── Synonym ─────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyInA_Synonym_emits_CREATE_SYNONYM()
    {
        Synonym syn = new("dbo", "OrdersAlias", "[OtherDb].[dbo].[Orders]", null, "OtherDb", "dbo", "Orders");
        ComparisonResult result = new([Pair(syn.Identity, DifferenceStatus.OnlyInA, syn, null)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE SYNONYM [dbo].[OrdersAlias] FOR [OtherDb].[dbo].[Orders]");
    }

    [Fact]
    public void OnlyInB_Synonym_emits_DROP_SYNONYM()
    {
        Synonym syn = new("dbo", "OrdersAlias", "[OtherDb].[dbo].[Orders]", null, "OtherDb", "dbo", "Orders");
        ComparisonResult result = new([Pair(syn.Identity, DifferenceStatus.OnlyInB, null, syn)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP SYNONYM [dbo].[OrdersAlias]");
    }

    [Fact]
    public void Different_Synonym_emits_DROP_then_CREATE()
    {
        Synonym src = new("dbo", "OrdersAlias", "[NewDb].[dbo].[Orders]", null, "NewDb", "dbo", "Orders");
        Synonym tgt = new("dbo", "OrdersAlias", "[OldDb].[dbo].[Orders]", null, "OldDb", "dbo", "Orders");
        ComparisonResult result = new([Pair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP SYNONYM [dbo].[OrdersAlias]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE SYNONYM [dbo].[OrdersAlias]", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
    }

    // ── User ────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyInA_User_emits_CREATE_USER()
    {
        DatabaseUser user = new("app_reader", "S", "app_reader_login", "dbo");
        ComparisonResult result = new([Pair(user.Identity, DifferenceStatus.OnlyInA, user, null)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE USER [app_reader]");
    }

    [Fact]
    public void OnlyInB_User_emits_DROP_USER()
    {
        DatabaseUser user = new("legacy", "S", "legacy_login", "dbo");
        ComparisonResult result = new([Pair(user.Identity, DifferenceStatus.OnlyInB, null, user)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP USER [legacy]");
    }

    [Fact]
    public void Different_User_with_only_default_schema_change_emits_ALTER_USER()
    {
        DatabaseUser src = new("app", "S", "app_login", "app_schema");
        DatabaseUser tgt = new("app", "S", "app_login", "dbo");
        ComparisonResult result = new([Pair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER USER [app] WITH DEFAULT_SCHEMA = [app_schema]");
        sql.Should().NotContain("DROP USER [app]");
    }

    [Fact]
    public void Different_User_with_login_change_emits_DROP_then_CREATE()
    {
        DatabaseUser src = new("app", "S", "new_login", "dbo");
        DatabaseUser tgt = new("app", "S", "old_login", "dbo");
        ComparisonResult result = new([Pair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP USER [app]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE USER [app]", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
    }

    // ── Role ────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyInA_Role_emits_CREATE_ROLE_plus_ADD_MEMBER_per_member()
    {
        DatabaseRole role = new("readers", "dbo", ["alice", "bob"]);
        ComparisonResult result = new([Pair(role.Identity, DifferenceStatus.OnlyInA, role, null)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE ROLE [readers]");
        sql.Should().Contain("ALTER ROLE [readers] ADD MEMBER [alice]");
        sql.Should().Contain("ALTER ROLE [readers] ADD MEMBER [bob]");
    }

    [Fact]
    public void OnlyInB_Role_emits_DROP_ROLE()
    {
        DatabaseRole role = new("readers", "dbo", []);
        ComparisonResult result = new([Pair(role.Identity, DifferenceStatus.OnlyInB, null, role)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP ROLE [readers]");
    }

    [Fact]
    public void Different_Role_same_owner_emits_member_set_delta()
    {
        DatabaseRole src = new("readers", "dbo", ["alice", "carol"]);
        DatabaseRole tgt = new("readers", "dbo", ["alice", "bob"]);
        ComparisonResult result = new([Pair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER ROLE [readers] DROP MEMBER [bob]");
        sql.Should().Contain("ALTER ROLE [readers] ADD MEMBER [carol]");
        sql.Should().NotContain("ALTER ROLE [readers] ADD MEMBER [alice]"); // already on target
        sql.Should().NotContain("CREATE ROLE [readers]");
    }

    [Fact]
    public void Different_Role_owner_change_emits_DROP_then_CREATE()
    {
        DatabaseRole src = new("readers", "newOwner", []);
        DatabaseRole tgt = new("readers", "dbo", []);
        ComparisonResult result = new([Pair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP ROLE [readers]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE ROLE [readers]", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
    }

    // ── Permission ──────────────────────────────────────────────────────────

    [Fact]
    public void OnlyInA_Permission_emits_GRANT_when_IgnorePermissions_is_OFF()
    {
        Permission p = new("alice", "SELECT", PermissionState.Grant, "OBJECT_OR_COLUMN", "dbo", "Orders", null);
        ComparisonResult result = new([Pair(p.Identity, DifferenceStatus.OnlyInA, p, null)]);

        string sql = Sut.Generate(result, options: ComparisonOptions.None);

        sql.Should().Contain("GRANT SELECT ON [dbo].[Orders] TO [alice]");
    }

    [Fact]
    public void OnlyInB_Permission_emits_REVOKE_when_IgnorePermissions_is_OFF()
    {
        Permission p = new("alice", "SELECT", PermissionState.Grant, "OBJECT_OR_COLUMN", "dbo", "Orders", null);
        ComparisonResult result = new([Pair(p.Identity, DifferenceStatus.OnlyInB, null, p)]);

        string sql = Sut.Generate(result, options: ComparisonOptions.None);

        sql.Should().Contain("REVOKE SELECT ON [dbo].[Orders] FROM [alice]");
    }

    [Fact]
    public void Permissions_are_skipped_when_IgnorePermissions_flag_is_ON()
    {
        Permission p = new("alice", "SELECT", PermissionState.Grant, "OBJECT_OR_COLUMN", "dbo", "Orders", null);
        ComparisonResult result = new([Pair(p.Identity, DifferenceStatus.OnlyInA, p, null)]);

        // ComparisonOptions.Default contains IgnorePermissions.
        string sql = Sut.Generate(result, options: ComparisonOptions.Default);

        sql.Should().NotContain("GRANT");
        sql.Should().NotContain("REVOKE");
    }

    // ── Ordering ────────────────────────────────────────────────────────────

    [Fact]
    public void Sequences_appear_in_the_prologue_before_tables()
    {
        Sequence seq = StubSeq("dbo", "OrderNo");
        Table table = new("dbo", "Orders", [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            Pair(seq.Identity, DifferenceStatus.OnlyInA, seq, null),
            Pair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result);

        int seqIdx = sql.IndexOf("CREATE SEQUENCE", StringComparison.Ordinal);
        int tableIdx = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        seqIdx.Should().BeGreaterThan(0);
        tableIdx.Should().BeGreaterThan(seqIdx);
    }

    [Fact]
    public void Synonyms_appear_between_triggers_and_FKs()
    {
        Synonym syn = new("dbo", "OrdersAlias", "[Other].[dbo].[Orders]", null, "Other", "dbo", "Orders");
        Table table = new("dbo", "Orders", [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            Pair(syn.Identity, DifferenceStatus.OnlyInA, syn, null),
            Pair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result);

        int tableIdx = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        int synIdx = sql.IndexOf("CREATE SYNONYM", StringComparison.Ordinal);
        synIdx.Should().BeGreaterThan(tableIdx);
    }

    [Fact]
    public void Permissions_appear_after_FKs_at_the_end_of_the_script()
    {
        Permission p = new("alice", "SELECT", PermissionState.Grant, "OBJECT_OR_COLUMN", "dbo", "Orders", null);
        Table table = new("dbo", "Orders", [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            Pair(p.Identity, DifferenceStatus.OnlyInA, p, null),
            Pair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result, options: ComparisonOptions.None);

        int tableIdx = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        int grantIdx = sql.IndexOf("GRANT SELECT", StringComparison.Ordinal);
        grantIdx.Should().BeGreaterThan(tableIdx);
    }

    [Fact]
    public void Users_and_Roles_appear_before_tables_in_the_prologue()
    {
        DatabaseUser user = new("app", "S", "app_login", "dbo");
        DatabaseRole role = new("readers", "dbo", ["app"]);
        Table table = new("dbo", "Orders", [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            Pair(role.Identity, DifferenceStatus.OnlyInA, role, null),
            Pair(user.Identity, DifferenceStatus.OnlyInA, user, null),
            Pair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result);

        int userIdx = sql.IndexOf("CREATE USER", StringComparison.Ordinal);
        int roleIdx = sql.IndexOf("CREATE ROLE", StringComparison.Ordinal);
        int tableIdx = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        userIdx.Should().BeGreaterThan(0);
        roleIdx.Should().BeGreaterThan(userIdx); // users before roles (roles reference users)
        tableIdx.Should().BeGreaterThan(roleIdx);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static DifferencePair Pair(ObjectIdentity id, DifferenceStatus status, object? a, object? b) =>
        new(id, status, a, b);

    private static Sequence StubSeq(string schema, string name, long start = 1, long inc = 1) =>
        new(schema, name, "bigint", StartValue: start, Increment: inc,
            MinValue: null, MaxValue: null, IsCycling: false, IsCached: true, CacheSize: null);
}
