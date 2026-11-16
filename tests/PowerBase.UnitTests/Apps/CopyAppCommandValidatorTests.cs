using FluentAssertions;
using PowerBase.Application.Apps.Commands.CopyApp;

namespace PowerBase.UnitTests.Apps;

public class CopyAppCommandValidatorTests
{
    private readonly CopyAppCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_HasNoErrors()
    {
        var result = _validator.Validate(new CopyAppCommand(Guid.NewGuid(), "Copy of HR", "Template for a new department"));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyName_ReturnsNameError(string name)
    {
        var result = _validator.Validate(new CopyAppCommand(Guid.NewGuid(), name, null));

        result.Errors.Should().Contain(e => e.PropertyName == nameof(CopyAppCommand.Name));
    }

    [Fact]
    public void Validate_NameLongerThan200_ReturnsNameError()
    {
        var result = _validator.Validate(new CopyAppCommand(Guid.NewGuid(), new string('a', 201), null));

        result.Errors.Should().Contain(e => e.PropertyName == nameof(CopyAppCommand.Name));
    }

    [Fact]
    public void Validate_DescriptionLongerThan500_ReturnsDescriptionError()
    {
        var result = _validator.Validate(new CopyAppCommand(Guid.NewGuid(), "Copy of HR", new string('a', 501)));

        result.Errors.Should().Contain(e => e.PropertyName == nameof(CopyAppCommand.Description));
    }

    [Fact]
    public void Validate_EmptyAppId_ReturnsAppIdError()
    {
        var result = _validator.Validate(new CopyAppCommand(Guid.Empty, "Copy of HR", null));

        result.Errors.Should().Contain(e => e.PropertyName == nameof(CopyAppCommand.AppPublicId));
    }
}
