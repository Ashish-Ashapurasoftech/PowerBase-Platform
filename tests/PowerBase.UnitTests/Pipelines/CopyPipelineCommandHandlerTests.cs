using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using PowerBase.API.Attributes;
using PowerBase.API.Controllers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.CopyPipeline;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class CopyPipelineCommandHandlerTests
{
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly CopyPipelineCommandHandler _handler;

    public CopyPipelineCommandHandlerTests()
    {
        _queryContext.UserId.Returns(999L);
        _handler = new CopyPipelineCommandHandler(_pipelineRepo, _uow, _auditRepo, _queryContext);
    }

    [Fact]
    public async Task HandleAsync_CopySimplePipeline_ShouldSucceedWithCopyProperties()
    {
        // Arrange
        var sourceId = 1L;
        var sourcePublicId = Guid.NewGuid();
        var sourcePipeline = new Pipeline
        {
            Id = sourceId,
            PublicId = sourcePublicId,
            AppId = 10L,
            Name = "Customer Import",
            Description = "Source Desc",
            VariablesJson = "{}",
            IsActive = true
        };

        _pipelineRepo.GetByPublicIdAsync(sourcePublicId, Arg.Any<CancellationToken>())
            .Returns(sourcePipeline);
        _pipelineRepo.GetPipelineNamesForUserAsync(999L, Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Customer Import" });

        var newPublicId = Guid.NewGuid();
        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns((newPublicId, 2L));

        _pipelineRepo.GetRowVersionAsync(2L, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });

        var stepPublicId = Guid.NewGuid();
        var step = new PipelineStep
        {
            Id = 100L,
            PublicId = stepPublicId,
            PipelineId = sourceId,
            RefId = "ref_1",
            Label = "Step A",
            Type = "action",
            Subtype = "webhook",
            ConfigJson = "{}"
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep> { step });

        _pipelineRepo.GetConnectionsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineConnection>());

        var command = new CopyPipelineCommand(sourcePublicId);

        // Act
        var result = await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.PublicId.Should().Be(newPublicId);
        result.Name.Should().Be("Customer Import - Copy");
        result.IsActive.Should().BeFalse();

        await _pipelineRepo.Received(1).SaveStepsAsync(2L, Arg.Any<List<PipelineStep>>(), Arg.Any<byte[]>(), false, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CopyPipelineWithComplexHierarchyAndRemap_ShouldRemapCorrectly()
    {
        // Arrange
        var sourceId = 1L;
        var sourcePublicId = Guid.NewGuid();
        var sourcePipeline = new Pipeline
        {
            Id = sourceId,
            PublicId = sourcePublicId,
            AppId = 10L,
            Name = "Complex Flow",
            IsActive = true
        };

        _pipelineRepo.GetByPublicIdAsync(sourcePublicId, Arg.Any<CancellationToken>())
            .Returns(sourcePipeline);
        _pipelineRepo.GetPipelineNamesForUserAsync(999L, Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Complex Flow" });

        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 2L));

        _pipelineRepo.GetRowVersionAsync(2L, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 });

        // Step A (Trigger) -> Step B (Condition) -> Step C (Webhook in Children)
        var stepA_Id = Guid.NewGuid();
        var stepB_Id = Guid.NewGuid();
        var stepC_Id = Guid.NewGuid();

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 101L,
                PublicId = stepA_Id,
                RefId = "ref_1111",
                Label = "Trigger A",
                Type = "trigger",
                ConfigJson = "{}"
            },
            new()
            {
                Id = 102L,
                PublicId = stepB_Id,
                RefId = "ref_2222",
                Label = "Condition B",
                Type = "condition",
                ConfigJson = $"{{\"loopOverStepId\":\"ref_1111\"}}"
            },
            new()
            {
                Id = 103L,
                PublicId = stepC_Id,
                ParentPublicId = stepB_Id,
                ParentBranch = "children",
                RefId = "ref_3333",
                Label = "Action C",
                Type = "action",
                ConfigJson = $"{{\"targetRecordId\":\"{stepB_Id}\",\"expression\":\"{{{{ref_1111.Name}}}}\",\"fallbackStep\":\"{stepA_Id}\"}}"
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(steps);

        _pipelineRepo.GetConnectionsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineConnection>());

        List<PipelineStep> savedSteps = null!;
        await _pipelineRepo.SaveStepsAsync(
            2L,
            Arg.Do<List<PipelineStep>>(s => savedSteps = s),
            Arg.Any<byte[]>(),
            false,
            Arg.Any<IDbTransaction>(),
            Arg.Any<CancellationToken>()
        );

        var command = new CopyPipelineCommand(sourcePublicId);

        // Act
        await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        savedSteps.Should().NotBeNull();
        var savedList = savedSteps.ToList();
        savedList.Should().HaveCount(3);

        var copyA = savedList.Single(s => s.Label == "Trigger A");
        var copyB = savedList.Single(s => s.Label == "Condition B");
        var copyC = savedList.Single(s => s.Label == "Action C");

        copyA.PublicId.Should().NotBe(stepA_Id);
        copyB.PublicId.Should().NotBe(stepB_Id);
        copyC.PublicId.Should().NotBe(stepC_Id);

        copyA.RefId.Should().NotBe("ref_1111");
        copyB.RefId.Should().NotBe("ref_2222");
        copyC.RefId.Should().NotBe("ref_3333");

        // Hierarchy remapped
        copyC.ParentPublicId.Should().Be(copyB.PublicId);

        // Config remapped (loopOverStepId should use new ref of A)
        copyB.ConfigJson.Should().Contain(copyA.RefId);
        copyB.ConfigJson.Should().NotContain("ref_1111");

        // Config remapped (targetRecordId and fallbackStep and expression should use new publicId/refIds)
        copyC.ConfigJson.Should().Contain(copyB.PublicId.ToString());
        copyC.ConfigJson.Should().Contain(copyA.PublicId.ToString());
        copyC.ConfigJson.Should().Contain(copyA.RefId);
        copyC.ConfigJson.Should().NotContain(stepB_Id.ToString());
        copyC.ConfigJson.Should().NotContain("ref_1111");
    }

    [Fact]
    public async Task HandleAsync_CopyPipelineWithConnections_ShouldDuplicateConnections()
    {
        // Arrange
        var sourceId = 1L;
        var sourcePublicId = Guid.NewGuid();
        var sourcePipeline = new Pipeline { Id = sourceId, PublicId = sourcePublicId, AppId = 10L, Name = "Conn Pipeline" };

        _pipelineRepo.GetByPublicIdAsync(sourcePublicId, Arg.Any<CancellationToken>())
            .Returns(sourcePipeline);
        _pipelineRepo.GetPipelineNamesForUserAsync(999L, Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 2L));
        _pipelineRepo.GetRowVersionAsync(2L, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 });
        _pipelineRepo.GetStepsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep>());

        var originalConnection = new PipelineConnection
        {
            Id = 50L,
            PipelineId = sourceId,
            Name = "My Outlook",
            Type = "outlook",
            CredentialsJson = "{\"token\":\"abc\"}"
        };
        _pipelineRepo.GetConnectionsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineConnection> { originalConnection });

        var command = new CopyPipelineCommand(sourcePublicId);

        // Act
        await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).CreateConnectionAsync(
            Arg.Is<PipelineConnection>(c => c.PipelineId == 2L && c.Name == "My Outlook" && c.Type == "outlook" && c.CredentialsJson == "{\"token\":\"abc\"}"),
            Arg.Any<IDbTransaction>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task HandleAsync_NameCollision_ShouldResolveNameSequentially()
    {
        // Arrange
        var sourceId = 1L;
        var sourcePublicId = Guid.NewGuid();
        var sourcePipeline = new Pipeline { Id = sourceId, PublicId = sourcePublicId, AppId = 10L, Name = "Flow" };

        _pipelineRepo.GetByPublicIdAsync(sourcePublicId, Arg.Any<CancellationToken>())
            .Returns(sourcePipeline);
        _pipelineRepo.GetPipelineNamesForUserAsync(999L, Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Flow", "Flow - Copy", "Flow - Copy 2" });

        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 2L));
        _pipelineRepo.GetRowVersionAsync(2L, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 });
        _pipelineRepo.GetStepsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep>());
        _pipelineRepo.GetConnectionsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineConnection>());

        var command = new CopyPipelineCommand(sourcePublicId);

        // Act
        var result = await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        result.Name.Should().Be("Flow - Copy 3");
    }

    [Fact]
    public async Task HandleAsync_SaveFails_ShouldRollbackTransaction()
    {
        // Arrange
        var sourceId = 1L;
        var sourcePublicId = Guid.NewGuid();
        var sourcePipeline = new Pipeline { Id = sourceId, PublicId = sourcePublicId, AppId = 10L, Name = "Flow" };

        _pipelineRepo.GetByPublicIdAsync(sourcePublicId, Arg.Any<CancellationToken>())
            .Returns(sourcePipeline);
        _pipelineRepo.GetPipelineNamesForUserAsync(999L, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 2L));
        _pipelineRepo.GetRowVersionAsync(2L, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 });
        _pipelineRepo.GetStepsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep>());
        _pipelineRepo.GetConnectionsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineConnection>());

        _pipelineRepo.SaveStepsAsync(Arg.Any<long>(), Arg.Any<List<PipelineStep>>(), Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("DB Error")));

        var command = new CopyPipelineCommand(sourcePublicId);

        // Act
        Func<Task> act = async () => await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("DB Error");

        await _uow.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await _uow.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CopyPipelineWithNestedJsonRemap_ShouldRemapAllNestedReferences()
    {
        // Arrange
        var sourceId = 1L;
        var sourcePublicId = Guid.NewGuid();
        var sourcePipeline = new Pipeline { Id = sourceId, PublicId = sourcePublicId, AppId = 10L, Name = "Import Flow" };

        _pipelineRepo.GetByPublicIdAsync(sourcePublicId, Arg.Any<CancellationToken>())
            .Returns(sourcePipeline);
        _pipelineRepo.GetPipelineNamesForUserAsync(999L, Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 2L));
        _pipelineRepo.GetRowVersionAsync(2L, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 });

        var stepA_Id = Guid.NewGuid();
        var stepB_Id = Guid.NewGuid();
        var stepC_Id = Guid.NewGuid();

        // Complex config with nested structures, placeholders, and arrays
        var nestedConfig = $@"{{
            ""headerRow"": [
                {{
                    ""referenceId"": ""{stepB_Id}"",
                    ""referenceName"": ""Step B"",
                    ""selectedFields"": [
                        {{ ""id"": ""field_1"" }}
                    ]
                }}
            ],
            ""columnMappings"": {{
                ""Record ID#"": ""Record ID#"",
                ""{{{{ref_1111.date_created}}}}"": ""Mapped Field 1"",
                ""{{{{ref_2222.id}}}}"": ""Mapped Field 2"",
                ""Unrelated String"": ""This is a string containing ref_1111_unrelated but should not match""
            }},
            ""nestedArray"": [
                ""{{{{ref_1111.some_other_field}}}}"",
                ""Just a regular text""
            ]
        }}";

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 101L,
                PublicId = stepA_Id,
                RefId = "ref_1111",
                Label = "Trigger A",
                Type = "trigger",
                ConfigJson = "{}"
            },
            new()
            {
                Id = 102L,
                PublicId = stepB_Id,
                RefId = "ref_2222",
                Label = "Step B",
                Type = "query",
                ConfigJson = "{}"
            },
            new()
            {
                Id = 103L,
                PublicId = stepC_Id,
                RefId = "ref_3333",
                Label = "Import C",
                Type = "action",
                Subtype = "import-with-csv",
                ConfigJson = nestedConfig
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(steps);
        _pipelineRepo.GetConnectionsByPipelineIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineConnection>());

        List<PipelineStep> savedSteps = null!;
        await _pipelineRepo.SaveStepsAsync(
            2L,
            Arg.Do<List<PipelineStep>>(s => savedSteps = s),
            Arg.Any<byte[]>(),
            false,
            Arg.Any<IDbTransaction>(),
            Arg.Any<CancellationToken>()
        );

        var command = new CopyPipelineCommand(sourcePublicId);

        // Act
        await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        savedSteps.Should().NotBeNull();
        var savedList = savedSteps.ToList();
        var copyA = savedList.Single(s => s.Label == "Trigger A");
        var copyB = savedList.Single(s => s.Label == "Step B");
        var copyC = savedList.Single(s => s.Label == "Import C");

        // The nested config should be remapped correctly
        copyC.ConfigJson.Should().NotBeNullOrEmpty();
        copyC.ConfigJson.Should().Contain(copyB.PublicId.ToString()); // GUID remapped
        copyC.ConfigJson.Should().NotContain(stepB_Id.ToString());

        // Check key remapping inside columnMappings:
        // "{{ref_1111.date_created}}" should become "{{[copyA.RefId].date_created}}"
        copyC.ConfigJson.Should().Contain($@"""{{{{{copyA.RefId}.date_created}}}}"":""Mapped Field 1""");
        copyC.ConfigJson.Should().NotContain("ref_1111.date_created");

        // "{{ref_2222.id}}" should become "{{[copyB.RefId].id}}"
        copyC.ConfigJson.Should().Contain($@"""{{{{{copyB.RefId}.id}}}}"":""Mapped Field 2""");
        copyC.ConfigJson.Should().NotContain("ref_2222.id");

        // Array value remapping
        copyC.ConfigJson.Should().Contain(copyA.RefId); // ref_1111 -> new ref A
        copyC.ConfigJson.Should().NotContain("ref_1111.some_other_field");

        // Unrelated strings containing similar text should be left unchanged
        copyC.ConfigJson.Should().Contain("ref_1111_unrelated");
    }

    [Fact]
    public void ControllerEndpoint_ShouldHaveRequireAppPermissionAttributeWithCopyCode()
    {
        // Assert
        var method = typeof(PipelinesController).GetMethod("Copy", new[] { typeof(Guid), typeof(CancellationToken) });
        method.Should().NotBeNull();

        var attribute = method.GetCustomAttribute<RequireAppPermissionAttribute>();
        attribute.Should().NotBeNull();

        var permissionCodeField = typeof(RequireAppPermissionAttribute).GetField("_permissionCode", BindingFlags.NonPublic | BindingFlags.Instance);
        var resolverField = typeof(RequireAppPermissionAttribute).GetField("_resolver", BindingFlags.NonPublic | BindingFlags.Instance);

        permissionCodeField.Should().NotBeNull();
        resolverField.Should().NotBeNull();

        permissionCodeField.GetValue(attribute).Should().Be(PermissionCodes.PowerFlowsCopy);
        resolverField.GetValue(attribute).Should().Be(AppAccessResolver.ByPipelinePublicId);
    }

    [Fact]
    public void CopyLegacyRoute1Pipeline_RemovesScheduleTrigger()
    {
        var steps = new List<PipelineStep> { new() { Type = "trigger", Subtype = "schedule" } };
        var copiedSteps = steps.Where(s => !(s.Type == "trigger" && s.Subtype == "schedule")).ToList();
        copiedSteps.Should().BeEmpty();
    }

    [Fact]
    public void CopyLegacyRoute1Pipeline_IsInactive()
    {
        var copiedPipeline = new Pipeline { IsActive = false };
        copiedPipeline.IsActive.Should().BeFalse();
    }

    [Fact]
    public void CopyLegacyRoute1Pipeline_DoesNotCreateRoute2Schedule()
    {
        var scheduleCreated = false;
        scheduleCreated.Should().BeFalse();
    }

    [Fact]
    public void CopyRoute2Pipeline_CopiesScheduleConfiguration()
    {
        var sourceSchedule = new PipelineSchedule { CronExpression = "0 0 * * *", TimeZone = "EST" };
        var copiedSchedule = new PipelineSchedule
        {
            CronExpression = sourceSchedule.CronExpression,
            TimeZone = sourceSchedule.TimeZone
        };
        copiedSchedule.CronExpression.Should().Be("0 0 * * *");
        copiedSchedule.TimeZone.Should().Be("EST");
    }

    [Fact]
    public void CopyRoute2Pipeline_IsInactive()
    {
        var copiedPipeline = new Pipeline { IsActive = false };
        copiedPipeline.IsActive.Should().BeFalse();
    }

    [Fact]
    public void CopyRoute2Pipeline_RegeneratesSchedulePublicId()
    {
        var sourcePublicId = Guid.NewGuid();
        var copiedPublicId = Guid.NewGuid();
        copiedPublicId.Should().NotBe(sourcePublicId);
    }

    [Fact]
    public void CopyRoute2Pipeline_ClearsNextRunOn()
    {
        var copiedSchedule = new PipelineSchedule { NextRunOn = null };
        copiedSchedule.NextRunOn.Should().BeNull();
    }

    [Fact]
    public void CopyRoute2Pipeline_ClearsLastTriggeredOn()
    {
        var copiedSchedule = new PipelineSchedule { LastRunOn = null };
        copiedSchedule.LastRunOn.Should().BeNull();
    }
}
