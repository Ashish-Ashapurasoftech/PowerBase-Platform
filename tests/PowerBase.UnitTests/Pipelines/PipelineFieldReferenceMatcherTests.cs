using FluentAssertions;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineFieldReferenceMatcherTests
{
    private static readonly Guid Sessions = Guid.NewGuid();
    private static readonly Guid Clients = Guid.NewGuid();

    private static PipelineStepSnapshot Step(string refId, string type, string config) =>
        new(refId, refId, type, "", config);

    [Fact]
    public void CreateRecord_OnOtherTable_UsingSameFidNumber_IsNotAReference()
    {
        var create = Step("ref_1", "action", $$$"""{"tableId":"{{{Clients}}}","fieldMappings":[{"field":"fid_10","value":"x"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(create, [create], 10, Sessions).Should().BeFalse();
    }

    [Fact]
    public void CreateRecord_OnSameTable_UsingFid_IsAReference()
    {
        var create = Step("ref_1", "action", $$$"""{"tableId":"{{{Sessions}}}","fieldMappings":[{"field":"fid_10","value":"x"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(create, [create], 10, Sessions).Should().BeTrue();
    }

    [Fact]
    public void SearchFilter_OnSameTable_IsAReference()
    {
        var search = Step("ref_1", "action", $$$"""{"tableId":"{{{Sessions}}}","filters":[{"field":"fid_10","operator":"equals","value":"1"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(search, [search], 10, Sessions).Should().BeTrue();
    }

    [Fact]
    public void Token_ReadingFromStepOnOtherTable_IsNotAReference()
    {
        var search = Step("ref_1", "action", $$$"""{"tableId":"{{{Clients}}}"}""");
        var create = Step("ref_2", "action", $$$"""{"tableId":"{{{Sessions}}}","fieldMappings":[{"field":"fid_3","value":"{{steps.ref_1.fid_10}}"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(create, [search, create], 10, Sessions).Should().BeFalse();
    }

    [Fact]
    public void Token_ReadingFromStepOnThisTable_IsAReference()
    {
        var search = Step("ref_1", "action", $$$"""{"tableId":"{{{Sessions}}}"}""");
        var create = Step("ref_2", "action", $$$"""{"tableId":"{{{Clients}}}","fieldMappings":[{"field":"fid_3","value":"{{steps.ref_1.fid_10}}"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(create, [search, create], 10, Sessions).Should().BeTrue();
    }

    [Fact]
    public void TriggerToken_UsesTriggerTable()
    {
        var trigger = Step("ref_0", "trigger", $$$"""{"tablePublicId":"{{{Sessions}}}"}""");
        var create = Step("ref_2", "action", $$$"""{"tableId":"{{{Clients}}}","fieldMappings":[{"field":"fid_3","value":"{{trigger.fid_10}}"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(create, [trigger, create], 10, Sessions).Should().BeTrue();
        PipelineFieldReferenceMatcher.ReferencesField(create, [trigger, create], 10, Clients).Should().BeFalse();
    }

    [Fact]
    public void Fid100_IsNotMistakenForFid10()
    {
        var create = Step("ref_1", "action", $$$"""{"tableId":"{{{Sessions}}}","fieldMappings":[{"field":"fid_100","value":"x"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(create, [create], 10, Sessions).Should().BeFalse();
    }

    [Fact]
    public void TokenFromMissingStep_IsNotAReference()
    {
        var create = Step("ref_2", "action", $$$"""{"tableId":"{{{Clients}}}","fieldMappings":[{"field":"fid_3","value":"{{steps.missing.fid_10}}"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(create, [create], 10, Sessions).Should().BeFalse();
    }

    private const string CopyConfig = """{"sourceTable":"{{{S}}}","sourceFields":["fid_6"],"destinationTable":"{{{D}}}","destinationFields":["fid_8"],"mergeField":"fid_3","advancedQuery":"{7.EX.'x'}"}""";

    private static PipelineStepSnapshot Copy(Guid source, Guid destination) =>
        Step("ref_1", "action", CopyConfig.Replace("{{{S}}}", source.ToString()).Replace("{{{D}}}", destination.ToString()));

    [Fact]
    public void CopyRecords_SourceField_BelongsToSourceTable()
    {
        var copy = Copy(Sessions, Clients);

        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], 6, Sessions).Should().BeTrue();
        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], 6, Clients).Should().BeFalse();
    }

    [Fact]
    public void CopyRecords_DestinationFieldAndMergeField_BelongToDestinationTable()
    {
        var copy = Copy(Sessions, Clients);

        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], 8, Clients).Should().BeTrue();
        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], 3, Clients).Should().BeTrue();
        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], 8, Sessions).Should().BeFalse();
    }

    [Fact]
    public void CopyRecords_AdvancedQueryFieldNumber_BelongsToSourceTable()
    {
        var copy = Copy(Sessions, Clients);

        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], 7, Sessions).Should().BeTrue();
        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], 7, Clients).Should().BeFalse();
    }

    private static readonly AppField NameField = new() { Fid = 6, Name = "C_name", Label = "Customer Name", PublicId = Guid.NewGuid() };

    [Fact]
    public void JsonKey_FidInRowValues_IsAReference()
    {
        var step = Step("ref_1", "action", $$$"""{"tableId":"{{{Sessions}}}","rowValues":{"fid_6":"x"}}""");

        PipelineFieldReferenceMatcher.ReferencesField(step, [step], NameField, Sessions).Should().BeTrue();
        PipelineFieldReferenceMatcher.ReferencesField(step, [step], NameField, Clients).Should().BeFalse();
    }

    [Fact]
    public void FilterField_ByFieldName_IsAReference()
    {
        var step = Step("ref_1", "trigger", $$$"""{"tableId":"{{{Sessions}}}","filters":[{"field":"C_name","operator":"is","value":"x"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(step, [step], NameField, Sessions).Should().BeTrue();
    }

    [Fact]
    public void FilterValueEqualToFieldName_IsNotAReference()
    {
        var step = Step("ref_1", "action", $$$"""{"tableId":"{{{Sessions}}}","fieldMappings":[{"field":"fid_8","value":"C_name"}]}""");

        PipelineFieldReferenceMatcher.ReferencesField(step, [step], NameField, Sessions).Should().BeFalse();
    }

    [Fact]
    public void Token_ByFieldLabel_ReadingFromStepOnThisTable_IsAReference()
    {
        var search = Step("ref_1", "action", $$$"""{"tableId":"{{{Sessions}}}"}""");
        var email = Step("ref_2", "action", """{"body":"Hello {{steps.ref_1.Customer Name}}"}""");

        PipelineFieldReferenceMatcher.ReferencesField(email, [search, email], NameField, Sessions).Should().BeTrue();
        PipelineFieldReferenceMatcher.ReferencesField(email, [search, email], NameField, Clients).Should().BeFalse();
    }

    [Fact]
    public void FieldPublicId_InSourceFields_IsAReference()
    {
        var copy = Step("ref_1", "action", $$$"""{"sourceTable":"{{{Sessions}}}","sourceFields":["{{{NameField.PublicId}}}"],"destinationTable":"{{{Clients}}}","destinationFields":["fid_8"]}""");

        PipelineFieldReferenceMatcher.ReferencesField(copy, [copy], NameField, Sessions).Should().BeTrue();
    }
}
