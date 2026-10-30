using System.Text.Json;
using System.Text.Json.Nodes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Tables.Queries.GetTable;

/// <param name="SummarySourceSettings">Lookup / Summary field Id → the current Settings JSON of the field whose value
/// it shows: a Lookup's parent field, a Sum / Avg / Min / Max summary's child target field. Lets the client render the
/// value in that field's own display format (decimals, currency symbol, month name, show time, …) wherever the
/// lookup's / summary's own Behavior Settings (its <c>display</c> object) leave a setting unset.</param>
/// <param name="ReferenceKeys">Reference field Id → the parent key it shows (see <see cref="ReferenceKey"/>), so the
/// client can offer and apply the Behavior Settings of that key's type — a Record ID# / Number key gets number formats,
/// a Date key date formats, as a Quickbase reference of that type does.</param>
public record GetTableResult(
    AppTable Table,
    IReadOnlyList<AppField> Fields,
    IReadOnlyDictionary<long, string?> SummarySourceSettings,
    IReadOnlyDictionary<long, ReferenceKey>? ReferenceKeys = null);

/// <summary>The parent field a Reference shows — the relationship's Display Key, else the parent table's Set Key, else
/// Record ID# (Fid 3) — and the type of value it holds (Number for Record ID#; null when that can't be told).</summary>
public record ReferenceKey(int? Fid, string? ValueTypeCode);

public class GetTableQueryHandler
{
    /// <summary>Record ID#'s Fid, the key a Reference shows when neither its relationship nor its parent table sets one.</summary>
    private const int RecordIdFid = 3;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository? _relRepo;

    public GetTableQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRelationshipRepository? relRepo = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
    }

    public async Task<GetTableResult> HandleAsync(GetTableQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.PublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var fieldsByTable = new Dictionary<long, IReadOnlyList<AppField>> { [table.Id] = fields };
        var summarySourceSettings = await ResolveSummarySourceSettingsAsync(fields, fieldsByTable, ct);
        var referenceKeys = await ResolveReferenceKeysAsync(table, fields, fieldsByTable, ct);
        // The fields a user sees and picks from (forms, reports, filters, formulas…) — helper fields stay out.
        return new GetTableResult(table, Relationships.HelperFields.Visible(fields), summarySourceSettings, referenceKeys);
    }

    /// <summary>The key each Reference field of <paramref name="table"/> shows, resolved the way the record read does
    /// (<see cref="KeyFieldResolver.ResolveDisplayKey"/>), live — so a Set Key or Display Key change shows up straight
    /// away. One relationship read for the table, one read per parent table, none for a table without references.</summary>
    private async Task<IReadOnlyDictionary<long, ReferenceKey>> ResolveReferenceKeysAsync(
        AppTable table, IReadOnlyList<AppField> fields, Dictionary<long, IReadOnlyList<AppField>> fieldsByTable, CancellationToken ct)
    {
        var result = new Dictionary<long, ReferenceKey>();
        var references = fields.Where(f => f.TypeCode == nameof(Domain.Enums.FieldTypeCode.Reference) && f.Fid.HasValue).ToList();
        if (references.Count == 0 || _relRepo is null) return result;

        var relationships = await _relRepo.ListByChildTableAsync(table.Id, ct);
        var parentTables = new Dictionary<long, AppTable>();
        foreach (var reference in references)
        {
            var settings = FormulaTypeMap.ParseReferenceSettings(reference.Settings);
            var rel = relationships.FirstOrDefault(r => r.Id == settings?.RelationshipId)
                      ?? relationships.FirstOrDefault(r => r.ReferenceFid == reference.Fid);
            if ((rel?.ParentTableId ?? settings?.ParentTableId) is not long parentTableId) continue;

            if (!parentTables.TryGetValue(parentTableId, out var parent))
            {
                try { parent = await _tableRepo.GetByIdAsync(parentTableId, ct); }
                catch (Domain.Exceptions.NotFoundException) { continue; }   // parent table gone: nothing to tell
                parentTables[parentTableId] = parent;
            }
            if (!fieldsByTable.TryGetValue(parentTableId, out var parentFields))
            {
                parentFields = await _fieldRepo.ListByTableAsync(parentTableId, ct);
                fieldsByTable[parentTableId] = parentFields;
            }

            result[reference.Id] = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields) is { } key
                ? new ReferenceKey(key.Fid, FieldValueTypes.Of(key))
                : new ReferenceKey(RecordIdFid, nameof(Domain.Enums.FieldTypeCode.Number));
        }
        return result;
    }

    /// <summary>Looks up each Lookup's / Summary's source field live (not a copy taken at creation),
    /// so a change to that field's display settings shows up straight away. One field-list read
    /// per source table, and none at all for a table without lookups or summaries.</summary>
    private async Task<IReadOnlyDictionary<long, string?>> ResolveSummarySourceSettingsAsync(
        IReadOnlyList<AppField> fields, Dictionary<long, IReadOnlyList<AppField>> fieldsByTable, CancellationToken ct)
    {
        var result = new Dictionary<long, string?>();

        foreach (var field in fields)
        {
            if (await SourceSettingsAsync(field, fieldsByTable, 0, ct) is { } settings)
                result[field.Id] = settings;
        }
        return result;
    }

    /// <summary>The settings of the field whose value <paramref name="field"/> shows, or null when it shows no single
    /// field's value: Count / Distinct Count / True-False / Combined Text summaries, one part of an address, a missing
    /// source, anything that isn't a Lookup or Summary. A source that is itself a Lookup or Summary passes on its own
    /// Behavior Settings (its <c>display</c>) laid over its source's.</summary>
    private async Task<string?> SourceSettingsAsync(
        AppField field, Dictionary<long, IReadOnlyList<AppField>> fieldsByTable, int depth, CancellationToken ct)
    {
        if (depth >= LookupChain.MaxLength || SourceOf(field) is not { } source) return null;

        if (!fieldsByTable.TryGetValue(source.TableId, out var sourceFields))
        {
            sourceFields = await _fieldRepo.ListByTableAsync(source.TableId, ct);
            fieldsByTable[source.TableId] = sourceFields;
        }
        if (sourceFields.FirstOrDefault(f => f.Fid == source.Fid) is not { } sourceField) return null;
        if (!SummaryLookupSources.IsLookup(sourceField) && sourceField.TypeCode != nameof(Domain.Enums.FieldTypeCode.Summary))
            return sourceField.Settings;

        var inherited = await SourceSettingsAsync(sourceField, fieldsByTable, depth + 1, ct);
        return Overlay(inherited, DisplayOf(sourceField));
    }

    private static (long TableId, int Fid)? SourceOf(AppField field)
    {
        if (SummaryLookupSources.IsLookup(field))
        {
            var s = FormulaTypeMap.ParseLookupSettings(field.Settings);
            // An Address sub-key is plain text — the Address field's settings don't describe it.
            return s is { SourceTableId: long t, SourceFid: int f } && string.IsNullOrWhiteSpace(s.SourceSubField) ? (t, f) : null;
        }
        if (field.TypeCode == nameof(Domain.Enums.FieldTypeCode.Summary))
        {
            var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
            return SummaryFunctions.Normalize(s?.Function) is SummaryFunctions.Sum or SummaryFunctions.Avg
                       or SummaryFunctions.Min or SummaryFunctions.Max
                   && s is { ChildTableId: long t, TargetFid: int f } && string.IsNullOrWhiteSpace(s.TargetSubField)
                ? (t, f)
                : null;
        }
        return null;
    }

    /// <summary>A Lookup's / Summary's own <c>display</c> object, or null.</summary>
    private static JsonObject? DisplayOf(AppField field)
    {
        if (string.IsNullOrWhiteSpace(field.Settings)) return null;
        try
        {
            if (JsonNode.Parse(field.Settings) is not JsonObject settings) return null;
            var key = settings.Select(p => p.Key)
                .FirstOrDefault(k => string.Equals(k, RelationshipDisplaySettingsPolicy.PropertyName, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : settings[key] as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    /// <summary><paramref name="inherited"/> settings JSON with every non-null member of <paramref name="own"/> laid over it.</summary>
    private static string? Overlay(string? inherited, JsonObject? own)
    {
        if (own is null || own.Count == 0) return inherited;
        JsonObject result;
        try { result = (string.IsNullOrWhiteSpace(inherited) ? null : JsonNode.Parse(inherited) as JsonObject) ?? new JsonObject(); }
        catch (JsonException) { result = new JsonObject(); }
        foreach (var (key, value) in own)
        {
            if (value is null) continue;
            foreach (var existing in result.Select(p => p.Key)
                         .Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && k != key).ToList())
                result.Remove(existing);
            result[key] = value.DeepClone();
        }
        return result.ToJsonString();
    }
}
