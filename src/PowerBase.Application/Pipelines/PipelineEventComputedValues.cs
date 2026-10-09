using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>What one listening trigger needs to know to decide whether computed values must be part of its event.</summary>
public sealed record PipelineEventListener(bool IsBulk, bool OnDeleted, bool OwnedByAnotherTenant, IReadOnlyList<string?> FilterTexts);

/// <summary>
/// Decides whether the Lookup / Summary / Formula values of a written record must be computed while the event is
/// written. Computing them costs several queries per record, so it is only done when something listening needs
/// them at that instant; everything else gets them re-read when the PowerFlow runs (<see cref="PipelineTriggerRefresh"/>).
/// </summary>
public static class PipelineEventComputedValues
{
    public static bool IsComputed(AppField f) =>
        f.Fid.HasValue && !f.IsDeleted &&
        (f.TypeCode is "Lookup" or "Summary" or "Formula" || f.TypeCode.StartsWith("Formula_", StringComparison.Ordinal));

    /// <summary>True when some listener needs computed values in the event itself: a bulk event (its staged records carry
    /// them), a delete event (the record is gone by run time), a PowerFlow of another tenant (it cannot re-read this
    /// tenant's record when it runs), or a filter that mentions a computed field (by fid_N, name or {N.…} id).</summary>
    public static bool Required(IEnumerable<PipelineEventListener> listeners, IReadOnlyList<AppField> fields)
    {
        var computed = fields.Where(IsComputed).ToList();
        if (computed.Count == 0) return false;

        foreach (var listener in listeners)
        {
            if (listener.IsBulk || listener.OnDeleted || listener.OwnedByAnotherTenant) return true;
            if (computed.Any(field => listener.FilterTexts.Any(text => Mentions(text, field)))) return true;
        }
        return false;
    }

    private static bool Mentions(string? text, AppField field) =>
        !string.IsNullOrEmpty(text) &&
        (text.Contains($"fid_{field.Fid}", StringComparison.OrdinalIgnoreCase) ||
         text.Contains($"fid_{field.Id}", StringComparison.OrdinalIgnoreCase) ||
         text.Contains($"{{{field.Fid}.", StringComparison.Ordinal) ||
         text.Contains($"\"{field.Name}\"", StringComparison.OrdinalIgnoreCase));
}
