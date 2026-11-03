using System.Text.Json.Nodes;
using FluentAssertions;
using PowerBase.Application.Import.Services;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>
/// An imported step is saved the way the PowerFlows editor leaves it once opened (values taken from what the editor saved for
/// the real QBL samples), so opening an imported PowerFlow saves nothing new — and never turns an active one off.
/// </summary>
public class PblPipelineEditorParityTests
{
    private static SavePipelineStepDto Step(string refId, string type, string subtype, object? config = null, List<SavePipelineStepDto>? children = null,
        List<SavePipelineStepDto>? elseChildren = null) =>
        new() { RefId = refId, Label = refId, Type = type, Subtype = subtype, ConfigJson = System.Text.Json.JsonSerializer.Serialize(config ?? new { }),
                Children = children, ElseChildren = elseChildren };

    private static JsonObject Config(SavePipelineStepDto step) => (JsonObject)JsonNode.Parse(step.ConfigJson!)!;

    private static JsonObject Run(SavePipelineStepDto step)
    {
        PblPipelineEditorParity.Apply([step]);
        return Config(step);
    }

    [Fact]
    public void Apply_HttpMakeRequest_GetsTheEditorsHttpDefaults_AndKeepsWhatWasImported()
    {
        var config = Run(Step("ref_aa", "action", "make-request", new { requestMode = "http", method = "POST", path = "/post", body = "{}", accountName = "ysl-dev" }));

        config["method"]!.GetValue<string>().Should().Be("POST");
        config["path"]!.GetValue<string>().Should().Be("/post");
        config["accountName"]!.GetValue<string>().Should().Be("ysl-dev");
        config["baseUrl"]!.GetValue<string>().Should().Be("");
        config["jwtAlg"]!.GetValue<string>().Should().Be("HS256 (symmetric)");
        config["jwtExp"]!.GetValue<string>().Should().Be("5m");
        config["jwtUseIat"]!.GetValue<bool>().Should().BeFalse();
        config["apiKeyPlacement"]!.GetValue<string>().Should().Be("Header");
        config["oauthGrantType"]!.GetValue<string>().Should().Be("Authorization code");
        config["oauthClientAuth"]!.GetValue<string>().Should().Be("Send client credentials as basic auth header");
        config["stepHeaders"]!.ToJsonString().Should().Be("""[{"key":"","value":""}]""");
        config["queryParams"]!.ToJsonString().Should().Be("""[{"key":"","value":""}]""");
        config["connectionHeaders"]!.ToJsonString().Should().Be("""[{"key":"","value":""}]""");
        config["schemaSample"]!.GetValue<string>().Should().Be("");
        config["exemptErrorStatuses"]!.GetValue<string>().Should().Be("");
    }

    [Fact]
    public void Apply_QuickbaseModeMakeRequest_IsLeftAsImported()
    {
        var step = Step("ref_aa", "action", "make-request", new { requestMode = "quickbase", url = "https://api.quickbase.com/v1/records/query", method = "GET" });
        var before = step.ConfigJson;

        PblPipelineEditorParity.Apply([step]);

        JsonNode.DeepEquals(JsonNode.Parse(step.ConfigJson!), JsonNode.Parse(before!)).Should().BeTrue();
    }

    [Fact]
    public void Apply_IncomingRequest_GetsTheCardsOutputFieldsAndBlankFilter()
    {
        var config = Run(Step("ref_aa", "trigger", "webhook", new { authType = "no-auth", methodType = "POST", jwtAlgorithm = "RS256", publicKey = "", proxyViaOnPremisesAgent = true }));

        config["methodType"]!.GetValue<string>().Should().Be("POST");
        config["proxyViaOnPremisesAgent"]!.GetValue<bool>().Should().BeTrue();
        config["tableLabel"]!.GetValue<string>().Should().Be("Request");
        config["responseFields"]!.AsArray().Select(f => f!["id"]!.GetValue<string>())
            .Should().Equal("method", "headers", "body", "json", "origin_ip", "jwt_payload", "url_params", "body_params");
        config["isSimpleFilter"]!.GetValue<bool>().Should().BeTrue();
        config["conditions"]!.ToJsonString().Should().Be("""[{"rules":[{"field":"","operator":"","value":""}]}]""");
    }

    [Theory]
    [InlineData("10m", 600)]
    [InlineData("5s", 5)]
    [InlineData("1 s", 1)]
    public void Apply_Pause_KeepsTheDurationTextAndAddsItsSeconds(string text, long seconds)
    {
        var config = Run(Step("ref_aa", "action", "pause", new { durationText = text }));

        config["durationText"]!.GetValue<string>().Should().Be(text);
        config["duration"]!.GetValue<long>().Should().Be(seconds);
        config["unit"]!.GetValue<string>().Should().Be("seconds");
    }

    [Fact]
    public void Apply_PauseOverThirtyMinutes_IsLeftForTheUserToFix()
    {
        var config = Run(Step("ref_aa", "action", "pause", new { durationText = "2h" }));

        config.ContainsKey("duration").Should().BeFalse();
    }

    [Fact]
    public void Apply_Condition_AddsTheFlatRulesNextToTheGroups()
    {
        var config = Run(Step("ref_2", "condition", "if-else", new
        {
            ruleGroups = new[] { new { logicalOp = "AND", rules = new object[] { new { type = "rule", left = "{{steps.ref_aa.fid_7}}", op = "starts-with", right = "Ronak" } } } },
        }));

        config["ruleGroups"]![0]!["rules"]![0]!["type"]!.GetValue<string>().Should().Be("rule");
        config["rules"]!.ToJsonString().Should().Be("""[{"left":"{{steps.ref_aa.fid_7}}","op":"starts-with","right":"Ronak"}]""");
    }

    [Fact]
    public void Apply_ConditionWithNoRules_GetsTheEditorsBlankRule()
    {
        var config = Run(Step("ref_1", "condition", "if-else", new { leftOperand = "", @operator = "equals", rightOperand = "", ruleGroups = Array.Empty<object>() }));

        config["ruleGroups"]!.ToJsonString().Should().Be("""[{"logicalOp":"OR","rules":[{"left":"","op":"equals","right":""}]}]""");
        config["rules"]!.ToJsonString().Should().Be("""[{"left":"","op":"equals","right":""}]""");
    }

    [Fact]
    public void Apply_SearchFilters_FlatListHoldsOnlyFieldOperatorAndValue()
    {
        var rule = new { type = "rule", field = "fid_46", @operator = "is_true", value = "", logicalOp = "AND" };
        var config = Run(Step("ref_b", "query", "search-records", new { isSimpleFilter = true, filters = new[] { rule }, filterGroups = new[] { new { logicalOp = "AND", rules = new[] { rule } } } }));

        config["filters"]!.ToJsonString().Should().Be("""[{"field":"fid_46","operator":"is_true","value":""}]""");
        config["filterGroups"]![0]!["rules"]![0]!["logicalOp"]!.GetValue<string>().Should().Be("AND");
    }

    [Fact]
    public void Apply_RecordSteps_LinkTheFirstEarlierRecordStep_LikeTheEditor()
    {
        var trigger = Step("ref_aa", "trigger", "new-event");
        var create = Step("ref_ab", "action", "create-record", new { tableId = "t", fieldMappings = Array.Empty<object>() });
        var inLoop = Step("ref_ac", "action", "create-record", new { tableId = "t" });
        var loop = Step("ref_1", "loop", "for-each", new { loopOverStepId = "ref_ab" }, children: [inLoop]);
        var update = Step("ref_ad", "action", "update-record", new { tableId = "t", targetRecordId = "{{steps.ref_ab.RecordPublicId}}", linkRecordId = "ref_ab" });

        PblPipelineEditorParity.Apply([trigger, create, loop, update]);

        Config(create)["linkRecordId"]!.GetValue<string>().Should().Be("ref_aa");
        Config(inLoop)["linkRecordId"]!.GetValue<string>().Should().Be("ref_aa");
        Config(update)["linkRecordId"]!.GetValue<string>().Should().Be("ref_ab", "a link the step already has is kept");
        Config(trigger).ContainsKey("linkRecordId").Should().BeFalse();
    }

    [Fact]
    public void Apply_LinkToAStepNoLongerAbove_IsClearedOrRelinked()
    {
        // The first save linked the trigger; the trigger was then left out and the steps saved again.
        var lookUp = Step("ref_b", "query", "look-up-record", new { linkRecordId = "ref_a" });
        var create = Step("ref_c", "action", "create-record", new { linkRecordId = "ref_a" });

        PblPipelineEditorParity.Apply([lookUp, create]);

        Config(lookUp)["linkRecordId"]!.GetValue<string>().Should().Be("", "nothing above it returns a record");
        Config(create)["linkRecordId"]!.GetValue<string>().Should().Be("ref_b");
    }

    [Fact]
    public void Apply_FirstRecordStepWithNothingAbove_GetsNoLink()
    {
        var config = Run(Step("ref_ab", "action", "create-record", new { tableId = "t" }));

        config.ContainsKey("linkRecordId").Should().BeFalse();
    }

    [Fact]
    public void Apply_UpdateWithNoRecordAndSeveralSources_ClearsItsTableLikeTheCard()
    {
        var a = Step("ref_a", "query", "look-up-record");
        var b = Step("ref_b", "query", "look-up-record");
        var update = Step("ref_c", "action", "update-record", new { tableId = "", targetRecordId = "", fieldMappings = Array.Empty<object>() });

        PblPipelineEditorParity.Apply([a, b, update]);

        var config = Config(update);
        config["tableLabel"]!.GetValue<string>().Should().Be("");
        config["tablePublicId"]!.GetValue<string>().Should().Be("");
        config["targetRecordId"]!.GetValue<string>().Should().Be("");
    }

    [Fact]
    public void Apply_UpdateWithItsRecord_TakesTheTableOfThatRecordsStep()
    {
        var a = Step("ref_a", "query", "look-up-record", new { connection = "c1", tableLabel = "t1", tablePublicId = "t1" });
        var update = Step("ref_c", "action", "update-record", new { tableId = "t1", targetRecordId = "{{steps.ref_a.RecordPublicId}}" });

        PblPipelineEditorParity.Apply([a, update]);

        var config = Config(update);
        config["targetRecordId"]!.GetValue<string>().Should().Be("{{steps.ref_a.RecordPublicId}}");
        config["tableId"]!.GetValue<string>().Should().Be("t1");
        config["tableLabel"]!.GetValue<string>().Should().Be("t1");
        config["tablePublicId"]!.GetValue<string>().Should().Be("t1");
        config["connection"]!.GetValue<string>().Should().Be("c1");
        config["connectionPublicId"]!.GetValue<string>().Should().Be("c1");
    }

    [Fact]
    public void Apply_UpdateOfALookUpWhoseTableIsNotInTheFile_GetsTheEmptyTableTheCardWrites()
    {
        var a = Step("ref_b", "query", "look-up-record", new { connection = "c1", tableId = "" });
        var update = Step("ref_c", "action", "update-record", new { tableId = "", targetRecordId = "{{steps.ref_b.RecordPublicId}}", connection = "c1", connectionPublicId = "c1" });

        PblPipelineEditorParity.Apply([a, update]);

        Config(update)["tableLabel"]!.GetValue<string>().Should().Be("");
        Config(update)["tablePublicId"]!.GetValue<string>().Should().Be("");
    }

    [Fact]
    public void Apply_UpdateOfTheLoopedRecord_TakesTheSearchesTable()
    {
        var search = Step("ref_a", "query", "search-records", new { connection = "c1", tablePublicId = "t9" });
        var update = Step("ref_c", "action", "update-record", new { targetRecordId = "{{steps.ref_1.item.RecordPublicId}}" });
        var loop = Step("ref_1", "loop", "for-each", new { loopOverStepId = "ref_a" }, children: [update]);

        PblPipelineEditorParity.Apply([search, loop]);

        Config(update)["tablePublicId"]!.GetValue<string>().Should().Be("t9");
    }

    [Fact]
    public void Apply_ErrorDetailChipInAMapping_IsSavedAsTheEditorDoes_TextAroundItIsNot()
    {
        var config = Run(Step("ref_ap", "action", "create-record", new
        {
            tableId = "t",
            fieldMappings = new[]
            {
                new { field = "fid_7", value = "{{steps.ERROR.error_message}}" },
                new { field = "fid_6", value = "Failed: {{steps.ERROR.name}}" },
            },
        }));

        config["fieldMappings"]![0]!["value"]!.GetValue<string>().Should().Be("{{ERROR.error_message}}");
        config["fieldMappings"]![1]!["value"]!.GetValue<string>().Should().Be("Failed: {{steps.ERROR.name}}");
    }

    [Fact]
    public void Apply_ErrorDetailInACondition_KeepsTheStepsForm()
    {
        var config = Run(Step("ref_3", "condition", "if-else", new
        {
            ruleGroups = new[] { new { logicalOp = "AND", rules = new object[] { new { left = "{{steps.ERROR.error_message}}", op = "contains", right = "x" } } } },
        }));

        config["ruleGroups"]![0]!["rules"]![0]!["left"]!.GetValue<string>().Should().Be("{{steps.ERROR.error_message}}");
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var steps = new List<SavePipelineStepDto>
        {
            Step("ref_aa", "trigger", "webhook", new { authType = "no-auth" }),
            Step("ref_ab", "action", "make-request", new { requestMode = "http", method = "GET" }),
            Step("ref_ac", "action", "pause", new { durationText = "10m" }),
            Step("ref_1", "condition", "if-else", new { ruleGroups = Array.Empty<object>() }),
        };
        PblPipelineEditorParity.Apply(steps);
        var once = steps.Select(s => s.ConfigJson).ToList();

        PblPipelineEditorParity.Apply(steps);

        steps.Select(s => s.ConfigJson).Should().Equal(once);
    }
}
