using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tables.Commands.UpdateTable;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Tables;

public class UpdateTableCommandHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private UpdateTableCommandHandler CreateSut() => new(_tableRepo, _appAccessService);

    [Fact]
    public async Task HandleAsync_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _tableRepo.UpdateAsync(id, "New Name", "Item", "Items", "desc", "icon", Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateTableCommand(id, "New Name", "Item", "Items", "desc", "icon"));

        await _tableRepo.Received(1).UpdateAsync(id, "New Name", "Item", "Items", "desc", "icon", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateTableCommand(Guid.NewGuid(), "", null, null, null, null)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_TableNotFound_ThrowsNotFoundException()
    {
        var id = Guid.NewGuid();
        _tableRepo.UpdateAsync(id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateTableCommand(id, "Name", null, null, null, null)))
            .Should().ThrowAsync<NotFoundException>();
    }
}
