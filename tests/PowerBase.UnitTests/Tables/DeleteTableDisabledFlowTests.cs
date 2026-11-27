using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Tables;

/// <summary>Only an ACTIVE PowerFlow blocks deleting a table. A disabled one (every flow imported from Quickbase starts disabled) is let
/// through, and its steps are marked invalid so it cannot be switched on again until they are fixed.</summary>
public class DeleteTableDisabledFlowTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();

    // No relationships in these tests, so the relationship handler is never reached.
    private DeleteTableCommandHandler CreateSut() => new(_tableRepo, _fieldRepo, _pipelineRepo, _auditRepo, _relRepo, null!);

    private AppTable Arrange()
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid(), Name = "Clients", AppId = 1 };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _relRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<Relationship>());
        _pipelineRepo.GetCrossAccountTriggerCountAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns(0);
        return table;
    }

    [Fact]
    public async Task WhenOnlyDisabledFlowsNameTheTable_DeletesItAndMarksTheirStepsInvalid()
    {
        var table = Arrange();
        _pipelineRepo.GetActivePipelineStepsUsingTableAsync(table.PublicId, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)>());

        await CreateSut().HandleAsync(new DeleteTableCommand(table.PublicId));

        await _tableRepo.Received(1).DeleteAsync(table.PublicId, Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).InvalidateStepsUsingTableAsync(table.PublicId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenAnActiveFlowNamesTheTable_BlocksTheDeleteAndChangesNothing()
    {
        var table = Arrange();
        _pipelineRepo.GetActivePipelineStepsUsingTableAsync(table.PublicId, Arg.Any<CancellationToken>())
            .Returns(new List<(string PipelineName, string StepLabel)> { ("Welcome email", "Trigger") });
        _pipelineRepo.GetActivePipelineReferencesForTableFieldsAsync(Arg.Any<IReadOnlyCollection<AppField>>(), table.PublicId, Arg.Any<CancellationToken>())
            .Returns(new List<(int Fid, string PipelineName, string StepLabel)>());

        var thrown = await CreateSut().Invoking(s => s.HandleAsync(new DeleteTableCommand(table.PublicId)))
            .Should().ThrowAsync<ValidationException>();

        thrown.Which.Errors["Table"][0].Should().Contain("Welcome email");
        await _tableRepo.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _pipelineRepo.DidNotReceive().InvalidateStepsUsingTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
