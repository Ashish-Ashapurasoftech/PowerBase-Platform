using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>
/// The fields a saved import uses: the ones it writes to (in every table it fills, sheet mappings included), the key it matches
/// existing records on, the source fields it reads, the fields its filters test and the source fields its formulas and virtual
/// columns read. Read from the saved configuration only — nothing here runs or validates the import.
///
/// A file import has no source table (its "fields" are file columns), so only what it writes and matches on counts for it.
/// Rows are filed under the table of the field they name, so one import can leave rows on its source and on every table it fills.
/// </summary>
public static class ImportReferenceExtractor
{
    public static async Task<IReadOnlyCollection<FieldReferenceRow>> ExtractAsync(
        ImportDefinition def, IFieldIndexProvider tables, FormulaEngine engine, CancellationToken ct)
    {
        var config = ImportConfigMapper.ToConfig(def, Guid.Empty);
        var isFile = def.SourceKind == ImportSourceKinds.File;
        var rows = new HashSet<FieldReferenceRow>();

        var source = !isFile && def.SourceTableId is { } sourceId ? await tables.GetByIdAsync(sourceId, ct) : null;
        var destination = await tables.GetByIdAsync(def.DestinationTableId, ct);

        void Add(TableFieldIndex? table, long? fid, string usage)
        {
            if (table?.IdOfFid(fid) is { } id)
                rows.Add(new FieldReferenceRow(FieldReferenceSourceTypes.Import, def.Id, table.TableId, id, usage));
        }

        void Filter(FilterGroup? group)
        {
            if (group is null || source is null) return; // a file's columns are not fields
            foreach (var node in group.Nodes)
            {
                if (node.Condition is { } c)
                {
                    Add(source, c.FieldId, FieldReferenceUsages.Filter);
                    Add(source, c.ValueFieldId, FieldReferenceUsages.Filter);
                }
                Filter(node.Group);
            }
        }

        void Formula(string? text)
        {
            if (source is null || string.IsNullOrWhiteSpace(text)) return;
            foreach (var id in source.FieldsReadBy(engine, text))
                rows.Add(new FieldReferenceRow(FieldReferenceSourceTypes.Import, def.Id, source.TableId, id, FieldReferenceUsages.Formula));
        }

        void Target(TableFieldIndex? table, string importType, int? mergeKeyFid, IEnumerable<ImportFieldMapping> mappings)
        {
            if (importType == ImportTypes.Merge) Add(table, mergeKeyFid, FieldReferenceUsages.MatchKey);
            foreach (var m in mappings.Where(m => !m.DoNotImport))
            {
                Add(table, m.DestFid, FieldReferenceUsages.WritesTo);
                if (m.Source == ImportMappingSource.Formula) Formula(m.Formula);
                else if (m.Source == ImportMappingSource.Dynamic && m.SourceFid is { } fid && !ImportVirtual.IsVirtualFid(fid))
                    Add(source, fid, FieldReferenceUsages.ReadsFrom);
            }
        }

        Target(destination, def.ImportType, def.MergeKeyFid, config.Mappings);
        foreach (var sheet in config.SheetMappings) Target(destination, ImportTypes.Copy, null, sheet.Mappings.Where(m => !m.DoNotImport).Select(WritesOnly));

        foreach (var extra in config.AdditionalTargets)
        {
            var table = await tables.GetByPublicIdAsync(extra.DestinationTableId, ct);
            Target(table, extra.ImportType, extra.MergeKeyFid, extra.Mappings);
            foreach (var sheet in extra.SheetMappings) Target(table, ImportTypes.Copy, null, sheet.Mappings.Select(WritesOnly));
            Filter(extra.Conditions);
        }

        Filter(config.Conditions);
        Filter(config.TableConditions);
        foreach (var virtualColumn in config.VirtualColumns) Formula(virtualColumn.Formula);

        return rows;
    }

    /// <summary>A sheet's columns are file columns, so only the field a sheet mapping writes to is a field of a table.</summary>
    private static ImportFieldMapping WritesOnly(ImportFieldMapping m) =>
        new() { DestFid = m.DestFid, Source = ImportMappingSource.Static, DoNotImport = m.DoNotImport };
}
