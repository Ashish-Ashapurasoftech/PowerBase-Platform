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

    private UpdateFieldCommandHandler CreateSut() => new(_tableRepo, _fieldRepo);

    [Fact]
    public async Task HandleAsync_ValidCommand_CallsUpdate()
    {
        var tablePublicId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tablePublicId).Returns(new AppTable { Id = 5, PublicId = tablePublicId });
        _fieldRepo.GetByIdInTableAsync(10, 5).Returns(new AppField { Id = 10, AppTableId = 5 });
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateFieldCommand(tablePublicId, 10, "My Label", "desc", true));

        await _fieldRepo.Received(1).UpdateAsync(Arg.Is<AppField>(f =>
            f.Label == "My Label" && f.Description == "desc" && f.IsRequired));
    }

    [Fact]
    public async Task HandleAsync_FieldNotInTable_ThrowsNotFoundException()
    {
        var tablePublicId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tablePublicId).Returns(new AppTable { Id = 5 });
        _fieldRepo.GetByIdInTableAsync(99, 5).Returns<AppField>(_ => throw new NotFoundException("Field", 99L));
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateFieldCommand(tablePublicId, 99, null, null, false)))
            .Should().ThrowAsync<NotFoundException>();
    }
}
