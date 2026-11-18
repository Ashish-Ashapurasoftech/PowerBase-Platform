using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>
/// The fields a PowerFlow's steps use. Deciding whether a step really uses a field (and not another table's field with the same
/// number) is left to <see cref="PipelineFieldReferenceMatcher"/>, the same check that already guards deleting a field — so the Usage tab
/// and the delete guard can never disagree. This class only decides which tables to ask about and records the answers.
/// </summary>
public static class PipelineReferenceExtractor
{
    private static readonly Regex GuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    /// <summary>Every table public id a step mentions anywhere in its settings ("connection:guid" settings included). A step
    /// can only use a field of a table it names, directly or through the step a {{...}} token reads from — and that step names it.</summary>
    public static IReadOnlySet<Guid> TablesMentioned(PipelineSource pipeline)
    {
        var ids = new HashSet<Guid>();
        foreach (var step in pipeline.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.ConfigJson)) continue;
            foreach (Match match in GuidRegex.Matches(step.ConfigJson))
                if (Guid.TryParse(match.Value, out var id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>One row per (step, field of <paramref name="table"/>) the step uses. <paramref name="collector"/> must be built for
    /// this pipeline and for the table's id, so the rows are filed under the table of the field.</summary>
    public static void Extract(PipelineSource pipeline, Guid tablePublicId, TableFieldIndex table, FieldReferenceCollector collector)
    {
        foreach (var step in pipeline.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.ConfigJson)) continue;
            // A step names the table itself, or reads another step's fields through a {{...}} token — anything else cannot use it.
            var namesTable = step.ConfigJson.Contains(tablePublicId.ToString(), StringComparison.OrdinalIgnoreCase);
            if (!namesTable && !step.ConfigJson.Contains("{{", StringComparison.Ordinal)) continue;

            var usage = UsageOf(step.RefId);
            foreach (var field in table.Fields)
                if (field.Fid.HasValue && PipelineFieldReferenceMatcher.ReferencesField(step, pipeline.Steps, field, tablePublicId))
                    collector.Add(field.Id, usage);
        }
    }

    /// <summary>"step:" + the step's RefId, cut to the 40 characters the Usage column holds.</summary>
    public static string UsageOf(string refId)
    {
        var usage = FieldReferenceUsages.PowerFlowStep + refId;
        return usage.Length <= 40 ? usage : usage[..40];
    }
}
