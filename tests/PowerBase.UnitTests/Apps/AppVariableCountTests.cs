using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.CreateAppVariable;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

/// <summary>An app can have as many variables as it needs — there is no cap on how many can be created.</summary>
public class AppVariableCountTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppVariableRepository _variableRepo = Substitute.For<IAppVariableRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(500)]
    public async Task A_variable_is_created_however_many_the_app_already_has(int existing)
    {
        var appPublicId = Guid.NewGuid();
        var variablePublicId = Guid.NewGuid();
        const long appId = 123;
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.CountAsync(appId, Arg.Any<CancellationToken>()).Returns(existing);
        _variableRepo.NameExistsAsync(appId, "Tax", Arg.Any<CancellationToken>()).Returns(false);
        _variableRepo.CreateAsync(Arg.Any<AppVariable>(), Arg.Any<CancellationToken>()).Returns(variablePublicId);
        _variableRepo.GetByPublicIdAsync(appId, variablePublicId, Arg.Any<CancellationToken>())
            .Returns(new AppVariable { Id = 1, PublicId = variablePublicId, AppId = appId, Name = "Tax", Value = "0.18" });

        var sut = new CreateAppVariableCommandHandler(_appRepo, _variableRepo, _appAccessService);

        var created = await sut.HandleAsync(new CreateAppVariableCommand(appPublicId, "Tax", "0.18", null));

        created.PublicId.Should().Be(variablePublicId);
        await _variableRepo.Received(1).CreateAsync(Arg.Any<AppVariable>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_duplicate_name_is_still_rejected()
    {
        var appPublicId = Guid.NewGuid();
        const long appId = 123;
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _variableRepo.NameExistsAsync(appId, "Tax", Arg.Any<CancellationToken>()).Returns(true);
        var sut = new CreateAppVariableCommandHandler(_appRepo, _variableRepo, _appAccessService);

        await sut.Invoking(s => s.HandleAsync(new CreateAppVariableCommand(appPublicId, "Tax", "0.18", null)))
            .Should().ThrowAsync<DuplicateException>();
    }
}
