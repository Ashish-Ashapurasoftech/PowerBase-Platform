using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbIdResolverTests
{
    [Theory]
    [InlineData("34abb534-30c2-f111-bbff-002324be71d7", QbIdResolver.IdKind.PublicId)]
    [InlineData("34ABB53430C2F111BBFF002324BE71D7", QbIdResolver.IdKind.PublicId)]
    [InlineData("{34abb534-30c2-f111-bbff-002324be71d7}", QbIdResolver.IdKind.PublicId)]
    [InlineData("5", QbIdResolver.IdKind.Number)]
    [InlineData(" 102803 ", QbIdResolver.IdKind.Number)]
    [InlineData("0", QbIdResolver.IdKind.Invalid)]
    [InlineData("-5", QbIdResolver.IdKind.Invalid)]
    [InlineData("+5", QbIdResolver.IdKind.Invalid)]
    [InlineData("5.0", QbIdResolver.IdKind.Invalid)]
    [InlineData("1e3", QbIdResolver.IdKind.Invalid)]
    [InlineData("bck7gp3q2", QbIdResolver.IdKind.Invalid)]
    [InlineData("99999999999999999999", QbIdResolver.IdKind.Invalid)]
    [InlineData("", QbIdResolver.IdKind.Missing)]
    [InlineData(null, QbIdResolver.IdKind.Missing)]
    public void Classify_GuidNumberOrInvalid(string? raw, QbIdResolver.IdKind expected)
    {
        QbIdResolver.Classify(raw, out _, out _).Should().Be(expected);
    }

    [Fact]
    public void Classify_Number_ReturnsValue()
    {
        QbIdResolver.Classify("42", out _, out var n);
        n.Should().Be(42);
    }
}
