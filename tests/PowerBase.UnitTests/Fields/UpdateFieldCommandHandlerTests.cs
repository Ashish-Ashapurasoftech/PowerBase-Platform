/*
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Fields;

public class UpdateFieldCommandHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private UpdateFieldCommandHandler CreateSut() => new(_tableRepo, _fieldRepo, _appAccessService);

    private static UpdateFieldCommand MakeCommand(Guid tableId, Guid fieldId, string name = "New Name")
        => new(tableId, fieldId, name, "lbl", "desc", false, null, false, false, false, true);

    [Fact]
    public async Task HandleAsync_ValidCommand_CallsUpdate()
    {
        var tablePublicId = Guid.NewGuid();
        var fieldPublicId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tablePublicId).Returns(new AppTable { Id = 5, PublicId = tablePublicId });
        _fieldRepo.UpdateAsync(fieldPublicId, 5, "New Name", "lbl", "desc",
            false, null, false, false, false, true, Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        await sut.HandleAsync(MakeCommand(tablePublicId, fieldPublicId));

        await _fieldRepo.Received(1).UpdateAsync(
            fieldPublicId, 5, "New Name", "lbl", "desc",
            false, null, false, false, false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(MakeCommand(Guid.NewGuid(), Guid.NewGuid(), "")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_FieldNotFound_ThrowsNotFoundException()
    {
        var tablePublicId = Guid.NewGuid();
        var fieldPublicId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tablePublicId).Returns(new AppTable { Id = 5, PublicId = tablePublicId });
        _fieldRepo.UpdateAsync(fieldPublicId, 5,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(MakeCommand(tablePublicId, fieldPublicId)))
            .Should().ThrowAsync<NotFoundException>();
    }
}

*/
