using DbDelta.Core.Abstractions;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Abstractions;

public class ResultTests
{
    [Fact]
    public void Success_carries_value_and_no_error()
    {
        Result<int> r = Result<int>.Success(42);

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
        r.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_carries_error_and_no_value()
    {
        Result<int> r = Result<int>.Failure(new Error(ErrorCode.CannotConnect, "boom"));

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.CannotConnect);
        r.Error.Message.Should().Be("boom");
    }
}
