using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

public class CallablePipelineFilterTests
{
    [Theory]
    [InlineData("Customer sync", "Alice", true)]
    [InlineData("Other sync", "Alice", false)]
    [InlineData("Customer sync", "Bob", false)]
    public void FiltersArgumentsAndCallerName(string caller, string name, bool expected)
    {
        const string config = """{"callDefinition":"f(name)","filterGroups":[{"rules":[{"field":"name","operator":"is","value":"Alice"},{"field":"calling_pipeline.name","operator":"starts-with","value":"Customer"}]}]}""";
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { Arguments = new { name }, CallingPipeline = new { name = caller } }));
        CallablePipelineFilter.Matches(config, payload.RootElement, "ref_1").Should().Be(expected);
    }

    [Fact]
    public void SupportsNestedAlternativesAndNumericCallerId()
    {
        const string config = """{"callDefinition":"f(name)","filterGroups":[{"rules":[{"field":"calling_pipeline.id","operator":"greater_than","value":2},{"type":"nested","groups":[{"rules":[{"field":"name","operator":"is","value":"Alice"}]},{"rules":[{"field":"name","operator":"is","value":"Bob"}]}]}]}]}""";
        using var payload = JsonDocument.Parse("""{"Arguments":{"name":"Bob"},"CallingPipeline":{"id":10}}""");
        CallablePipelineFilter.Matches(config, payload.RootElement, "ref_1").Should().BeTrue();
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"isSimpleFilter\":false,\"advancedQuery\":\"trigger.name == 'Alice'\"}", true)]
    [InlineData("{\"isSimpleFilter\":false,\"advancedQuery\":\"trigger.name == 'Bob'\"}", false)]
    public void SupportsNoFilterAndExpressions(string filter, bool expected)
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, object>>(filter)!;
        config["callDefinition"] = "f(name)";
        using var payload = JsonDocument.Parse("""{"Arguments":{"name":"Alice"}}""");
        CallablePipelineFilter.Matches(JsonSerializer.Serialize(config), payload.RootElement, "ref_1").Should().Be(expected);
    }

    [Fact]
    public void AllowsAnArgumentNamedExpressionInSimpleFilters()
    {
        const string config = """{"callDefinition":"f(expression)","filterGroups":[{"rules":[{"field":"expression","operator":"is","value":"Alice"}]}]}""";
        using var payload = JsonDocument.Parse("""{"Arguments":{"expression":"Bob"}}""");
        CallablePipelineFilter.Matches(config, payload.RootElement, "ref_1").Should().BeFalse();
    }

    [Fact]
    public void RejectsIncompleteValueButAllowsZeroAndEmptyOperators()
    {
        foreach (var value in new[] { "\"\"", "null", "\"   \"" })
            FluentActions.Invoking(() => CallablePipelineDefinition.ValidateConfig("{\"callDefinition\":\"f(name)\",\"filterGroups\":[{\"rules\":[{\"field\":\"name\",\"operator\":\"is\",\"value\":" + value + "}]}]}", false))
                .Should().Throw<PipelineNonRetryableException>();
        CallablePipelineDefinition.ValidateConfig("""{"callDefinition":"f(name)","filterGroups":[{"rules":[{"field":"name","operator":"is","value":0}]}]}""", false).Should().NotBeNull();
        CallablePipelineDefinition.ValidateConfig("""{"callDefinition":"f(name)","filterGroups":[{"rules":[{"field":"name","operator":"is-empty","value":""}]}]}""", false).Should().NotBeNull();
    }

    [Fact]
    public void RejectsDeletedParameter()
    {
        FluentActions.Invoking(() => CallablePipelineDefinition.ValidateConfig("""{"callDefinition":"f(new_name)","filterGroups":[{"rules":[{"field":"old_name","operator":"is","value":"Alice"}]}]}""", false))
            .Should().Throw<PipelineNonRetryableException>();
    }
}
