using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tables.Commands.UpdateTable;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Tables;

public class UpdateTableCommandHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly FormulaEngine _engine = new();

    private UpdateTableCommandHandler CreateSut() => new(_tableRepo, _fieldRepo, _appAccessService, _auditRepo, _engine);

    [Fact]
    public async Task HandleAsync_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(id, Arg.Any<CancellationToken>()).Returns(new AppTable { PublicId = id, Name = "Old Name" });
        _tableRepo.UpdateAsync(id, "New Name", "Item", "Items", "desc", "icon", null, null, null, null, Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateTableCommand(id, "New Name", "Item", "Items", "desc", "icon"));

        await _tableRepo.Received(1).UpdateAsync(id, "New Name", "Item", "Items", "desc", "icon", null, null, null, null, Arg.Any<CancellationToken>());
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
        _tableRepo.GetByPublicIdAsync(id, Arg.Any<CancellationToken>()).Returns((AppTable)null!);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateTableCommand(id, "Name", null, null, null, null)))
            .Should().ThrowAsync<NotFoundException>();
    }
}
