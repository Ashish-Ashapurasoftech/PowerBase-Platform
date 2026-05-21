using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.UpdateAppVariable;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

public class UpdateAppVariableCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppVariableRepository _variableRepo = Substitute.For<IAppVariableRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private UpdateAppVariableCommandHandler CreateSut() =>
        new(_appRepo, _variableRepo, _appAccessService);

    [Fact]
    public async Task HandleAsync_ValidCommand_UpdatesAppVariable()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var varPublicId = Guid.NewGuid();
        var appId = 123L;
        var existingName = "OldName";
        var newName = "NewName";
        var newValue = "NewVal";
        var newDesc = "NewDesc";

        var existing = new AppVariable
        {
            AppId = appId,
            PublicId = varPublicId,
            Name = existingName,
            Value = "OldVal",
            Description = "OldDesc"
        };

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.GetByPublicIdAsync(appId, varPublicId, Arg.Any<CancellationToken>()).Returns(existing);
        _variableRepo.NameExistsAsync(appId, newName, Arg.Any<CancellationToken>()).Returns(false);

        var command = new UpdateAppVariableCommand(appPublicId, varPublicId, newName, newValue, newDesc);
        var sut = CreateSut();

        // Act
        await sut.HandleAsync(command);

        // Assert
        await _appAccessService.Received(1).RequireByAppPublicIdAsync(appPublicId, AppAccess.Admin, Arg.Any<CancellationToken>());
        await _variableRepo.Received(1).UpdateAsync(appId, varPublicId, newName, newValue, newDesc, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SameName_DoesNotCheckUniquenessOrThrow()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var varPublicId = Guid.NewGuid();
        var appId = 123L;
        var name = "SameName";

        var existing = new AppVariable
        {
            AppId = appId,
            PublicId = varPublicId,
            Name = name,
            Value = "OldVal"
        };

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.GetByPublicIdAsync(appId, varPublicId, Arg.Any<CancellationToken>()).Returns(existing);

        var command = new UpdateAppVariableCommand(appPublicId, varPublicId, name, "NewVal", null);
        var sut = CreateSut();

        // Act
        await sut.HandleAsync(command);

        // Assert
        await _variableRepo.DidNotReceive().NameExistsAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _variableRepo.Received(1).UpdateAsync(appId, varPublicId, name, "NewVal", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VariableNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var varPublicId = Guid.NewGuid();
        var appId = 123L;

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.GetByPublicIdAsync(appId, varPublicId, Arg.Any<CancellationToken>()).Returns((AppVariable?)null);

        var command = new UpdateAppVariableCommand(appPublicId, varPublicId, "Name", "Val", null);
        var sut = CreateSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task HandleAsync_DuplicateNameOnUpdate_ThrowsDuplicateException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var varPublicId = Guid.NewGuid();
        var appId = 123L;
        var existingName = "OldName";
        var duplicateName = "DuplicateName";

        var existing = new AppVariable
        {
            AppId = appId,
            PublicId = varPublicId,
            Name = existingName,
            Value = "OldVal"
        };

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.GetByPublicIdAsync(appId, varPublicId, Arg.Any<CancellationToken>()).Returns(existing);
        _variableRepo.NameExistsAsync(appId, duplicateName, Arg.Any<CancellationToken>()).Returns(true);

        var command = new UpdateAppVariableCommand(appPublicId, varPublicId, duplicateName, "Val", null);
        var sut = CreateSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Theory]
    [InlineData("", "Val")]
    [InlineData("Name", "")]
    public async Task HandleAsync_InvalidInput_ThrowsValidationException(string name, string value)
    {
        // Arrange
        var command = new UpdateAppVariableCommand(Guid.NewGuid(), Guid.NewGuid(), name, value, null);
        var sut = CreateSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<ValidationException>();
    }
}
