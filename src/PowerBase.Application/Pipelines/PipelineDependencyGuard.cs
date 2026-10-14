using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Shared "is this used by a PowerFlow?" checks for every path that removes a field or a table (field / bulk / table delete,
/// relationship delete, relationship field removal). Deleting a whole app is deliberately not checked. Active and inactive PowerFlows both count.
/// </summary>
public static class PipelineDependencyGuard
{
    /// <summary>Blocks removing <paramref name="fields"/> of <paramref name="table"/> when a PowerFlow step uses any of them,
    /// or a PowerFlow in another account is triggered through them.</summary>
    public static async Task EnsureFieldsNotUsedAsync(
        IPipelineRepository? pipelineRepo, AppTable table, IReadOnlyCollection<AppField> fields, CancellationToken ct)
    {
        if (pipelineRepo is null) return;
        var withFid = fields.Where(f => f.Fid.HasValue).ToList();
        if (withFid.Count == 0) return;

        var references = await pipelineRepo.GetActivePipelineReferencesForTableFieldsAsync(withFid, table.PublicId, ct);
        if (references.Count > 0)
        {
            var byFid = withFid.GroupBy(f => f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
            var details = references
                .GroupBy(r => (r.PipelineName, r.StepLabel))
                .Select(g => $"PowerFlow '{g.Key.PipelineName}' (Step: '{g.Key.StepLabel}') uses: " +
                             string.Join(", ", g.Select(r => r.Fid).Distinct().Select(fid => FieldDisplay(byFid[fid]))));
            throw Blocked("Field", $"Cannot remove field(s) of table '{table.Name}' because they are used in PowerFlows. {string.Join("; ", details)}");
        }

        if (await pipelineRepo.GetCrossAccountTriggerCountAsync(table.PublicId, withFid.Select(f => f.Fid!.Value).ToList(), ct) > 0)
            throw Blocked("Field", $"Cannot remove field(s) of table '{table.Name}' because a PowerFlow in another account is triggered through them.");
    }

    public static string FieldDisplay(AppField field) => string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;

    private static ValidationException Blocked(string key, string message) =>
        new(new Dictionary<string, string[]> { { key, new[] { message } } });
}
