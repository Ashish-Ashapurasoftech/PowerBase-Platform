namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Ambient runtime values for platform formula functions that are not tied to a
/// record or table — currently the frontend base URL surfaced by <c>URLRoot()</c>.
/// </summary>
public interface IFormulaRuntimeContext
{
    /// <summary>Base URL of the app frontend (no trailing slash). Empty when unconfigured.</summary>
    string UrlRoot { get; }
}
