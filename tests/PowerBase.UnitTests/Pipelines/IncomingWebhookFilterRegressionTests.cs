using System.Text.Json;
using PowerBase.Application.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

public class IncomingWebhookFilterRegressionTests
{
    private static bool Matches(string config, string payload)
    {
        var definition = IncomingWebhookConfig.Read(config);
        definition.Validate();
        using var request = JsonDocument.Parse(payload);
        return definition.Matches(request.RootElement, "b");
    }

    [Fact]
    public void EditorGroupsOverrideStaleExecutableConditions()
    {
        const string config = """
            {"conditions":[],"filterGroups":[{"rules":[
              {"field":"body","operator":"is","value":"ready"},
              {"type":"nested","groups":[]}
            ]}]}
            """;
        Assert.False(Matches(config, """{"body":"pending"}"""));
        Assert.True(Matches(config, """{"body":"ready"}"""));
    }

    [Theory]
    [InlineData("ready", "POST", true)]
    [InlineData("ready", "GET", true)]
    [InlineData("pending", "POST", false)]
    [InlineData("ready", "DELETE", false)]
    public void EmptyNestedAlternativesDoNotBypassAndOrConditions(string body, string method, bool expected)
    {
        const string config = """
            {"filterGroups":[{"rules":[
              {"field":"body","operator":"is","value":"ready"},
              {"type":"nested","groups":[{"rules":[]},
                {"rules":[{"field":"method","operator":"is","value":"POST"}]},
                {"rules":[{"field":"method","operator":"is","value":"GET"}]}]}
            ]}]}
            """;
        Assert.Equal(expected, Matches(config, JsonSerializer.Serialize(new { body, method })));
    }

    [Fact]
    public void AdvancedModeOverridesStaleSimpleConditionsAndUsesActualStepReference()
    {
        const string config = """
            {"isSimpleFilter":false,"advancedQuery":"b.json.count > 2","conditions":[],"filterGroups":[]}
            """;
        Assert.False(Matches(config, """{"json":{"count":1}}"""));
        Assert.True(Matches(config, """{"json":{"count":3}}"""));
        Assert.True(Matches("""{"isSimpleFilter":false,"advancedQuery":"","conditions":[{"rules":[{"field":"body","operator":"is","value":"stale"}]}]}""", "{}"));
    }

    [Theory]
    [InlineData("is", "yes", true)]
    [InlineData("is-not", "yes", false)]
    [InlineData("contains", "es", true)]
    [InlineData("not-contains", "es", false)]
    [InlineData("starts-with", "ye", true)]
    [InlineData("not-starts-with", "no", false)]
    [InlineData("ends-with", "es", true)]
    [InlineData("not-ends-with", "es", false)]
    [InlineData("matches-regex", "^y.*s$", true)]
    [InlineData("not-matches-regex", "^y.*s$", false)]
    [InlineData("is-empty", "", false)]
    [InlineData("is-not-empty", "", true)]
    public void HeaderOperatorsUseSelectedHeaderAndAllValuesForNegativeChecks(string op, string value, bool expected)
    {
        var config = JsonSerializer.Serialize(new { conditions = new[] { new { rules = new[] {
            new { field = "headers", path = "x-event", @operator = op, value }
        } } } });
        Assert.Equal(expected, Matches(config, """{"headers":[{"name":"X-Event","value":"yes"},{"name":"X-Event","value":"no"},{"name":"Other","value":"irrelevant"}]}"""));
    }

    [Theory]
    [InlineData("headers.name", "X-Event")]
    [InlineData("headers.value", "yes")]
    public void HeaderChildFieldsAreValidAndExecutable(string field, string value)
    {
        var config = JsonSerializer.Serialize(new { conditions = new[] { new { rules = new[] {
            new { field, @operator = "is", value }
        } } } });
        Assert.True(Matches(config, """{"headers":[{"name":"X-Event","value":"yes"}]}"""));
        Assert.False(Matches(config, """{"headers":[]}"""));
    }

    [Theory]
    [InlineData("items.0.id", "is", "0", true)]
    [InlineData("items.1.id", "is-empty", "", true)]
    [InlineData("enabled", "is", "False", true)]
    [InlineData("missing", "is-not-empty", "", false)]
    public void JsonPathsSupportArraysZeroFalseAndMissingValues(string path, string op, string value, bool expected)
    {
        var config = JsonSerializer.Serialize(new { conditions = new[] { new { rules = new[] {
            new { field = "json", path, @operator = op, value }
        } } } });
        Assert.Equal(expected, Matches(config, """{"json":{"items":[{"id":0}],"enabled":false}}"""));
    }

    [Theory]
    [InlineData("{\"conditions\":[{\"rules\":[null]}]}")]
    [InlineData("{\"filterGroups\":[null]}")]
    [InlineData("{\"filterGroups\":[{\"rules\":[null]}]}")]
    [InlineData("{\"filterGroups\":[{\"rules\":[{\"operator\":\"is\",\"value\":\"orphan\"}]}]}")]
    public void MalformedConditionsFailValidation(string config)
    {
        Assert.Throws<ArgumentException>(() => IncomingWebhookConfig.Read(config).Validate());
    }

    [Fact]
    public void ExcessiveNestedExpansionFailsBeforeAllocatingEveryCombination()
    {
        var alternatives = new { type = "nested", groups = new[] {
            new { rules = new[] { new { field = "body", @operator = "is", value = "a" } } },
            new { rules = new[] { new { field = "body", @operator = "is", value = "b" } } }
        } };
        var config = JsonSerializer.Serialize(new { filterGroups = new[] { new { rules = Enumerable.Repeat(alternatives, 6) } } });
        Assert.Throws<ArgumentException>(() => IncomingWebhookConfig.Read(config));
    }

    [Fact]
    public void InvalidRegexFailsClosedDuringEvaluation()
    {
        var config = IncomingWebhookConfig.Read("""{"conditions":[{"rules":[{"field":"body","operator":"matches-regex","value":"["}]}]}""");
        config.Validate();
        using var request = JsonDocument.Parse("""{"body":"anything"}""");
        Assert.ThrowsAny<ArgumentException>(() => config.Matches(request.RootElement, "a"));
    }
}
