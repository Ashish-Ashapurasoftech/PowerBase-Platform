using System.Text.Json;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.ConnectedTables.Mapping;

/// <summary>How one source field lands in the destination. <see cref="Connectable"/> false means the field is offered
/// greyed out with <see cref="Reason"/>.</summary>
public sealed record ConnectedFieldPlan(bool Connectable, string? DestinationTypeCode, string Conversion, string? Settings, string? Reason)
{
    public static ConnectedFieldPlan No(string reason) => new(false, null, ConnectedConversions.Identity, null, reason);
}

/// <summary>Maps source field types to destination field types. Within PowerBase most types stay what they are; computed
/// fields become plain fields of their result type; user fields from another realm become email text.</summary>
public static class ConnectedTypeMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Types stored as they are, with their settings (choices, formats) copied.</summary>
    private static readonly HashSet<string> SameType = new(StringComparer.Ordinal)
    {
        "Text", "TextMultiLine", "RichText", "SingleSelect", "MultiSelect", "Number", "Currency", "Percent", "Rating",
        "Date", "DateTime", "Time", "Duration", "DateRange", "NumericRange", "Boolean", "Email", "Phone", "Url", "Address",
    };

    private static readonly Dictionary<string, string> FormulaResult = new(StringComparer.Ordinal)
    {
        ["Formula_Text"] = "Text", ["Formula_Number"] = "Number", ["Formula_Date"] = "Date", ["Formula_DateTime"] = "DateTime",
        ["Formula_Duration"] = "Duration", ["Formula_Bool"] = "Boolean", ["Formula_User"] = "User", ["Formula_Time"] = "Time",
        ["Formula_Phone"] = "Phone", ["Formula_Email"] = "Email", ["Formula_RichText"] = "RichText", ["Formula_ListUser"] = "MultiUser",
        ["Formula_MultiSelect"] = "Text", ["Formula_Url"] = "Url", ["FormulaUrl"] = "Url",
    };

    private static readonly Dictionary<string, string> GenericFormulaResult = new(StringComparer.OrdinalIgnoreCase)
    {
        [FormulaResultTypes.Text] = "Text", [FormulaResultTypes.Number] = "Number", [FormulaResultTypes.Date] = "Date",
        [FormulaResultTypes.DateTime] = "DateTime", [FormulaResultTypes.Duration] = "Duration", [FormulaResultTypes.Bool] = "Boolean",
        [FormulaResultTypes.User] = "User",
    };

    public static ConnectedFieldPlan Plan(AppField source, bool crossTenant)
    {
        var type = source.TypeCode;

        // Built-in fields: Record ID#, Date Created/Modified, Record Owner, Last Modified By.
        if (source.IsSystem)
            return source.Fid switch
            {
                ConnectedSystemFids.RecordId => new(true, "Number", ConnectedConversions.Identity, null, null),
                ConnectedSystemFids.DateCreated or ConnectedSystemFids.DateModified => new(true, "DateTime", ConnectedConversions.Identity, null, null),
                ConnectedSystemFids.RecordOwner or ConnectedSystemFids.LastModifiedBy => User("User", crossTenant, ConnectedConversions.Identity),
                _ => ConnectedFieldPlan.No("This built-in field can't be connected."),
            };

        if (type == "File") return ConnectedFieldPlan.No("File attachments can't be connected.");
        if (type == "ReportLink") return ConnectedFieldPlan.No("Report links can't be connected.");
        if (PhysicalNaming.IsActionButtonTypeCode(type)) return ConnectedFieldPlan.No("Buttons can't be connected.");

        if (SameType.Contains(type)) return new(true, type, ConnectedConversions.Identity, source.Settings, null);
        if (type is "User" or "MultiUser") return User(type, crossTenant, ConnectedConversions.Identity);
        if (type == "Reference") return new(true, "Number", ConnectedConversions.ReferenceToNumber, null, null);

        if (FormulaResult.TryGetValue(type, out var formulaType))
            return formulaType is "User" or "MultiUser" ? User(formulaType, crossTenant, ConnectedConversions.Computed)
                : new(true, formulaType, ConnectedConversions.Computed, null, null);

        if (type == "Formula")
        {
            var resultType = Read<FormulaSettings>(source.Settings)?.ResultType;
            if (resultType is not null && GenericFormulaResult.TryGetValue(resultType, out var mapped))
                return mapped == "User" ? User("User", crossTenant, ConnectedConversions.Computed) : new(true, mapped, ConnectedConversions.Computed, null, null);
            return new(true, "Text", ConnectedConversions.Computed, null, null);
        }

        if (type == "Lookup")
        {
            var sourceType = Read<LookupSettings>(source.Settings)?.SourceTypeCode;
            if (sourceType is not null && SameType.Contains(sourceType) && sourceType is not ("SingleSelect" or "MultiSelect" or "DateRange" or "NumericRange" or "Address"))
                return new(true, sourceType, ConnectedConversions.Computed, null, null);
            if (sourceType is "User" or "MultiUser") return User(sourceType, crossTenant, ConnectedConversions.Computed);
            return new(true, "Text", ConnectedConversions.Computed, null, null);
        }

        if (type == "Summary")
        {
            var summary = Read<SummarySettings>(source.Settings);
            return SummaryFunctions.Normalize(summary?.Function) switch
            {
                SummaryFunctions.CombinedText => new(true, "Text", ConnectedConversions.Computed, null, null),
                SummaryFunctions.Exists => new(true, "Boolean", ConnectedConversions.Computed, null, null),
                SummaryFunctions.Min or SummaryFunctions.Max when summary?.TargetTypeCode is "Date" or "DateTime" =>
                    new(true, summary.TargetTypeCode!, ConnectedConversions.Computed, null, null),
                _ => new(true, "Number", ConnectedConversions.Computed, null, null),
            };
        }

        // Anything else with a stored value is copied as text rather than refused.
        return PhysicalNaming.IsComputedTypeCode(type)
            ? ConnectedFieldPlan.No("This kind of field can't be connected.")
            : new(true, "Text", ConnectedConversions.ToText, null, null);
    }

    /// <summary>Whether a destination field of <paramref name="destinationType"/> can still take the source field's values
    /// (the destination type may have been changed by hand to a compatible one).</summary>
    public static bool IsCompatible(ConnectedFieldPlan plan, string destinationType)
    {
        if (!plan.Connectable) return false;
        if (string.Equals(plan.DestinationTypeCode, destinationType, StringComparison.Ordinal)) return true;
        return Family(plan.DestinationTypeCode) is { } a && a == Family(destinationType);
    }

    private static string? Family(string? type) => type switch
    {
        "Text" or "TextMultiLine" or "RichText" or "Email" or "Phone" or "Url" => "text",
        "Number" or "Currency" or "Percent" or "Rating" => "number",
        "Date" or "DateTime" => "date",
        _ => null,
    };

    private static ConnectedFieldPlan User(string type, bool crossTenant, string conversion) =>
        crossTenant
            ? new(true, type == "MultiUser" ? "TextMultiLine" : "Email", ConnectedConversions.UserToEmail, null, null)
            : new(true, type, conversion, null, null);

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
