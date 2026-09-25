using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Tables.Queries.GetTable;

/// <param name="SummarySourceSettings">Summary field Id → the current Settings JSON of the child
/// field it summarizes, for Min/Max summaries only (the one kind whose value IS a value of that
/// field, e.g. the latest Due Date). Lets the client render it in the source field's own display
/// format (month name, show time, …) — the summary's own Settings carry no display settings.</param>
public record GetTableResult(
    AppTable Table,
    IReadOnlyList<AppField> Fields,
    IReadOnlyDictionary<long, string?> SummarySourceSettings);

public class GetTableQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public GetTableQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task<GetTableResult> HandleAsync(GetTableQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.PublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var summarySourceSettings = await ResolveSummarySourceSettingsAsync(fields, ct);
        return new GetTableResult(table, fields, summarySourceSettings);
    }

    /// <summary>Looks up each Min/Max summary's source field live (not a copy taken at creation),
    /// so a change to that field's display settings shows up straight away. One field-list read
    /// per child table, and none at all for a table without Min/Max summaries.</summary>
    private async Task<IReadOnlyDictionary<long, string?>> ResolveSummarySourceSettingsAsync(
        IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        var result = new Dictionary<long, string?>();
        var childFieldsByTable = new Dictionary<long, IReadOnlyList<AppField>>();

        foreach (var field in fields.Where(f => f.TypeCode == nameof(Domain.Enums.FieldTypeCode.Summary)))
        {
            var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
            if (SummaryFunctions.Normalize(s?.Function) is not (SummaryFunctions.Min or SummaryFunctions.Max)
                || s is not { TargetFid: int targetFid, ChildTableId: long childId })
                continue;
            // An Address sub-key is plain text — the Address field's settings don't describe it.
            if (!string.IsNullOrWhiteSpace(s.TargetSubField)) continue;

            if (!childFieldsByTable.TryGetValue(childId, out var childFields))
            {
                childFields = await _fieldRepo.ListByTableAsync(childId, ct);
                childFieldsByTable[childId] = childFields;
            }
            if (childFields.FirstOrDefault(f => f.Fid == targetFid) is { } source)
                result[field.Id] = source.Settings;
        }
        return result;
    }
}
