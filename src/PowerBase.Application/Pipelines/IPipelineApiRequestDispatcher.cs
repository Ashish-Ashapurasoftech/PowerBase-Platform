namespace PowerBase.Application.Pipelines;

/// <summary>Runs a PowerBase API request with the already verified connection scope.</summary>
public interface IPipelineApiRequestDispatcher
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, IServiceProvider connectionServices, CancellationToken ct);
}
