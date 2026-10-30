using PowerBase.Application.Reports;

namespace PowerBase.Application.Imports;

/// <summary>An import into one table may have a table-level filter too, like each table of a multi-table import. With one table the import-wide filter and the
/// table's own filter ask the same question of the same rows, so the table's filter is folded into the import-wide one (both must hold) before the import is
/// planned; the engine for one table never needs a second filter.</summary>
public static class ImportSingleTableFilter
{
    /// <summary>The configuration with the table's own filter combined into <see cref="ImportDefinitionConfig.Conditions"/>. The same object when there is
    /// nothing to fold, or when the import fills several tables or reads sheets separately (those keep their own filters).</summary>
    public static ImportDefinitionConfig Fold(ImportDefinitionConfig cfg)
    {
        if (cfg.UsesTargetRows || cfg.TableConditions is not { Nodes.Count: > 0 }) return cfg;

        var copy = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(cfg))!;
        var both = new FilterGroup { Logic = "and" };
        if (copy.Conditions is { Nodes.Count: > 0 }) both.Nodes.Add(new FilterNode { Group = copy.Conditions });
        both.Nodes.Add(new FilterNode { Group = copy.TableConditions! });
        copy.Conditions = both;
        copy.TableConditions = null;
        return copy;
    }
}
