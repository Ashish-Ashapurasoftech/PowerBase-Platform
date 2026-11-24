using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>JSON Handler steps: Fetch JSON and Iterate over JSON records (see <see cref="JsonHandler"/>).</summary>
public partial class PipelineEngine
{
    public const string FetchJsonHttpClient = "PipelineMakeRequest";
    public const string FetchJsonInsecureHttpClient = "PipelineFetchJsonInsecure";

    private async Task<string> ExecuteFetchJsonAsync(PipelineStep step, string payloadJson, List<PipelineStep> allSteps,
        string executionPath, PipelineStepRun stepRun, CancellationToken ct)
    {
        var config = FetchJsonDefinition.Read(step.ConfigJson);
        string Resolve(string? value) => EvaluateTokens(value, payloadJson, executionPath, allSteps);
        // Redirects are not followed (credentials must never reach another host); "Disable SSL Certificate Validation" uses a
        // client that accepts any server certificate.
        using var client = _httpClientFactory.CreateClient(config.DisableSslVerification ? FetchJsonInsecureHttpClient : FetchJsonHttpClient);
        client.Timeout = TimeSpan.FromMinutes(5);
        var executor = new FetchJsonExecutor((request, token) => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token));
        stepRun.InputContext = SerializeAndSanitizeAudit(new { config.AuthenticationSchema, Method = config.MethodType, Url = config.JsonUrl });
        FetchJsonResult result;
        try { result = await executor.ExecuteAsync(config, Resolve, ct, audit => stepRun.InputContext = SerializeAndSanitizeAudit(audit)); }
        catch (InvalidOperationException ex) { throw new PipelineNonRetryableException(ex.Message); }
        // Quickbase exports Headers and Status; the document itself travels to Iterate over JSON records in "body".
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["headers"] = result.Headers,
            ["status"] = result.Status,
            ["body"] = result.Body,
            ["_metadata"] = new Dictionary<string, object?>
            {
                ["stats"] = new Dictionary<string, object?> { ["max_file_size"] = 0, ["n_files"] = 0, ["total_bytes_streamed"] = 0 }
            }
        });
    }

    private string ExecuteIterateJsonRecords(PipelineStep step, string payloadJson, List<PipelineStep> allSteps,
        Dictionary<string, object> stepsDict, string executionPath, PipelineStepRun stepRun)
    {
        var config = IterateJsonDefinition.Read(step.ConfigJson);
        var errors = config.Errors();
        if (errors.Count > 0) throw new PipelineNonRetryableException(errors[0]);
        var sourceRef = config.JsonSourceStepId!.Trim();
        if (sourceRef.StartsWith("{{") && sourceRef.EndsWith("}}")) sourceRef = sourceRef[2..^2].Trim().Replace("steps.", "");
        var sourceStep = allSteps.FirstOrDefault(s => s.RefId == sourceRef || s.PublicId.ToString() == sourceRef || s.Id.ToString() == sourceRef);
        if (sourceStep != null) sourceRef = sourceStep.RefId;
        if (!stepsDict.TryGetValue(sourceRef, out var source))
            throw new PipelineNonRetryableException($"JSON Source '{config.JsonSourceStepId}' has not run before this step.");

        var document = DocumentOf(source);
        string Resolve(string? value) => EvaluateTokens(value, payloadJson, executionPath, allSteps);
        // "Expression (advanced)", as Quickbase runs it (live, 2026-11-23): a Jinja template over the earlier steps ({{aa.status == 200}});
        // the record itself is not in scope. A name that is not an earlier step renders empty on its own ({{ab.x}} → false) but fails
        // the step when used in an operation ("Validation error: Incorrect jinja template: 'x' is undefined"). The rendered text is
        // false when it is "", False, no, 0, None (any case; "off", "n" stay true) and true otherwise ("0.0", "abc").
        var knownRefs = allSteps.Where(s => s.Id != step.Id).Select(s => s.RefId).Where(r => !string.IsNullOrEmpty(r)).ToHashSet(StringComparer.Ordinal);
        bool Expression(JsonObject record, string text)
        {
            foreach (Match block in Regex.Matches(text, @"\{\{(.*?)\}\}", RegexOptions.Singleline))
            {
                var inner = block.Groups[1].Value.Trim();
                foreach (Match reference in Regex.Matches(inner, @"(?<![\w.'""])([A-Za-z_]\w*)\.([A-Za-z_]\w*)"))
                {
                    if (knownRefs.Contains(reference.Groups[1].Value) || reference.Groups[1].Value == "steps") continue;
                    if (inner != reference.Value) throw new PipelineNonRetryableException($"Validation error: Incorrect jinja template: '{reference.Groups[2].Value}' is undefined");
                    text = text.Replace(block.Value, "");
                }
            }
            var rendered = EvaluateTokens(text, payloadJson, executionPath, allSteps).Trim();
            return JsonExpressionTruth(rendered);
        }
        var records = JsonRecordsIterator.Iterate(document, config, Resolve, Expression);
        // Filters as plain text: a JsonNode's Parent points back up the tree, which the audit sanitizer would walk forever.
        var filters = config.Filters.Select(group => group.Select(rule => new
        {
            field = rule.Field, condition = rule.Condition, value = rule.Value?.ToJsonString(), value2 = rule.Value2?.ToJsonString()
        }).ToList()).ToList();
        stepRun.InputContext = SerializeAndSanitizeAudit(new { JsonSource = sourceRef, config.JsonRecordsPath, config.Limit, Filters = filters, ItemCount = records.Count });
        return new JsonObject
        {
            ["records"] = records,
            ["count"] = records.Count,
            ["_metadata"] = JsonSerializer.SerializeToNode(SearchMetadata(records.Count))
        }.ToJsonString();
    }

    /// <summary>Quickbase's reading of an expression's rendered text.</summary>
    public static bool JsonExpressionTruth(string rendered) =>
        !(rendered.Trim().ToLowerInvariant() is "" or "false" or "no" or "0" or "none");

    /// <summary>Condition type category of a JSON Handler field: number / boolean from the Schema Sample, text otherwise.</summary>
    private static string JsonFieldTypeCategory(PipelineStep step, string field)
    {
        if (step.Subtype == JsonHandler.FetchSubtype) return field == "status" ? "NUMBER" : "TEXT";
        List<JsonSchemaField>? fields;
        try { fields = JsonRecordsIterator.Fields(IterateJsonDefinition.Read(step.ConfigJson).JsonSchemaSample); }
        catch (PipelineNonRetryableException) { fields = null; }
        return fields?.FirstOrDefault(f => f.Key == field)?.Type switch
        {
            "integer" or "number" => "NUMBER",
            "boolean" => "BOOLEAN",
            _ => "TEXT"
        };
    }

    /// <summary>The JSON text a source step carries: Fetch JSON's fetched document, an Incoming Request's body, or the step's own output.</summary>
    private static string DocumentOf(object? source)
    {
        if (source is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("body", out var body))
                return body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : body.GetRawText();
            return element.GetRawText();
        }
        if (source is string text) return text;
        if (source is IDictionary<string, object> dict && dict.TryGetValue("body", out var value))
            return value is string s ? s : JsonSerializer.Serialize(value);
        return JsonSerializer.Serialize(source);
    }
}
