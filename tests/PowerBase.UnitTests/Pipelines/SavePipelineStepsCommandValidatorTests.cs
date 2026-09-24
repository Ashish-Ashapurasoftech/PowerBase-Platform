using FluentAssertions;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using Xunit;
using System;
using System.Collections.Generic;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

public class SavePipelineStepsCommandValidatorTests
{
    private readonly SavePipelineStepsCommandValidator _validator = new();

    [Theory]
    [InlineData("{\"duration\":1,\"unit\":\"seconds\"}", 1)]
    [InlineData("{\"duration\":30,\"unit\":\"minutes\"}", 1800)]
    [InlineData("{\"duration\":0.5,\"unit\":\"minutes\"}", 30)]
    [InlineData("{\"duration\":10}", 10)]
    [InlineData("{\"durationText\":\"5m\",\"duration\":10,\"unit\":\"seconds\"}", 300)]
    [InlineData("{\"durationText\":\"2m 30s\"}", 150)]
    [InlineData("{\"durationText\":\"4 minutes, 56 seconds\"}", 296)]
    [InlineData("{\"durationText\":\"4:13\"}", 253)]
    public void PauseDuration_ValidConfig_UsesExpectedSeconds(string json, int seconds)
    {
        PauseStepConfig.ParseDuration(json).TotalSeconds.Should().Be(seconds);
    }

    [Theory]
    [InlineData("{\"duration\":0,\"unit\":\"seconds\"}")]
    [InlineData("{\"duration\":31,\"unit\":\"minutes\"}")]
    [InlineData("{\"duration\":1,\"unit\":\"days\"}")]
    [InlineData("{\"duration\":2,\"unit\":\"unknown\"}")]
    [InlineData("{\"durationText\":\"30 min, 30 sec\"}")]
    [InlineData("{\"durationText\":\"5m nonsense\",\"duration\":10}")]
    [InlineData("invalid json")]
    public async System.Threading.Tasks.Task PauseDuration_InvalidConfig_FailsValidation(string json)
    {
        var pause = new SavePipelineStepDto { PublicId = Guid.NewGuid(), RefId = "pause", Type = "action", Subtype = "pause", ConfigJson = json, IsValidated = true };
        var result = await _validator.ValidateAsync(new SavePipelineStepsCommand(Guid.NewGuid(), new() { pause }, Array.Empty<byte>()));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PauseWait_IsControlFlow_NotCatchableStepError()
    {
        PipelineEngine.IsCatchablePipelineStepError(new PipelineWaitException(DateTime.UtcNow.AddSeconds(1))).Should().BeFalse();
    }

    [Fact]
    public async System.Threading.Tasks.Task ErrorDetails_AreAvailableInNestedRecoveryButNotOutsideIt()
    {
        SavePipelineStepDto Mapping(string id) => new()
        {
            PublicId = Guid.NewGuid(), RefId = id, Type = "action", Subtype = "send-email",
            ConfigJson = "{\"body\":\"{{ERROR.error_message}}\"}", IsValidated = true
        };
        var handler = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(), RefId = "handler", Type = "condition", Subtype = "handle-errors",
            ConfigJson = "{\"fallbackAction\":\"handle\"}", IsValidated = true,
            ErrorChildren = new() { new() {
                PublicId = Guid.NewGuid(), RefId = "nested_condition", Type = "condition", Subtype = "condition",
                IsValidated = true, Children = new() { Mapping("nested_then") }, ElseChildren = new() { Mapping("nested_else") }
            } },
            SuccessChildren = new() { Mapping("success") }
        };
        var result = await _validator.ValidateAsync(new SavePipelineStepsCommand(
            Guid.NewGuid(), new() { CreateTriggerStep(), handler, Mapping("after") }, Array.Empty<byte>()));
        var scopeErrors = System.Linq.Enumerable.Where(result.Errors, e => e.ErrorMessage.Contains("outside of an On error branch"));
        scopeErrors.Should().HaveCount(2);
        scopeErrors.Should().NotContain(e => e.ErrorMessage.Contains("nested_"));
    }

    private static SavePipelineStepDto CreateTriggerStep(string refId = "ref_1") => new()
    {
        PublicId = Guid.NewGuid(),
        RefId = refId,
        Type = "trigger",
        Subtype = "record-added",
        IsValidated = true
    };

    private static SavePipelineStepDto CreateActionStep(string refId = "ref_2") => new()
    {
        PublicId = Guid.NewGuid(),
        RefId = refId,
        Type = "action",
        Subtype = "create-record",
        IsValidated = true
    };

    private static SavePipelineStepDto CreateStopStep(string refId = "ref_3") => new()
    {
        PublicId = Guid.NewGuid(),
        RefId = refId,
        Type = "action",
        Subtype = "stop",
        ConfigJson = "{\"reason\":\"Limit reached\"}",
        IsValidated = true
    };

    private static SavePipelineStepDto CreateLoopStep(string refId = "ref_4", string loopOverStepId = "ref_query") => new()
    {
        PublicId = Guid.NewGuid(),
        RefId = refId,
        Type = "loop",
        Subtype = "for-each",
        ConfigJson = $"{{\"loopOverStepId\":\"{loopOverStepId}\"}}",
        Children = new List<SavePipelineStepDto>(),
        IsValidated = true
    };

    private static SavePipelineStepDto CreateQueryStep(string refId = "ref_query") => new()
    {
        PublicId = Guid.NewGuid(),
        RefId = refId,
        Type = "query",
        Subtype = "search-records",
        ConfigJson = $"{{\"tableId\":\"{Guid.NewGuid()}\",\"maxResults\":15}}",
        IsValidated = true
    };

    [Fact]
    public async Task Validate_WellFormedPipeline_IsValid()
    {
        // Arrange
        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { CreateTriggerStep(), CreateActionStep() },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_SearchRecordsAtStart_IsValid()
    {
        // Arrange
        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { CreateQueryStep(), CreateActionStep() },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_PrepareBulkUpsertAtStart_IsValid()
    {
        var prepare = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_prepare",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = "{\"tableId\":\"" + Guid.NewGuid() + "\",\"fields\":[\"fid_6\"],\"mergeField\":\"fid_3\"}",
            IsValidated = true
        };
        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { prepare }, Array.Empty<byte>());

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("send-email")]
    [InlineData("send-email-outlook")]
    public async Task Validate_SendEmailAtStart_IsValid(string subtype)
    {
        var email = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_email",
            Type = "email",
            Subtype = subtype,
            ConfigJson = "{\"toAddresses\":\"a@b.com\",\"subject\":\"Hi\",\"body\":\"Hello\"}",
            IsValidated = true
        };
        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { email }, Array.Empty<byte>());

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains("A pipeline must begin with"));
    }

    [Fact]
    public async Task Validate_InvalidFirstStepType_ReturnsError()
    {
        // Arrange
        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { CreateActionStep() },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("A pipeline must begin with"));
    }

    [Fact]
    public async Task Validate_MultipleTriggers_ReturnsError()
    {
        // Arrange
        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { CreateTriggerStep("ref_1"), CreateTriggerStep("ref_2") },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Multiple triggers are forbidden"));
    }

    [Fact]
    public async Task Validate_NestedTrigger_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var query = CreateQueryStep("ref_query");
        var loop = CreateLoopStep("ref_loop", "ref_query");
        loop.Children = new List<SavePipelineStepDto> { CreateTriggerStep("ref_nested_trigger") };
        
        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, query, loop },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot be nested"));
    }

    [Fact]
    public async Task Validate_StopStepAtRoot_ReturnsError()
    {
        // Arrange
        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { CreateTriggerStep(), CreateStopStep() },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("must be nested inside a Condition or Loop"));
    }

    [Fact]
    public async Task Validate_StopStepNested_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var query = CreateQueryStep("ref_query");
        var loop = CreateLoopStep("ref_loop", "ref_query");
        loop.Children = new List<SavePipelineStepDto> { CreateStopStep("ref_stop") };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, query, loop },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_LoopIteratingOverNonCollectionStep_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var action = CreateActionStep("ref_action");
        var loop = CreateLoopStep("ref_loop", "ref_action"); // iterating over an action (not collection)

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, action, loop },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot iterate over step"));
    }

    [Fact]
    public async Task Validate_LoopIteratingOverValidCollectionStep_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var query = CreateQueryStep("ref_query");
        var loop = CreateLoopStep("ref_loop", "ref_query"); // iterating over query (valid collection)

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, query, loop },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_LoopIteratingOverMakeRequest_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var request = CreateActionStep("ref_request");
        request.Subtype = "make-request";
        request.ConfigJson = $"{{\"requestMode\":\"quickbase\",\"connectionPublicId\":\"{Guid.NewGuid()}\",\"url\":\"/api/items\",\"method\":\"GET\"}}";
        var loop = CreateLoopStep("ref_loop", "ref_request");

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, request, loop },
            Array.Empty<byte>());

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_PrecedingVariableReference_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var action1 = CreateActionStep("ref_action1");
        var action2 = CreateActionStep("ref_action2");
        action2.ConfigJson = "{\"fieldMappings\": [{\"field\": \"fid_101\", \"value\": \"{{steps.ref_action1.CreatedRecordPublicId}}\"}]}";

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, action1, action2 },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ForwardVariableReference_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var action1 = CreateActionStep("ref_action1");
        action1.ConfigJson = "{\"fieldMappings\": [{\"field\": \"fid_101\", \"value\": \"{{steps.ref_action2.CreatedRecordPublicId}}\"}]}"; // forward ref to action2
        var action2 = CreateActionStep("ref_action2");

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, action1, action2 },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("does not exist or is not preceding"));
    }

    [Fact]
    public async Task Validate_OutsideLoopVariableReference_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var query = CreateQueryStep("ref_query");
        var loop = CreateLoopStep("ref_loop", "ref_query");
        var actionInLoop = CreateActionStep("ref_action_in_loop");
        loop.Children.Add(actionInLoop);

        var actionAfterLoop = CreateActionStep("ref_action_after");
        actionAfterLoop.ConfigJson = "{\"fieldMappings\": [{\"field\": \"fid_101\", \"value\": \"{{steps.ref_loop.item.fid_200}}\"}]}"; // referencing loop variable outside

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, query, loop, actionAfterLoop },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot reference loop step"));
    }

    [Fact]
    public async Task Validate_VisualLabelInMappings_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var action = CreateActionStep("ref_action");
        action.ConfigJson = "{\"fieldMappings\": [{\"field\": \"Client Status\", \"value\": \"Active\"}]}"; // visual name "Client Status" instead of "fid_X"

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, action },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("contains invalid visual label reference"));
    }

    [Fact]
    public async Task Validate_GuidBasedFidInMappings_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var action = CreateActionStep("ref_action");
        action.ConfigJson = "{\"fieldMappings\": [{\"field\": \"fid_372e0f07-5d92-f111-bbf5-002324be71d7\", \"value\": \"Active\"}]}"; // Guid-based FID

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, action },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_CsvImportWithValidFidMapping_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var csvStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_csv",
            Type = "action",
            Subtype = "import-with-csv",
            ConfigJson = "{\"columnMappings\": {\"ColumnA\": \"fid_101\", \"ColumnB\": \"fid_372e0f07-5d92-f111-bbf5-002324be71d7\"}}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, csvStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_CsvImportWithDynamicTokenKeyAndFidValue_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var csvStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_csv",
            Type = "action",
            Subtype = "import-with-csv",
            ConfigJson = "{\"columnMappings\": {\"{{ref_1.record_id#}}\": \"fid_101\"}}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, csvStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_CsvImportWithVisualLabelValue_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var csvStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_csv",
            Type = "action",
            Subtype = "import-with-csv",
            ConfigJson = "{\"columnMappings\": {\"{{ref_1.record_id#}}\": \"Client Status\"}}", // Visual label value
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, csvStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("contains invalid visual label value reference"));
    }

    [Fact]
    public async Task Validate_CsvImportWithInvalidDynamicTokenKeyScope_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_1");
        var csvStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_csv",
            Type = "action",
            Subtype = "import-with-csv",
            ConfigJson = "{\"columnMappings\": {\"{{ref_nonexistent.record_id#}}\": \"fid_101\"}}", // ref_nonexistent does not exist
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, csvStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("does not exist or is not preceding"));
    }

    [Fact]
    public async Task Validate_ContainerPrefixRule_SiblingIsolation_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var query = CreateQueryStep("ref_query");

        var loop1 = CreateLoopStep("ref_loop1", "ref_query");
        var actionInLoop1 = CreateActionStep("ref_action_in_loop1");
        loop1.Children.Add(actionInLoop1);

        var loop2 = CreateLoopStep("ref_loop2", "ref_query");
        var actionInLoop2 = CreateActionStep("ref_action_in_loop2");
        // Referencing step inside loop1 from loop2 (sibling isolation violation)
        actionInLoop2.ConfigJson = "{\"fieldMappings\": [{\"field\": \"fid_101\", \"value\": \"{{steps.ref_action_in_loop1.CreatedRecordPublicId}}\"}]}";
        loop2.Children.Add(actionInLoop2);

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, query, loop1, loop2 },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("inaccessible container"));
    }

    [Fact]
    public async Task Validate_ContainerPrefixRule_NestedAccess_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var query = CreateQueryStep("ref_query");

        var loop = CreateLoopStep("ref_loop", "ref_query");
        var actionInLoop = CreateActionStep("ref_action_in_loop");
        loop.Children.Add(actionInLoop);

        var nestedLoop = CreateLoopStep("ref_nested_loop", "ref_query");
        var actionInNested = CreateActionStep("ref_action_in_nested");
        // Nested referencing step in outer loop (valid prefix)
        actionInNested.ConfigJson = "{\"fieldMappings\": [{\"field\": \"fid_101\", \"value\": \"{{steps.ref_action_in_loop.CreatedRecordPublicId}}\"}]}";
        nestedLoop.Children.Add(actionInNested);

        loop.Children.Add(nestedLoop);

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, query, loop },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_UpdateRecordWithInvalidTargetRecordId_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var query = CreateQueryStep("ref_query");
        var updateAction = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_update",
            Type = "action",
            Subtype = "update-record",
            ConfigJson = "{\"targetRecordId\": \"ref_query\"}", // Query step is not single-record
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, query, updateAction },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("does not return a single record"));
    }

    [Fact]
    public async Task Validate_UpdateRecordWithValidTargetRecordId_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger"); // trigger is record-added (valid single record)
        var createAction = CreateActionStep("ref_create"); // create-record action (valid single record)
        var updateAction = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_update",
            Type = "action",
            Subtype = "update-record",
            ConfigJson = "{\"targetRecordId\": \"ref_create\", \"fieldMappings\": [{\"field\": \"fid_101\", \"value\": \"Updated\"}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, createAction, updateAction },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SearchRecordsMissingTableId_ReturnsError()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var searchQuery = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_search",
            Type = "query",
            Subtype = "search-records",
            ConfigJson = "{\"tableId\": \"\", \"maxResults\": 15}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, searchQuery },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("requires a valid table selection"));
    }

    [Fact]
    public async Task Validate_SearchRecordsWithValidTableId_IsValid()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var searchQuery = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_search",
            Type = "query",
            Subtype = "search-records",
            ConfigJson = $"{{\"tableId\": \"{Guid.NewGuid()}\", \"maxResults\": 15}}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, searchQuery },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DraftSearchRecordsWithEmptyTable_IsValidatedFalse_Passes()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var searchQuery = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_search",
            Type = "query",
            Subtype = "search-records",
            ConfigJson = "{\"tableId\": \"\", \"maxResults\": 15}",
            IsValidated = false
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, searchQuery },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DraftLoopWithEmptyTarget_IsValidatedFalse_Passes()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var loopStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_loop",
            Type = "loop",
            Subtype = "for-each",
            ConfigJson = "{\"loopOverStepId\": \"\"}",
            IsValidated = false
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, loopStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DraftUpdateRecordWithEmptyTarget_IsValidatedFalse_Passes()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var updateStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_update",
            Type = "action",
            Subtype = "update-record",
            ConfigJson = "{\"targetRecordId\": \"\"}",
            IsValidated = false
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, updateStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DraftDeleteRecordWithEmptyTarget_IsValidatedFalse_Passes()
    {
        // Arrange
        var trigger = CreateTriggerStep("ref_trigger");
        var deleteStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_delete",
            Type = "action",
            Subtype = "delete-record",
            ConfigJson = "{\"targetRecordId\": \"\"}",
            IsValidated = false
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { trigger, deleteStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_StructuralInvalidHierarchy_IsValidatedFalse_Fails()
    {
        // Arrange
        // Stop step at root is structurally invalid, should fail even if IsValidated = false
        var stopStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_stop",
            Type = "action",
            Subtype = "stop",
            ConfigJson = "{\"reason\": \"Stop\"}",
            IsValidated = false
        };

        var command = new SavePipelineStepsCommand(
            Guid.NewGuid(),
            new List<SavePipelineStepDto> { CreateTriggerStep(), stopStep },
            Array.Empty<byte>()
        );

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("must be nested inside a Condition or Loop"));
    }

    [Fact]
    public async Task Validate_ConditionWithIntegerOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":3}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithDecimalOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":3.5}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithNegativeNumberOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":-2}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithBooleanTrueOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":true}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithBooleanFalseOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":false}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithTextOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":\"Ronak\"}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithDateOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":\"2026-09-07T12:00:00.000Z\"}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithDynamicTokenOperand_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":\"{{steps.ref_1.fid_7}}\"}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithNullRightOperandForUnaryOperator_IsValid()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"is_blank\",\"right\":null}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConditionWithObjectOperand_FailsValidationCleanly()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":{\"value\":3}}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("invalid complex JSON structure"));
    }

    [Fact]
    public async Task Validate_ConditionWithArrayOperand_FailsValidationCleanly()
    {
        var trigger = CreateTriggerStep("ref_1");
        var condition = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_cond",
            Type = "condition",
            Subtype = "condition",
            ConfigJson = "{\"ruleGroups\":[{\"logicalOp\":\"OR\",\"rules\":[{\"left\":\"{{steps.ref_1.fid_6}}\",\"op\":\"equals\",\"right\":[3]}]}]}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { trigger, condition }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("invalid complex JSON structure"));
    }

    [Fact]
    public async Task Validate_RootHandleErrorsInNonTriggerPipeline_ReturnsValid()
    {
        var handleErrors = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_he",
            Type = "control",
            Subtype = "handle-errors",
            ConfigJson = "{\"fallbackAction\":\"handle\"}",
            Children = new List<SavePipelineStepDto> { CreateActionStep("ref_action") },
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { handleErrors }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_RootHandleErrorsWithNestedTrigger_ReturnsInvalid()
    {
        var nestedTrigger = CreateTriggerStep("ref_trig");
        var handleErrors = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "ref_he",
            Type = "control",
            Subtype = "handle-errors",
            ConfigJson = "{\"fallbackAction\":\"handle\"}",
            Children = new List<SavePipelineStepDto> { nestedTrigger },
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { handleErrors }, Array.Empty<byte>());
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot be nested inside container steps"));
    }

    [Fact]
    public async Task Validate_ValidatedHttpRequestWithoutSavedConnection_ReturnsInvalid()
    {
        var request = CreateActionStep("ref_request");
        request.Subtype = "make-request";
        request.ConfigJson = "{\"requestMode\":\"http\",\"baseUrl\":\"https://example.com\",\"method\":\"GET\",\"expectedPayloadType\":\"JSON\"}";
        request.IsValidated = true;
        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { CreateTriggerStep("ref_trigger"), request }, Array.Empty<byte>());

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Connect the HTTP account"));
    }

    [Fact]
    public async Task Validate_ValidatedHttpRequestAtStartWithSavedConnection_ReturnsValid()
    {
        var request = CreateActionStep("ref_request");
        request.Subtype = "make-request";
        request.ConfigJson = $"{{\"requestMode\":\"http\",\"httpConnectionId\":\"{Guid.NewGuid()}\",\"baseUrl\":\"https://example.com\",\"method\":\"GET\",\"expectedPayloadType\":\"JSON\"}}";
        request.IsValidated = true;
        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { request }, Array.Empty<byte>());

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ValidatedPowerBaseRequestAtStart_ReturnsValid()
    {
        var request = CreateActionStep("ref_request");
        request.Subtype = "make-request";
        request.ConfigJson = $"{{\"requestMode\":\"quickbase\",\"connectionPublicId\":\"{Guid.NewGuid()}\",\"url\":\"/api/records\",\"method\":\"GET\"}}";
        request.IsValidated = true;
        var command = new SavePipelineStepsCommand(Guid.NewGuid(), new List<SavePipelineStepDto> { request }, Array.Empty<byte>());

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
