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
        var records = JsonRecordsIterator.Iterate(document, config, Resolve);
        stepRun.InputContext = SerializeAndSanitizeAudit(new { JsonSource = sourceRef, config.JsonRecordsPath, config.Limit, config.Filters, ItemCount = records.Count });
        return new JsonObject
        {
            ["records"] = records,
            ["count"] = records.Count,
            ["_metadata"] = JsonSerializer.SerializeToNode(SearchMetadata(records.Count))
        }.ToJsonString();
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
