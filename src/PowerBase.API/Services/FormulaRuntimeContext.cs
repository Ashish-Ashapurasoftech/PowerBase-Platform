using Microsoft.AspNetCore.Http;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Services;

/// <summary>
/// Supplies ambient formula runtime values. <see cref="UrlRoot"/> comes from
/// <c>Frontend:BaseUrl</c> (the same setting used for invite/reset links).
/// <see cref="ReturnUrl"/> is read per-request from the Referer header via
/// <see cref="IHttpContextAccessor"/> (safe to use from this singleton — it holds
/// only an AsyncLocal reference to the ambient HttpContext).
/// </summary>
public sealed class FormulaRuntimeContext : IFormulaRuntimeContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public FormulaRuntimeContext(IConfiguration config, IHttpContextAccessor httpContextAccessor)
    {
        UrlRoot = (config["Frontend:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        _httpContextAccessor = httpContextAccessor;
    }

    public string UrlRoot { get; }

    public string ReturnUrl => _httpContextAccessor.HttpContext?.Request.Headers.Referer.ToString() ?? string.Empty;
}
