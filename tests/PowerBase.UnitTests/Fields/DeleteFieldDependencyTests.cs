using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.BulkDeleteFields;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Fields;

public class DeleteFieldDependencyTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();

    private DeleteFieldCommandHandler CreateDeleteFieldSut() =>
        new(_tableRepo, _fieldRepo, _pipelineRepo, _auditRepo, _uow);

    private BulkDeleteFieldsCommandHandler CreateBulkDeleteFieldSut() =>
        new(_tableRepo, _fieldRepo, _pipelineRepo, _appAccessService, _auditRepo, _uow);

    private static AppTable MakeTable(long id = 5) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = "TestTable", AppId = 1 };

    private static AppField MakeField(Guid publicId, int fid, bool isSystem = false) =>
        new() { Id = 1, PublicId = publicId, Name = "TestField", Fid = fid, IsSystem = isSystem };

    [Fact]
    public async Task DeleteField_WhenFieldReferencedInActivePipeline_ThrowsValidationException()
    {
        // Arrange
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        var fid = 101;
        var field = MakeField(fieldId, fid);
        
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.GetByFidInTableAsync(table.Id, fid, Arg.Any<CancellationToken>()).Returns(field);
        
        // Mock active pipeline reference
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(fid, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)> { ("Pipeline A", "Step 1") });

        var sut = CreateDeleteFieldSut();

        // Act & Assert
        var exception = await sut.Invoking(s => s.HandleAsync(new DeleteFieldCommand(table.PublicId, fid)))
            .Should().ThrowAsync<ValidationException>();

        exception.Which.Errors.Should().ContainKey("Field");
        exception.Which.Errors["Field"][0].Should().Contain("referenced in the following active pipelines");

        await _fieldRepo.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteField_WhenFieldNotReferenced_DeletesSuccessfully()
    {
        // Arrange
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        var fid = 101;
        var field = MakeField(fieldId, fid);
        
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.GetByFidInTableAsync(table.Id, fid, Arg.Any<CancellationToken>()).Returns(field);
        
        // Mock no active references
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(fid, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _fieldRepo.DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>()).Returns(1);

        var sut = CreateDeleteFieldSut();

        // Act
        await sut.HandleAsync(new DeleteFieldCommand(table.PublicId, fid));

        // Assert
        await _fieldRepo.Received(1).DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeleteFields_WhenAnyFieldReferencedInActivePipeline_ThrowsValidationException()
    {
        // Arrange
        var table = MakeTable();
        var fieldId1 = Guid.NewGuid();
        var fieldId2 = Guid.NewGuid();
        var fid1 = 101;
        var fid2 = 102;
        var field1 = MakeField(fieldId1, fid1);
        var field2 = MakeField(fieldId2, fid2);

        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField> { field1, field2 });

        // Mock fid1 as referenced, fid2 as not referenced
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(fid1, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)> { ("Pipeline A", "Step 1") });
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(fid2, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());

        var sut = CreateBulkDeleteFieldSut();

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(new BulkDeleteFieldsCommand(table.PublicId, new List<Guid> { fieldId1, fieldId2 })))
            .Should().ThrowAsync<ValidationException>();

        await _fieldRepo.DidNotReceive().BulkDeleteAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeleteFields_WhenNoFieldReferenced_DeletesSuccessfully()
    {
        // Arrange
        var table = MakeTable();
        var fieldId1 = Guid.NewGuid();
        var fieldId2 = Guid.NewGuid();
        var fid1 = 101;
        var fid2 = 102;
        var field1 = MakeField(fieldId1, fid1);
        var field2 = MakeField(fieldId2, fid2);

        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField> { field1, field2 });

        // Mock no active references
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(fid1, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(fid2, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _fieldRepo.BulkDeleteAsync(Arg.Any<IEnumerable<Guid>>(), table.Id, Arg.Any<CancellationToken>()).Returns(2);

        var sut = CreateBulkDeleteFieldSut();

        // Act
        await sut.HandleAsync(new BulkDeleteFieldsCommand(table.PublicId, new List<Guid> { fieldId1, fieldId2 }));

        // Assert
        await _fieldRepo.Received(1).BulkDeleteAsync(Arg.Any<IEnumerable<Guid>>(), table.Id, Arg.Any<CancellationToken>());
    }
}
