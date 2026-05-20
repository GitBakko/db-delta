using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class ColumnExtensionsTests
{
    [Fact]
    public void Column_carries_identity_seed_and_increment_when_identity()
    {
        Column col = new(
            name: "Id",
            dataType: "int",
            isNullable: false,
            ordinal: 1,
            isIdentity: true,
            identitySeed: 1000,
            identityIncrement: 5);

        col.IsIdentity.Should().BeTrue();
        col.IdentitySeed.Should().Be(1000);
        col.IdentityIncrement.Should().Be(5);
    }

    [Fact]
    public void Column_carries_computed_expression_for_persisted_columns()
    {
        Column col = new(
            name: "FullName",
            dataType: "nvarchar(200)",
            isNullable: true,
            ordinal: 5,
            computedExpression: "([FirstName]+N' '+[LastName])",
            isPersistedComputed: true);

        col.ComputedExpression.Should().Be("([FirstName]+N' '+[LastName])");
        col.IsPersistedComputed.Should().BeTrue();
    }

    [Fact]
    public void Column_defaults_have_no_identity_or_computed()
    {
        Column col = new("Name", "nvarchar(100)", true, 2);

        col.IdentitySeed.Should().BeNull();
        col.IdentityIncrement.Should().BeNull();
        col.ComputedExpression.Should().BeNull();
        col.IsPersistedComputed.Should().BeFalse();
    }
}
