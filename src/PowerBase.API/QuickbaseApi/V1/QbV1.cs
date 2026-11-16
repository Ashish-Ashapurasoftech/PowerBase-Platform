namespace PowerBase.API.QuickbaseApi.V1;

/// <summary>
/// Version 1 of the Quickbase-compatible API. Every v1 controller lives in this namespace/folder and routes under
/// <see cref="ROUTE_PREFIX"/>. A later version gets its own V2 folder and prefix, leaving v1 untouched.
/// </summary>
public static class QbV1
{
    public const string ROUTE_PREFIX = "v1";
    public const string VERSION = "1";
}
