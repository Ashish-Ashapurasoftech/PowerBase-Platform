using System.Text.Json;
using PowerBase.Domain.Exceptions;
using Scriban;

namespace PowerBase.Application.Pipelines;

/// <summary>Uses the shared trigger editor's AND/OR tree and scalar operators.</summary>
public static class CallablePipelineFilter
{
    private static string Category(string field) => field switch
    {
        "calling_pipeline.id" => "NUMBER",
        "calling_pipeline.triggered_at" => "DATE",
        _ => "TEXT"
    };

    public static IncomingWebhookConfig Read(string? json, CallablePipelineDefinition definition)
    {
        var config = IncomingWebhookConfig.Read(json);
        var fields = definition.Arguments.Concat(new[] { "calling_pipeline.id", "calling_pipeline.name",
            "calling_pipeline.url", "calling_pipeline.run", "calling_pipeline.activity_url", "calling_pipeline.triggered_at" }).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in config.Conditions.SelectMany(group => group.Rules).Where(rule => !rule.IsBlank))
        {
            if (rule.Field == "expression" && config.IsSimpleFilter == false)
            {
                var expression = rule.Value?.Contains("{{") == true ? rule.Value : "{{ " + rule.Value + " }}";
                if (string.IsNullOrWhiteSpace(rule.Value) || Template.Parse(expression).HasErrors)
                    throw new PipelineNonRetryableException("Enter a valid PowerFlow trigger expression.");
            }
            else if (!fields.Contains(rule.Field) || !PipelineFilterEvaluator.AllowedOperatorsByTypeCategory[Category(rule.Field)].Contains(rule.Operator))
                throw new PipelineNonRetryableException("Select a valid PowerFlow trigger field and operator.");
            else if (rule.Operator is not ("is-empty" or "is-not-empty") && string.IsNullOrWhiteSpace(rule.Value))
                throw new PipelineNonRetryableException("Enter a PowerFlow trigger filter value.");
        }
        return config;
    }

    public static bool Matches(string? json, JsonElement envelope, string refId)
        => Evaluate(json, envelope, refId, out _);

    /// <summary>Human-readable reason the trigger filter rejected the call, e.g. <c>name "nirav" does not satisfy: name contains "ron"</c>.</summary>
    public static string DescribeMismatch(string? json, JsonElement envelope, string refId)
    {
        Evaluate(json, envelope, refId, out var reason);
        return reason;
    }

    private static bool Evaluate(string? json, JsonElement envelope, string refId, out string reason)
    {
        reason = "";
        var definition = CallablePipelineDefinition.ValidateConfig(json, false);
        var config = Read(json, definition);
        var values = envelope.GetProperty("Arguments").EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        values["calling_pipeline"] = envelope.TryGetProperty("CallingPipeline", out var caller) ? caller.Clone() : null;
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(values));
        string Actual(IncomingWebhookRule rule)
        {
            var value = data.RootElement;
            foreach (var segment in rule.Field.Split('.'))
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                { value = default; break; }
            }
            return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.ToString();
        }
        bool Match(IncomingWebhookRule rule)
        {
            if (rule.Field == "expression" && config.IsSimpleFilter == false)
                return new IncomingWebhookConfig { Conditions = new() { new() { Rules = new() { rule } } } }.Matches(data.RootElement, refId);
            return PipelineFilterEvaluator.EvaluateConditionOperator(Actual(rule), rule.Operator, rule.Value, Category(rule.Field));
        }
        string Describe(IncomingWebhookRule rule)
        {
            if (rule.Field == "expression" && config.IsSimpleFilter == false)
                return $"expression {rule.Value} evaluated to false";
            var op = rule.Operator.Replace('-', ' ');
            return rule.Operator is "is-empty" or "is-not-empty"
                ? $"{rule.Field} {op} (actual value: \"{Actual(rule)}\")"
                : $"{rule.Field} {op} \"{rule.Value}\" (actual value: \"{Actual(rule)}\")";
        }

        if (config.Conditions.Count == 0) return true;
        var failures = new List<string>();
        foreach (var group in config.Conditions)
        {
            var failed = group.Rules.Where(rule => !rule.IsBlank).FirstOrDefault(rule => !Match(rule));
            if (failed == null) return true;
            failures.Add(Describe(failed));
        }
        reason = "Condition not met: " + string.Join(" OR ", failures);
        return false;
    }
}
