using FluentAssertions;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Common;

public class UnauthorizedActionExceptionTests
{
    [Theory]
    [InlineData("Only app administrators can copy this app.")]
    [InlineData("Only app administrators can copy this app")]
    public void Message_EndsWithOneFullStop(string action)
    {
        var exception = new UnauthorizedActionException(action);

        exception.Message.Should().Be("You are not authorized to perform the action: Only app administrators can copy this app.");
    }
}
