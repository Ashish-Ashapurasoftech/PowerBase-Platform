using System.Text.Json;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Reports;

/// <summary>
/// Turns a report's <see cref="ReportFormula"/>s into in-memory <see cref="AppField"/>s (never saved) that are added to the
/// table's field list for the duration of one run/export. They have a reserved high Fid and a Formula_* type code, so the existing
/// compute-on-read machinery treats them exactly like a Formula field: <c>FormulaProjector</c> evaluates them per row,
/// <c>FormulaFilterSorter</c> filters/sorts on them in memory, and <c>InMemorySummarizer</c> groups/aggregates over them.
/// </summary>
public static class ReportFormulaFields
{
    /// <summary>A report can define at most this many formulas.</summary>
    public const int MaxFormulas = 3;

    /// <summary>The first of the reserved ids (FirstId .. FirstId + MaxFormulas - 1) a report formula's virtual field uses. Far above
    /// any real field's Fid, and positive on purpose: negative computed-value keys already mean something else (an Action
    /// Button's label/color), see RecordResult.FromRow.</summary>
    public const long FirstId = 2_000_000_001;

    /// <summary>The Formula_* type codes a report formula may return (the "Formula - …" dropdown).</summary>
    public static readonly string[] AllowedTypeCodes =
    [
        "Formula_Text", "Formula_RichText", "Formula_Number", "Formula_Date", "Formula_DateTime", "Formula_Time",
        "Formula_Duration", "Formula_Bool", "Formula_Phone", "Formula_Email", "Formula_Url", "Formula_User", "Formula_ListUser",
    ];

    private static readonly string[] AllowedDisplayAs = [NumericDisplayAs.Number, NumericDisplayAs.Currency, NumericDisplayAs.Percent, NumericDisplayAs.Rating];

    public static bool IsAllowedTypeCode(string? typeCode) =>
        AllowedTypeCodes.Contains(typeCode ?? string.Empty, StringComparer.Ordinal);

    public static bool IsAllowedDisplayAs(string? displayAs) =>
        string.IsNullOrEmpty(displayAs) || AllowedDisplayAs.Contains(displayAs, StringComparer.Ordinal);

    public static bool IsReportFormulaFid(long fieldId) => fieldId >= FirstId && fieldId < FirstId + MaxFormulas;

    /// <summary>The virtual fields for the formulas that have an expression; blank ones are skipped.</summary>
    public static IReadOnlyList<AppField> Build(IEnumerable<ReportFormula>? formulas, long tableId)
    {
        if (formulas is null) return [];
        return formulas
            .Where(f => IsReportFormulaFid(f.Id) && IsAllowedTypeCode(f.TypeCode) && !string.IsNullOrWhiteSpace(f.Expression))
            .GroupBy(f => f.Id).Select(g => g.First())
            .Select(f => new AppField
            {
                Id = f.Id,
                Fid = (int)f.Id,
                AppTableId = tableId,
                TypeCode = f.TypeCode,
                Name = f.Name,
                Label = f.Name,
                Settings = BuildSettings(f),
                IsReportable = true,
                IsSortable = true,
                IsFilterable = true,
                IsSearchable = false,
                IsAuditable = false,
            })
            .ToList();
    }

    /// <summary>The table's fields followed by the report's virtual formula fields (the table's own list when there are none).</summary>
    public static IReadOnlyList<AppField> WithReportFormulas(IReadOnlyList<AppField> tableFields, ReportDefinition definition, long tableId)
    {
        var virtualFields = Build(definition.ReportFormulas, tableId);
        return virtualFields.Count == 0 ? tableFields : [.. tableFields, .. virtualFields];
    }

    /// <summary>A copy of <paramref name="access"/> in which the virtual fields are visible too: they are computed from the
    /// report's own definition, not a column a role grants or hides, so every viewer of the report sees them.</summary>
    public static PowerBase.Application.Common.Interfaces.TableAccessContext WithVisible(
        PowerBase.Application.Common.Interfaces.TableAccessContext access, IReadOnlyList<AppField> virtualFields) =>
        virtualFields.Count == 0 ? access : new PowerBase.Application.Common.Interfaces.TableAccessContext
        {
            Unrestricted = access.Unrestricted,
            ViewScope = access.ViewScope,
            ModifyScope = access.ModifyScope,
            CanAdd = access.CanAdd,
            CanDelete = access.CanDelete,
            VisibleFields = [.. access.VisibleFields, .. virtualFields],
            EditableFieldIds = access.EditableFieldIds,
            ViewFilter = access.ViewFilter,
            ModifyFilter = access.ModifyFilter,
            RestrictToCreatedBy = access.RestrictToCreatedBy,
        };

    /// <summary>The settings JSON a real Formula_* field of the same type would hold, so FormulaTypeMap.ExpressionAndType
    /// and the display code read it unchanged (Formula_Url keeps its expression under "Template").</summary>
    internal static string BuildSettings(ReportFormula f)
    {
        var settings = new Dictionary<string, object?>();
        if (f.TypeCode == "Formula_Url")
        {
            settings["Template"] = f.Expression;
        }
        else
        {
            settings["Expression"] = f.Expression;
            if (f.TypeCode == "Formula_Number" && !string.IsNullOrEmpty(f.DisplayAs) && f.DisplayAs != NumericDisplayAs.Number)
                settings["DisplayAs"] = f.DisplayAs;
        }
        return JsonSerializer.Serialize(settings);
    }
}
