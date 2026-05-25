using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Fields;

public class DeleteFieldHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private DeleteFieldCommandHandler CreateSut() =>
        new(_tableRepo, _fieldRepo, _appAccessService);

    private static AppTable MakeTable(long id = 5) =>
        new() { Id = id, PublicId = Guid.NewGuid() };

    private static AppField MakeField(Guid publicId, bool isSystem = false) =>
        new() { Id = 1, PublicId = publicId, Name = "Field", IsSystem = isSystem };

    [Fact]
    public async Task DeleteField_ValidCommand_CallsDeleteOnRepo()
    {
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>()).Returns(MakeField(fieldId));
        _fieldRepo.DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        await sut.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId));

        await _fieldRepo.Received(1).DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteField_FieldNotFound_ThrowsNotFoundException()
    {
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>()).Returns((AppField?)null);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId)))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteField_SystemField_ThrowsUnauthorizedActionException()
    {
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>())
            .Returns(MakeField(fieldId, isSystem: true));
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId)))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task DeleteField_DeleteReturnsZero_ThrowsNotFoundException()
    {
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>()).Returns(MakeField(fieldId));
        _fieldRepo.DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId)))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteField_AccessDenied_DoesNotCallRepo()
    {
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        _appAccessService
            .RequireByTablePublicIdAsync(table.PublicId, AppAccess.Admin, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new UnauthorizedActionException("Forbidden")));
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId)))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _fieldRepo.DidNotReceive()
            .DeleteAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
