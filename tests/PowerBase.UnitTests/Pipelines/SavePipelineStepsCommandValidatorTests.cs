using FluentAssertions;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using Xunit;
using System;
using System.Collections.Generic;

namespace PowerBase.UnitTests.Pipelines;

public class SavePipelineStepsCommandValidatorTests
{
    private readonly SavePipelineStepsCommandValidator _validator = new();

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
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("begin with either a Trigger step or a Search/Query step"));
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
}
