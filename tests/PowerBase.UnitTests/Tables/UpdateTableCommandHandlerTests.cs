using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tables.Commands.UpdateTable;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Tables;

public class UpdateTableCommandHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();

    private UpdateTableCommandHandler CreateSut() => new(_tableRepo);

    [Fact]
    public async Task HandleAsync_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(id).Returns(new AppTable { PublicId = id, Name = "Old" });
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateTableCommand(id, "New Name", "Item", "Items", "desc", "icon"));

        await _tableRepo.Received(1).UpdateAsync(Arg.Is<AppTable>(t =>
            t.Name == "New Name" && t.SingularLabel == "Item" && t.PluralLabel == "Items"));
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateTableCommand(Guid.NewGuid(), "", null, null, null, null)))
            .Should().ThrowAsync<ValidationException>();
    }
}
