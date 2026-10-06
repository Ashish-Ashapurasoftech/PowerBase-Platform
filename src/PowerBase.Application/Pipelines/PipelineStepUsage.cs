using System.Text.Json;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

public static class PipelineStepUsage
{
    public static bool IsBillable(PipelineStep step)
    {
        if (step.Subtype is "send-email" or "send-email-outlook") return true;
        if (step.Subtype != "make-request") return false;
        try
        {
            using var document = JsonDocument.Parse(step.ConfigJson ?? "{}");
            var config = document.RootElement;
            if (MakeRequestDefinition.Read(step.ConfigJson ?? "{}").IsPowerBase) return false;
            var url = config.TryGetProperty("baseUrl", out var baseUrl) ? baseUrl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                url = config.TryGetProperty("url", out var value) ? value.GetString() : null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }
        catch (JsonException) { return false; }
    }
}
