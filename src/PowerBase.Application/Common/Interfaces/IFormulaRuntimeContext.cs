namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Ambient runtime values for platform formula functions that are not tied to a
/// record or table: the frontend base URL surfaced by <c>URLRoot()</c>, and the page
/// that triggered the current evaluation, surfaced by <c>Rurl()</c> for "return here"
/// links (e.g. a URL-formula field that opens another table's Add form).
/// </summary>
public interface IFormulaRuntimeContext
{
    /// <summary>Base URL of the app frontend (no trailing slash). Empty when unconfigured.</summary>
    string UrlRoot { get; }

    /// <summary>The current request's originating page (from the Referer header), or "" when unknown.</summary>
    string ReturnUrl { get; }
}
