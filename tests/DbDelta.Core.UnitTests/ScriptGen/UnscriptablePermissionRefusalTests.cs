using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A permission row whose securable has no name must not be written. The
/// statement it produces is not a narrower one or an invalid one — it is
/// <c>GRANT &lt;action&gt; TO [principal];</c>, which is the database-scoped
/// form. The missing <c>ON</c> clause widens the grant from one object to every
/// object, silently.
/// </summary>
/// <remarks>
/// The row that triggers this is the row read under reduced privilege:
/// <c>PermissionReader</c> LEFT JOINs the view that names the securable and
/// metadata visibility returns NULL for what the reading login cannot see. That
/// is exactly when granting more must not happen quietly.
/// </remarks>
public class UnscriptablePermissionRefusalTests
{
    private static readonly PermissionScriptEmitter Sut = new();

    private static Permission Row(string classDesc, string? schema, string? name) => new(
        GranteeName: "app_user",
        Action: "SELECT",
        State: PermissionState.Grant,
        ClassDesc: classDesc,
        ObjectSchema: schema,
        ObjectName: name,
        ColumnName: null);

    [Fact]
    public void An_object_permission_with_no_resolvable_name_is_refused()
    {
        Permission p = Row("OBJECT_OR_COLUMN", schema: null, name: null);

        Action act = () => Sut.EmitGrantOrDeny(p);

        act.Should().Throw<UnscriptablePermissionException>()
            .Which.ClassDesc.Should().Be("OBJECT_OR_COLUMN");
    }

    [Fact]
    public void The_revoke_path_is_refused_on_the_same_row()
    {
        // Both paths route through the same ON-clause builder. If one day they
        // stop doing so, this is the test that says the guard covers only half.
        Permission p = Row("OBJECT_OR_COLUMN", schema: null, name: null);

        Action act = () => Sut.EmitRevoke(p);

        act.Should().Throw<UnscriptablePermissionException>();
    }

    [Fact]
    public void A_schema_permission_with_neither_name_is_refused_too()
    {
        // This one used to emit "SCHEMA::[]", then was changed to emit nothing
        // at all — which reads as valid SQL and is a database-wide grant.
        Permission p = Row("SCHEMA", schema: null, name: null);

        Action act = () => Sut.EmitGrantOrDeny(p);

        act.Should().Throw<UnscriptablePermissionException>();
    }

    [Fact]
    public void A_database_scoped_permission_still_emits_without_an_ON_clause()
    {
        // The negative control. A database-scoped grant takes no ON clause by
        // design; if the refusal ever swallows this one, this test is right and
        // the change is wrong.
        Permission p = new(
            GranteeName: "app_user",
            Action: "CONNECT",
            State: PermissionState.Grant,
            ClassDesc: "DATABASE",
            ObjectSchema: null,
            ObjectName: null,
            ColumnName: null);

        string sql = Sut.EmitGrantOrDeny(p);

        sql.Should().Be("GRANT CONNECT TO [app_user];");
    }

    [Fact]
    public void A_named_object_permission_still_carries_its_ON_clause()
    {
        // The second negative control: the guard must not fire on the ordinary
        // case it exists to protect.
        Permission p = Row("OBJECT_OR_COLUMN", schema: "dbo", name: "Ordini");

        string sql = Sut.EmitGrantOrDeny(p);

        sql.Should().Be("GRANT SELECT ON [dbo].[Ordini] TO [app_user];");
    }
}
