using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// The formula runtime values in the compute worker. URLRoot() is the deployment's frontend address, from the same setting the API reads
/// (Frontend:BaseUrl), so a stored URL formula equals the one a report shows. There is no request, so no return URL (formulas using Rurl()
/// are never stored).
/// </summary>
public sealed class WorkerFormulaRuntimeContext(IConfiguration configuration) : IFormulaRuntimeContext
{
    public string UrlRoot { get; } = (configuration["Frontend:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');

    public string ReturnUrl => string.Empty;
}
