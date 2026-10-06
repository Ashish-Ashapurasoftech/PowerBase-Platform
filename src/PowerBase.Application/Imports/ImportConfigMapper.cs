using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports;

/// <summary>Converts between the stored definition row and the configuration the engine and API work with, so the
/// JSON columns are read and written in exactly one place.</summary>
public static class ImportConfigMapper
{
    private sealed record Options(string? ConstraintPolicy, List<string>? NotifyEmails, ImportFileOptions? File = null, List<ImportTargetConfig>? AdditionalTargets = null);

    public static ImportDefinitionConfig ToConfig(ImportDefinition def, Guid sourceTableId) => new()
    {
        Name = def.Name,
        SourceKind = def.SourceKind,
        File = ImportJson.Deserialize<Options>(def.OptionsJson)?.File,
        AdditionalTargets = ImportJson.Deserialize<Options>(def.OptionsJson)?.AdditionalTargets ?? [],
        SourceTableId = sourceTableId,
        ImportType = def.ImportType,
        MergeKeyFid = def.MergeKeyFid,
        Conditions = ImportJson.Deserialize<FilterGroup>(def.ConditionsJson),
        Mappings = ImportJson.Deserialize<List<ImportFieldMapping>>(def.FieldMappingJson) ?? [],
        ColumnRules = ImportJson.Deserialize<List<ImportColumnRule>>(def.ColumnRulesJson) ?? [],
        ConstraintPolicy = ImportJson.Deserialize<Options>(def.OptionsJson)?.ConstraintPolicy is { } p && ImportConstraintPolicy.IsValid(p)
            ? p : ImportConstraintPolicy.ImportValid,
        NotifyEmails = ImportJson.Deserialize<Options>(def.OptionsJson)?.NotifyEmails ?? []
    };

    /// <summary>Copies the editable configuration onto the stored row (identity and audit columns are left alone).</summary>
    public static void Apply(ImportDefinition entity, ImportDefinitionConfig cfg)
    {
        entity.Name = cfg.Name.Trim();
        entity.SourceKind = cfg.SourceKind;
        entity.ImportType = cfg.ImportType;
        entity.MergeKeyFid = cfg.MergeKeyFid;
        entity.ConditionsJson = cfg.Conditions is { Nodes.Count: > 0 } ? ImportJson.Serialize(cfg.Conditions) : null;
        entity.FieldMappingJson = ImportJson.Serialize(cfg.Mappings);
        entity.ColumnRulesJson = cfg.ColumnRules.Any(r => r.RemoveDuplicates || r.RequireField || r.IgnoreBlanks)
            ? ImportJson.Serialize(cfg.ColumnRules.Where(r => r.RemoveDuplicates || r.RequireField || r.IgnoreBlanks)) : null;
        var notify = ImportNotify.Normalize(cfg.NotifyEmails);
        var file = cfg.SourceKind == ImportSourceKinds.File ? cfg.File : null;
        entity.OptionsJson = cfg.ConstraintPolicy == ImportConstraintPolicy.ImportValid && notify.Count == 0 && file is null && cfg.AdditionalTargets.Count == 0
            ? null
            : ImportJson.Serialize(new Options(
                cfg.ConstraintPolicy == ImportConstraintPolicy.ImportValid ? null : cfg.ConstraintPolicy, notify.Count == 0 ? null : notify, file,
                cfg.AdditionalTargets.Count == 0 ? null : cfg.AdditionalTargets));
    }
}
