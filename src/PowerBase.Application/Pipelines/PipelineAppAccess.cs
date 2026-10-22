using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>The apps a PowerFlow step may point at: those whose PowerFlows the user can read. Being a member of an app is not enough —
/// its tables and fields (the next selectors) ask for that permission, so an app without it led to a table list that failed to load.</summary>
public static class PipelineAppAccess
{
    public static async Task<List<App>> ReadableAsync(IAppAccessService access, IEnumerable<App> apps, CancellationToken ct)
    {
        var readable = new List<App>();
        foreach (var app in apps)
        {
            try
            {
                await access.RequirePermissionByAppIdAsync(app.Id, PermissionCodes.PowerFlowsRead, ct);
                readable.Add(app);
            }
            catch (UnauthorizedActionException) { }
        }
        return readable;
    }
}
