using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>An import template as the Import with CSV step lists it.</summary>
/// <param name="MergeFieldId">The field (FID) a merge import matches existing records on; null for an import that only adds records.</param>
/// <param name="MergeFieldLabel">That field's name, shown read-only under the selected import.</param>
public sealed record PipelineImportTemplateDto(
    Guid TemplateId, string Name, string ImportType, Guid TableId, string TableName,
    bool NeedsAttention, string? AttentionReason, int? MergeFieldId, string? MergeFieldLabel);

/// <summary>
/// The imports of an app the Import with CSV step can run: those that read a file, from the import feature's own PowerFlow
/// list (<see cref="ListPowerFlowImportTemplatesHandler"/>, unchanged), plus the merge field each one matches records on.
/// Runs with whatever services it is given — the caller's own account, or a connected account's scope.
/// </summary>
public static class PipelineImportTemplates
{
    public static async Task<IReadOnlyList<PipelineImportTemplateDto>> ListAsync(IServiceProvider services, Guid appId, Guid? tableId, CancellationToken ct)
    {
        var templates = await services.GetRequiredService<ListPowerFlowImportTemplatesHandler>()
            .HandleAsync(appId, tableId, null, ListPowerFlowImportTemplatesHandler.SourceKindFile, ct);
        if (templates.Count == 0) return [];

        var definitions = services.GetRequiredService<IImportDefinitionRepository>();
        var fieldRepo = services.GetRequiredService<IAppFieldRepository>();
        var fieldsByTable = new Dictionary<long, IReadOnlyList<AppField>>();
        var result = new List<PipelineImportTemplateDto>(templates.Count);
        foreach (var template in templates)
        {
            int? mergeFieldId = null;
            string? mergeFieldLabel = null;
            if (string.Equals(template.ImportType, ImportTypes.Merge, StringComparison.OrdinalIgnoreCase) &&
                await definitions.GetByPublicIdAsync(template.TemplateId, ct) is { MergeKeyFid: int fid } definition)
            {
                mergeFieldId = fid;
                if (!fieldsByTable.TryGetValue(definition.DestinationTableId, out var fields))
                    fieldsByTable[definition.DestinationTableId] = fields = await fieldRepo.ListByTableAsync(definition.DestinationTableId, ct);
                var field = fields.FirstOrDefault(f => f.Fid == fid);
                mergeFieldLabel = field is null ? $"Field {fid}" : string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;
            }
            result.Add(new PipelineImportTemplateDto(template.TemplateId, template.Name, template.ImportType, template.TableId, template.TableName,
                template.NeedsAttention, template.AttentionReason, mergeFieldId, mergeFieldLabel));
        }
        return result;
    }
}
