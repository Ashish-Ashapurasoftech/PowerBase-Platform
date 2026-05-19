using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.UpdateApp;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

public class UpdateAppCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();

    private UpdateAppCommandHandler CreateSut() => new(_appRepo);

    private void SetupHappyPath(Guid publicId)
    {
        _appRepo.GetByPublicIdAsync(publicId).Returns(new App { PublicId = publicId, Name = "Old Name" });
        _appRepo.UpdateAsync(Arg.Any<App>()).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        SetupHappyPath(id);
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateAppCommand(id, "New Name", "desc", null, null));

        await _appRepo.Received(1).UpdateAsync(Arg.Is<App>(a => a.Name == "New Name" && a.Description == "desc"));
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
}
