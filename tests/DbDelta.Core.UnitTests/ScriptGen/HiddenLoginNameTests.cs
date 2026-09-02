using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A login name the reading connection cannot see is not a login name that
/// differs. <c>UserReader</c> LEFT JOINs <c>sys.server_principals</c>, and
/// metadata visibility returns NULL for every login a least-privilege account
/// does not own — so under such an account EVERY user came back with a null
/// login, every user compared Different, and the script dropped and re-created
/// principals that were already correct, losing their role memberships on the
/// way. Same mechanism the permission refusal documents, one catalog view over.
/// </summary>
public class HiddenLoginNameTests
{
    private static DatabaseUser Visible(string name, string login, string schema = "dbo") =>
        new(name, "S", login, schema);

    private static DatabaseUser Hidden(string name, string schema = "dbo") =>
        new(name, "S", null, schema) { LoginNameIsHidden = true };

    private static ComparisonResult Compare(DatabaseUser a, DatabaseUser b) =>
        new ComparisonEngine().Compare(
            new Database("d", [], [], [], [], [], []) { Users = [a] },
            new Database("d", [], [], [], [], [], []) { Users = [b] },
            ComparisonOptions.Default);

    // ── The diff half ──────────────────────────────────────────────────────

    [Fact]
    public void A_login_name_the_connection_cannot_see_does_not_make_the_user_different()
    {
        ComparisonResult r = Compare(Visible("appuser", "appuser_login"), Hidden("appuser"));

        r.Differences.Should().ContainSingle(p => p.Identity.Kind == "User")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Two_visible_login_names_that_differ_still_flag_the_user_different()
    {
        ComparisonResult r = Compare(Visible("appuser", "login_a"), Visible("appuser", "login_b"));

        r.Differences.Should().ContainSingle(p => p.Identity.Kind == "User")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void A_hidden_login_facing_a_user_with_no_login_at_all_is_still_different()
    {
        // The flag is the whole point: 'mapped to a login I cannot name' and
        // 'mapped to no login' are two different users, and only the flag
        // separates them once the name is NULL on both sides.
        ComparisonResult r = Compare(Hidden("appuser"), new DatabaseUser("appuser", "S", null, "dbo"));

        r.Differences.Should().ContainSingle(p => p.Identity.Kind == "User")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }

    // ── The emission half ──────────────────────────────────────────────────

    [Fact]
    public void A_user_whose_login_name_is_hidden_produces_no_script()
    {
        ComparisonResult r = Compare(Visible("appuser", "appuser_login"), Hidden("appuser"));

        string sql = new ScriptGenerator().Generate(r);

        sql.Should().NotContain("DROP USER").And.NotContain("CREATE USER");
    }

    [Fact]
    public void A_hidden_login_name_and_a_changed_default_schema_alter_instead_of_dropping()
    {
        // ScriptGenerator asks the same question the engine does, in its own
        // code: if only that one answers 'hidden', the pair is Identical for
        // the engine and a DROP + CREATE for the emitter.
        ComparisonResult r = Compare(Visible("appuser", "appuser_login", "app"), Hidden("appuser", "report"));

        string sql = new ScriptGenerator().Generate(r);

        sql.Should().Contain("ALTER USER [appuser] WITH DEFAULT_SCHEMA = [app];");
        sql.Should().NotContain("DROP USER");
    }

    [Fact]
    public void A_hidden_login_name_is_refused_instead_of_becoming_WITHOUT_LOGIN()
    {
        Action act = () => new UserScriptEmitter().EmitCreate(Hidden("appuser"));

        act.Should().Throw<UnscriptableUserException>()
            .Which.UserName.Should().Be("appuser");
    }

    [Fact]
    public void A_user_genuinely_without_a_login_still_emits_WITHOUT_LOGIN()
    {
        new UserScriptEmitter().EmitCreate(new DatabaseUser("orphan", "S", null, "dbo"))
            .Should().Be("CREATE USER [orphan] WITHOUT LOGIN;");
    }

    /// <summary>
    /// The refusal has to say WHICH of the two it is. Both come back as a NULL
    /// login name with an authentication_type that says "mapped", so one flag
    /// cannot tell them apart — and the smoke of 2026-09-02 measured what that
    /// costs: connected as sa with sysadmin = 1 against a real database, the
    /// message blamed metadata visibility for a login that simply did not
    /// exist, and sent the reader after a permission problem there was no way
    /// to fix. The refusal itself is unchanged; only the words are.
    /// </summary>
    [Fact]
    public void An_orphaned_user_is_refused_for_the_reason_it_actually_has()
    {
        Action act = () => new UserScriptEmitter().EmitCreate(Orphaned("appuser"));

        act.Should().Throw<UnscriptableUserException>()
            .Which.Message.Should().Contain("no longer exists")
            .And.NotContain("cannot see",
                "as sa there is nothing hidden — saying so sends the reader after a ghost");
    }

    [Fact]
    public void A_hidden_login_name_is_still_refused_for_ITS_reason()
    {
        Action act = () => new UserScriptEmitter().EmitCreate(Hidden("appuser"));

        act.Should().Throw<UnscriptableUserException>()
            .Which.Message.Should().Contain("cannot see")
            .And.NotContain("no longer exists");
    }

    /// <summary>
    /// The control. An orphaned user is still a user mapped to a login as far
    /// as the catalog is concerned, so it must keep comparing Identical against
    /// a reader that CAN see the login — the rule this whole file exists for.
    /// </summary>
    [Fact]
    public void An_orphaned_user_does_not_become_Different()
    {
        ComparisonResult r = Compare(Visible("appuser", "appuser_login"), Orphaned("appuser"));

        r.Differences.Where(p => p.Identity.Kind == "User")
            .Should().OnlyContain(p => p.Status == DifferenceStatus.Identical);
    }

    private static DatabaseUser Orphaned(string name, string schema = "dbo") =>
        new(name, "S", null, schema) { LoginNameIsHidden = true, LoginIsOrphaned = true };
}
