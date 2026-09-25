using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

public sealed record CallablePipelineDefinition(string Definition, string Name, IReadOnlyList<string> Arguments)
{
    public static CallablePipelineDefinition Parse(string? definition)
    {
        var text = definition?.Trim() ?? string.Empty;
        var match = Regex.Match(text, @"^([A-Za-z][A-Za-z0-9_]*)\(\s*([^()]*)\s*\)$");
        if (!match.Success) throw new PipelineNonRetryableException("Enter a Call Definition such as myFunction(contact_id, created_at).");
        var args = string.IsNullOrWhiteSpace(match.Groups[2].Value)
            ? Array.Empty<string>() : match.Groups[2].Value.Split(',').Select(value => value.Trim()).ToArray();
        if (args.Any(arg => !Regex.IsMatch(arg, @"^[A-Za-z][A-Za-z0-9_]*$")) || args.Distinct(StringComparer.Ordinal).Count() != args.Length)
            throw new PipelineNonRetryableException("Call and argument names must start with a letter and contain only Latin letters, numbers or underscores. Argument names must be unique.");
        return new(text, match.Groups[1].Value, args);
    }

    public static CallablePipelineDefinition ValidateConfig(string? configJson, bool caller)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson ?? "{}");
            var config = document.RootElement;
            if (config.ValueKind != JsonValueKind.Object) throw new PipelineNonRetryableException("Callable PowerFlow configuration must be a JSON object.");
            var definition = Parse(config.TryGetProperty("callDefinition", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null);
            if (caller && definition.Arguments.Count > 0)
            {
                if (!config.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object)
                    throw new PipelineNonRetryableException("Set values for the call arguments.");
                foreach (var name in definition.Arguments)
                    if (!args.TryGetProperty(name, out _)) throw new PipelineNonRetryableException($"Set a value for {name}.");
            }
            return definition;
        }
        catch (JsonException)
        {
            throw new PipelineNonRetryableException("Callable PowerFlow configuration must be valid JSON.");
        }
    }
}
