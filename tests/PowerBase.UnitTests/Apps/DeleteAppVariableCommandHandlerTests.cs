/*
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.DeleteAppVariable;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Apps;

public class DeleteAppVariableCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppVariableRepository _variableRepo = Substitute.For<IAppVariableRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private DeleteAppVariableCommandHandler CreateSut() =>
        new(_appRepo, _variableRepo, _appAccessService);

    [Fact]
    public async Task HandleAsync_ValidCommand_DeletesAppVariable()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var varPublicId = Guid.NewGuid();
        var appId = 123L;

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);

        var command = new DeleteAppVariableCommand(appPublicId, varPublicId);
        var sut = CreateSut();

        // Act
        await sut.HandleAsync(command);

        // Assert
        await _appAccessService.Received(1).RequireByAppPublicIdAsync(appPublicId, AppAccess.Admin, Arg.Any<CancellationToken>());
        await _variableRepo.Received(1).DeleteAsync(appId, varPublicId, Arg.Any<CancellationToken>());
    }
}

*/
