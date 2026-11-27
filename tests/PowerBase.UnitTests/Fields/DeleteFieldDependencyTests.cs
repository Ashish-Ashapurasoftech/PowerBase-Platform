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
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();

    private DeleteFieldCommandHandler CreateDeleteFieldSut() =>
        new(_tableRepo, _fieldRepo, _pipelineRepo, _auditRepo, _uow, _relRepo);

    private BulkDeleteFieldsCommandHandler CreateBulkDeleteFieldSut() =>
        new(_tableRepo, _fieldRepo, _pipelineRepo, _appAccessService, _auditRepo, _uow, _relRepo);

    private static AppTable MakeTable(long id = 5) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = "TestTable", AppId = 1 };

    private static AppField MakeField(Guid publicId, int fid, bool isSystem = false) =>
        new() { Id = 1, PublicId = publicId, Name = "TestField", Fid = fid, IsSystem = isSystem, AppTableId = 5 };

    [Fact]
    public async Task DeleteField_WhenFieldReferencedInActivePipeline_ThrowsValidationException()
    {
        // Arrange
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        var fid = 101;
        var field = MakeField(fieldId, fid);
        
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>()).Returns(field);
        
        // Mock active pipeline reference
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Is<PowerBase.Domain.Entities.AppField>(f => f.Fid == fid), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)> { ("Pipeline A", "Step 1") });

        var sut = CreateDeleteFieldSut();

        // Act & Assert
        var exception = await sut.Invoking(s => s.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId)))
            .Should().ThrowAsync<ValidationException>();

        exception.Which.Errors.Should().ContainKey("Field");
        exception.Which.Errors["Field"][0].Should().Contain("referenced in the following PowerFlows");

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
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>()).Returns(field);
        
        // Mock no active references
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Is<PowerBase.Domain.Entities.AppField>(f => f.Fid == fid), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _fieldRepo.DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>()).Returns(1);

        var sut = CreateDeleteFieldSut();

        // Act
        await sut.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId));

        // Assert
        await _fieldRepo.Received(1).DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>());
    }

    // A PowerFlow that is switched off does not run, so it must not block a delete (the repository only reports ACTIVE flows, which is
    // what these mocks model). Its steps are then marked invalid, so enabling it later is refused until they are fixed.
    [Fact]
    public async Task DeleteField_WhenOnlyDisabledFlowsUseTheField_DeletesItAndMarksItsStepsInvalid()
    {
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        var fid = 101;
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>()).Returns(MakeField(fieldId, fid));
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Any<AppField>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _fieldRepo.DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>()).Returns(1);

        await CreateDeleteFieldSut().HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId));

        await _fieldRepo.Received(1).DeleteAsync(fieldId, table.Id, Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).InvalidateStepsReferencingFieldAsync(fid, Arg.Is<System.Data.IDbTransaction?>(t => t == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteField_WhenBlockedByAnActiveFlow_ChangesNoFlowSteps()
    {
        var table = MakeTable();
        var fieldId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.GetByPublicIdAsync(fieldId, Arg.Any<CancellationToken>()).Returns(MakeField(fieldId, 101));
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Any<AppField>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)> { ("Pipeline A", "Step 1") });

        await CreateDeleteFieldSut().Invoking(s => s.HandleAsync(new DeleteFieldCommand(table.PublicId, fieldId)))
            .Should().ThrowAsync<ValidationException>();

        await _pipelineRepo.DidNotReceive().InvalidateStepsReferencingFieldAsync(Arg.Any<int>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeleteFields_WhenOnlyDisabledFlowsUseTheFields_DeletesThemAndMarksTheirStepsInvalid()
    {
        var table = MakeTable();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField> { MakeField(id1, 101), MakeField(id2, 102) });
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Any<AppField>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _fieldRepo.BulkDeleteAsync(Arg.Any<IEnumerable<Guid>>(), table.Id, Arg.Any<CancellationToken>()).Returns(2);

        await CreateBulkDeleteFieldSut().HandleAsync(new BulkDeleteFieldsCommand(table.PublicId, new List<Guid> { id1, id2 }));

        await _fieldRepo.Received(1).BulkDeleteAsync(Arg.Any<IEnumerable<Guid>>(), table.Id, Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).InvalidateStepsReferencingFieldAsync(101, Arg.Is<System.Data.IDbTransaction?>(t => t == null), Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).InvalidateStepsReferencingFieldAsync(102, Arg.Is<System.Data.IDbTransaction?>(t => t == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeleteFields_WhenBlockedByAnActiveFlow_ChangesNoFlowSteps()
    {
        var table = MakeTable();
        var id1 = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField> { MakeField(id1, 101) });
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Any<AppField>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)> { ("Pipeline A", "Step 1") });

        await CreateBulkDeleteFieldSut().Invoking(s => s.HandleAsync(new BulkDeleteFieldsCommand(table.PublicId, new List<Guid> { id1 })))
            .Should().ThrowAsync<ValidationException>();

        await _pipelineRepo.DidNotReceive().InvalidateStepsReferencingFieldAsync(Arg.Any<int>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
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
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Is<PowerBase.Domain.Entities.AppField>(f => f.Fid == fid1), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)> { ("Pipeline A", "Step 1") });
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Is<PowerBase.Domain.Entities.AppField>(f => f.Fid == fid2), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
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
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Is<PowerBase.Domain.Entities.AppField>(f => f.Fid == fid1), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Is<PowerBase.Domain.Entities.AppField>(f => f.Fid == fid2), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());
        _fieldRepo.BulkDeleteAsync(Arg.Any<IEnumerable<Guid>>(), table.Id, Arg.Any<CancellationToken>()).Returns(2);

        var sut = CreateBulkDeleteFieldSut();

        // Act
        await sut.HandleAsync(new BulkDeleteFieldsCommand(table.PublicId, new List<Guid> { fieldId1, fieldId2 }));

        // Assert
        await _fieldRepo.Received(1).BulkDeleteAsync(Arg.Any<IEnumerable<Guid>>(), table.Id, Arg.Any<CancellationToken>());
    }
}
