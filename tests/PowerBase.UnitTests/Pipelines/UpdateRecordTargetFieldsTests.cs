using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>As in Quickbase, an Update Record step can only set the fields the step it acts on outputs.</summary>
public class UpdateRecordTargetFieldsTests
{
    private const string Table = "11111111-1111-1111-1111-111111111111";
    private readonly SavePipelineStepsCommandValidator _validator = new();

    private static SavePipelineStepDto Step(string refId, string type, string subtype, object config, List<SavePipelineStepDto>? children = null) => new()
    {
        PublicId = Guid.NewGuid(), RefId = refId, Label = refId, Type = type, Subtype = subtype, IsValidated = true,
        ConfigJson = JsonSerializer.Serialize(config), Children = children
    };

    private static SavePipelineStepDto Update(string target, params string[] fids) => Step("ref_up", "action", "update-record", new
    {
        tableId = Table, targetRecordId = target,
        fieldMappings = fids.Select(f => new { field = f, value = "x" }).ToArray()
    });

    private static SavePipelineStepDto LookUp(params string[] fields) => Step("ref_lk", "query", "look-up-record", new
    {
        tablePublicId = Table, recordIdValue = "1", subsequentFields = fields
    });

    private async Task<List<string>> Errors(params SavePipelineStepDto[] steps)
    {
        var result = await _validator.ValidateAsync(new SavePipelineStepsCommand(Guid.NewGuid(), steps.ToList(), Array.Empty<byte>()));
        return result.Errors.Select(e => e.ErrorMessage).Where(m => m.Contains("does not output")).ToList();
    }

    [Fact]
    public async Task LookUp_FieldInItsFields_CanBeUpdated()
    {
        (await Errors(LookUp("fid_6", "fid_7"), Update("{{steps.ref_lk.RecordPublicId}}", "fid_7"))).Should().BeEmpty();
    }

    [Fact]
    public async Task LookUp_FieldNotInItsFields_CannotBeSaved()
    {
        var errors = await Errors(LookUp("fid_6"), Update("{{steps.ref_lk.RecordPublicId}}", "fid_6", "fid_12"));

        errors.Should().ContainSingle().Which.Should().Contain("'fid_12'").And.Contain("step 'ref_lk'").And.NotContain("fid_6'");
    }

    [Fact]
    public async Task LookUp_WithNoFields_LetsNothingBeUpdated_AsQuickbase()
    {
        (await Errors(LookUp(), Update("{{steps.ref_lk.RecordPublicId}}", "fid_6"))).Should().ContainSingle();
    }

    [Fact]
    public async Task AddressPart_CountsAsItsField()
    {
        (await Errors(LookUp("fid_20"), Update("{{steps.ref_lk.RecordPublicId}}", "fid_20.city"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Trigger_OnlyItsFieldsForSubsequentSteps()
    {
        var trigger = Step("ref_t", "trigger", "new-event", new { tableLabel = Table, subsequentFields = new[] { "fid_6" } });

        (await Errors(trigger, Update("{{trigger.RecordPublicId}}", "fid_6"))).Should().BeEmpty();
        (await Errors(trigger, Update("{{trigger.RecordPublicId}}", "fid_8"))).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateRecord_OnlyTheFieldsItSets()
    {
        var create = Step("ref_cr", "action", "create-record", new { tableId = Table, fieldMappings = new[] { new { field = "fid_6", value = "a" } } });

        (await Errors(create, Update("{{steps.ref_cr.RecordPublicId}}", "fid_6"))).Should().BeEmpty();
        (await Errors(create, Update("{{steps.ref_cr.RecordPublicId}}", "fid_9"))).Should().ContainSingle();
    }

    [Fact]
    public async Task SearchThroughLoop_OnlyTheSearchsFields()
    {
        var search = Step("ref_s", "query", "search-records", new { tableId = Table, subsequentFields = new[] { "fid_6", "fid_12" } });
        SavePipelineStepDto Loop(SavePipelineStepDto update) => Step("ref_loop", "loop", "for-each", new { loopOverStepId = "ref_s" }, [update]);

        (await Errors(search, Loop(Update("{{steps.ref_loop.item.RecordPublicId}}", "fid_12")))).Should().BeEmpty();
        (await Errors(search, Loop(Update("{{steps.ref_loop.item.RecordPublicId}}", "fid_7")))).Should().ContainSingle();
    }

    [Fact]
    public async Task UnvalidatedDraft_IsNotHeldToTheRule()
    {
        var update = Update("{{steps.ref_lk.RecordPublicId}}", "fid_12");
        update.IsValidated = false;

        (await Errors(LookUp("fid_6"), update)).Should().BeEmpty();
    }

    [Fact]
    public async Task ByRecordId_HasNoTargetStep_AndIsNotLimited()
    {
        var update = Step("ref_up", "action", "update-record", new
        {
            tableId = Table, recordSource = "id", targetRecordId = "5",
            fieldMappings = new[] { new { field = "fid_12", value = "x" } }
        });

        (await Errors(LookUp("fid_6"), update)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAfterUpdate_CanSetWhatTheFirstUpdatesTargetOutputs_AsQuickbase()
    {
        var first = Update("{{steps.ref_lk.RecordPublicId}}", "fid_6");
        var second = Step("ref_up2", "action", "update-record", new
        {
            tableId = Table, targetRecordId = "{{steps.ref_up.RecordPublicId}}",
            fieldMappings = new[] { new { field = "fid_7", value = "x" } }
        });

        (await Errors(LookUp("fid_6", "fid_7"), first, second)).Should().BeEmpty();

        var notOutput = Step("ref_up2", "action", "update-record", new
        {
            tableId = Table, targetRecordId = "{{steps.ref_up.RecordPublicId}}",
            fieldMappings = new[] { new { field = "fid_9", value = "x" } }
        });
        (await Errors(LookUp("fid_6", "fid_7"), first, notOutput)).Should().ContainSingle().Which.Should().Contain("'fid_9'");
    }

    [Fact]
    public async Task CreateRecord_FieldChosenWithoutAValue_CanBeUpdated_AsQuickbase()
    {
        var create = Step("ref_cr", "action", "create-record", new
        {
            tableId = Table, fieldMappings = new[] { new { field = "fid_6", value = "a" } }, outputFields = new[] { "fid_6", "fid_175" }
        });

        (await Errors(create, Update("{{steps.ref_cr.RecordPublicId}}", "fid_175"))).Should().BeEmpty();
        (await Errors(create, Update("{{steps.ref_cr.RecordPublicId}}", "fid_9"))).Should().ContainSingle();
    }
}
