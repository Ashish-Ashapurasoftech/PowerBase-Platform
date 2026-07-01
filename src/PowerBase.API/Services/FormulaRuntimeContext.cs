using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Services;

/// <summary>
/// Supplies ambient formula runtime values from configuration. <see cref="UrlRoot"/>
/// comes from <c>Frontend:BaseUrl</c> (the same setting used for invite/reset links).
/// </summary>
public sealed class FormulaRuntimeContext : IFormulaRuntimeContext
{
    public FormulaRuntimeContext(IConfiguration config)
        => UrlRoot = (config["Frontend:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');

    public string UrlRoot { get; }
}
