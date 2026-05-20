using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.UpdateApp;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

public class UpdateAppCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private UpdateAppCommandHandler CreateSut() => new(_appRepo, _appAccessService);

    [Fact]
    public async Task HandleAsync_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _appRepo.UpdateAsync(id, "New Name", "desc", null, null, Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateAppCommand(id, "New Name", "desc", null, null));

        await _appRepo.Received(1).UpdateAsync(id, "New Name", "desc", null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateAppCommand(Guid.NewGuid(), "", null, null, null)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_NameTooLong_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateAppCommand(Guid.NewGuid(), new string('x', 201), null, null, null)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_AppNotFound_ThrowsNotFoundException()
    {
        var id = Guid.NewGuid();
        _appRepo.UpdateAsync(id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateAppCommand(id, "Name", null, null, null)))
            .Should().ThrowAsync<NotFoundException>();
    }
}
