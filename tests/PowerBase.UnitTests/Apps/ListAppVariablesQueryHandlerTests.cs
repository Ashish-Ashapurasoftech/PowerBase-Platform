/*
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Queries.ListAppVariables;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Apps;

public class ListAppVariablesQueryHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppVariableRepository _variableRepo = Substitute.For<IAppVariableRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private ListAppVariablesQueryHandler CreateSut() =>
        new(_appRepo, _variableRepo, _appAccessService);

    [Fact]
    public async Task HandleAsync_ValidQuery_ReturnsVariablesList()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var appId = 123L;

        var variables = new List<AppVariable>
        {
            new() { Id = 1, Name = "V1", Value = "Val1" },
            new() { Id = 2, Name = "V2", Value = "Val2" }
        };

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.ListAsync(appId, Arg.Any<CancellationToken>()).Returns(variables);

        var query = new ListAppVariablesQuery(appPublicId);
        var sut = CreateSut();

        // Act
        var result = await sut.HandleAsync(query);

        // Assert
        result.Should().BeEquivalentTo(variables);
        await _appAccessService.Received(1).RequireByAppPublicIdAsync(appPublicId, AppAccess.Read, Arg.Any<CancellationToken>());
        await _variableRepo.Received(1).ListAsync(appId, Arg.Any<CancellationToken>());
    }
}

*/
