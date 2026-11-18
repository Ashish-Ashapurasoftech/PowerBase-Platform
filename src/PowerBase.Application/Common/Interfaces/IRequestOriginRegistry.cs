namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// The address each tenant last reached this API at (scheme, host and path base of a signed-in request), so work that runs
/// later without a request — a PowerFlow run — can give out full links to the API, such as a File field's Browser url.
/// Nothing is configured: every tenant gets the address its own users use.
/// </summary>
public interface IRequestOriginRegistry
{
    void Remember(long tenantId, string origin);
    string? Get(long tenantId);
}
