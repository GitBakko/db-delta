using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class ModuleTests
{
    [Fact]
    public void View_carries_identity_with_kind_View()
    {
        View view = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT 1 AS Id;", IsEncrypted: false);
        view.Identity.SchemaName.Should().Be("dbo");
        view.Identity.ObjectName.Should().Be("vCustomer");
        view.Identity.Kind.Should().Be("View");
    }

    [Fact]
    public void StoredProcedure_carries_identity_with_kind_Procedure()
    {
        StoredProcedure proc = new("dbo", "uspGetCustomer", "CREATE PROCEDURE dbo.uspGetCustomer AS RETURN 0;", IsEncrypted: false);
        proc.Identity.SchemaName.Should().Be("dbo");
        proc.Identity.ObjectName.Should().Be("uspGetCustomer");
        proc.Identity.Kind.Should().Be("Procedure");
    }

    [Fact]
    public void Encrypted_module_has_null_body_and_IsEncrypted_true_but_identity_still_resolves()
    {
        View view = new("dbo", "vSecret", Body: null, IsEncrypted: true);
        view.Body.Should().BeNull();
        view.IsEncrypted.Should().BeTrue();
        view.Identity.SchemaName.Should().Be("dbo");
        view.Identity.ObjectName.Should().Be("vSecret");
        view.Identity.Kind.Should().Be("View");
    }

    [Fact]
    public void Modules_are_records_with_value_equality()
    {
        View a = new("dbo", "vA", "BODY", IsEncrypted: false);
        View b = new("dbo", "vA", "BODY", IsEncrypted: false);
        a.Should().Be(b);
    }
}
