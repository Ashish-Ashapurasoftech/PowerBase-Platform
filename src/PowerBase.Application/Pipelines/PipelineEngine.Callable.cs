using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines;

public partial class PipelineEngine
{
    private async Task<object> BuildCallingPipelineAsync(long pipelineId, Guid messageId,
        Dictionary<string, object> context, CancellationToken ct)
    {
        var pipeline = await _pipelineRepo.GetByIdAsync(pipelineId, ct);
        var appRepository = _serviceProvider.GetService<IAppRepository>();
        var app = pipeline != null && appRepository != null
            ? await appRepository.GetByIdAsync(pipeline.AppId, ct) : null;
        var configuration = _serviceProvider.GetService<IConfiguration>();
        var baseUrl = (configuration?["Frontend:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var url = pipeline != null && app != null
            ? $"{baseUrl}/app/{app.PublicId}/pipelines/{pipeline.PublicId}" : null;
        var run = context.GetValueOrDefault("_RunPublicId")?.ToString() ?? messageId.ToString();
        return new
        {
            id = pipelineId,
            name = pipeline?.Name,
            url,
            run,
            activity_url = url == null ? null : $"{url}?runId={Uri.EscapeDataString(run)}",
            triggered_at = context.GetValueOrDefault("_RunStartedOn") ?? DateTime.UtcNow
        };
    }
}
