using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.CreateAppVariable;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

public class CreateAppVariableCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppVariableRepository _variableRepo = Substitute.For<IAppVariableRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private CreateAppVariableCommandHandler CreateSut() =>
        new(_appRepo, _variableRepo, _appAccessService);

    [Fact]
    public async Task HandleAsync_ValidCommand_CreatesAndReturnsAppVariable()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var varPublicId = Guid.NewGuid();
        var appId = 123L;
        var name = "AppName";
        var value = "MyVal";
        var description = "MyDesc";

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.CountAsync(appId, Arg.Any<CancellationToken>()).Returns(0);
        _variableRepo.NameExistsAsync(appId, name, Arg.Any<CancellationToken>()).Returns(false);
        _variableRepo.CreateAsync(Arg.Any<AppVariable>(), Arg.Any<CancellationToken>()).Returns(varPublicId);

        var expectedVariable = new AppVariable
        {
            Id = 1L,
            PublicId = varPublicId,
            AppId = appId,
            Name = name,
            Value = value,
            Description = description
        };
        _variableRepo.GetByPublicIdAsync(appId, varPublicId, Arg.Any<CancellationToken>()).Returns(expectedVariable);

        var command = new CreateAppVariableCommand(appPublicId, name, value, description);
        var sut = CreateSut();

        // Act
        var result = await sut.HandleAsync(command);

        // Assert
        result.Should().NotBeNull();
        result.PublicId.Should().Be(varPublicId);
        result.Name.Should().Be(name);
        result.Value.Should().Be(value);
        result.Description.Should().Be(description);

        await _appAccessService.Received(1).RequireByAppPublicIdAsync(appPublicId, AppAccess.Admin, Arg.Any<CancellationToken>());
        await _variableRepo.Received(1).CreateAsync(Arg.Is<AppVariable>(v => v.AppId == appId && v.Name == name && v.Value == value), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VariableLimitReached_ThrowsConflictException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var appId = 123L;
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.CountAsync(appId, Arg.Any<CancellationToken>()).Returns(10); // Limit is 10

        var command = new CreateAppVariableCommand(appPublicId, "VarName", "Val", null);
        var sut = CreateSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<ConflictException>()
            .WithMessage("App variables limit of 10 reached.");
    }

    [Fact]
    public async Task HandleAsync_DuplicateName_ThrowsDuplicateException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var appId = 123L;
        var name = "DuplicateName";

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.CountAsync(appId, Arg.Any<CancellationToken>()).Returns(5);
        _variableRepo.NameExistsAsync(appId, name, Arg.Any<CancellationToken>()).Returns(true);

        var command = new CreateAppVariableCommand(appPublicId, name, "Val", null);
        var sut = CreateSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Theory]
    [InlineData("", "Val", "Desc")]
    [InlineData("Name", "", "Desc")]
    public async Task HandleAsync_EmptyFields_ThrowsValidationException(string name, string value, string desc)
    {
        // Arrange
        var command = new CreateAppVariableCommand(Guid.NewGuid(), name, value, desc);
        var sut = CreateSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_FieldLengthsTooLong_ThrowsValidationException()
    {
        // Arrange
        var longName = new string('a', 101);
        var longValue = new string('b', 501);
        var longDesc = new string('c', 501);

        var command1 = new CreateAppVariableCommand(Guid.NewGuid(), longName, "Val", "Desc");
        var command2 = new CreateAppVariableCommand(Guid.NewGuid(), "Name", longValue, "Desc");
        var command3 = new CreateAppVariableCommand(Guid.NewGuid(), "Name", "Val", longDesc);

        var sut = CreateSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command1)).Should().ThrowAsync<ValidationException>();
        await sut.Invoking(s => s.HandleAsync(command2)).Should().ThrowAsync<ValidationException>();
        await sut.Invoking(s => s.HandleAsync(command3)).Should().ThrowAsync<ValidationException>();
    }
}
