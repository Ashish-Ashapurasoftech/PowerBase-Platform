using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.ValueObjects;
using PowerBase.Formula;

namespace PowerBase.Application.Pipelines;

/// <summary>The rules a record has to pass before the Create Record step writes it: the same defaults, Required / Unique / format
/// constraints and Custom Data Rule that <c>CreateRecordCommandHandler</c> applies to a record added by hand. A pipeline writes
/// through the repository directly, so without this a Required field could be left empty and a Unique value repeated.</summary>
internal static class PipelineCreateRecordRules
{
    /// <summary>Fills the field default for every field the step did not map (a mapping that resolved to blank is not written).
    /// Without this a Required field that has a default would be reported empty although the form would have filled it.</summary>
    public static async Task ApplyDefaultsAsync(
        IReadOnlyList<AppField> fields, IDictionary<long, object?> values, long currentUserId, IServiceProvider services, CancellationToken ct)
    {
        foreach (var field in fields)
        {
            if (field.IsSystem || field.IsDeleted || !field.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(field.TypeCode)) continue;
            if (values.ContainsKey(field.Fid.Value)) continue;
            if (string.IsNullOrWhiteSpace(field.DefaultValue)) continue;
            var (apply, value) = await ResolveDefaultAsync(field, currentUserId, services, ct);
            if (apply) values[field.Fid.Value] = value;
        }
    }

    /// <summary>Required / Unique / format constraints, then the table's Custom Data Rule. Throws a <c>ValidationException</c> naming
    /// the field. Run inside the step's transaction so the Unique check sees what the transaction has already written.</summary>
    public static async Task ValidateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values,
        IRecordRepository records, IAppTableRepository tables, IAppFieldRepository fieldRepo,
        IServiceProvider services, System.Data.IDbTransaction? transaction, CancellationToken ct)
    {
        string? dateFormat = null;
        if (services.GetService<IAppRepository>() is { } apps)
            dateFormat = AppFormattingSettings.GetDateFormatString((await apps.GetByIdAsync(table.AppId, ct)).Formatting);

        await RecordConstraintValidator.ValidateAsync(table, fields, values, records, isCreate: true, excludeRecordId: null, ct,
            appDateFormat: dateFormat, transaction: transaction);

        if (services.GetService<FormulaEngine>() is { } engine)
            await CustomDataRuleValidator.ValidateAsync(table, fields, values, tables, fieldRepo, records, engine, ct);
    }

    private static async Task<(bool Apply, object? Value)> ResolveDefaultAsync(AppField field, long currentUserId, IServiceProvider services, CancellationToken ct)
    {
        var raw = field.DefaultValue!;
        switch (field.TypeCode)
        {
            case "Boolean":
                return (true, string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase));
            case "NumericRange":
            case "DateRange":
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    return (true, doc.RootElement.Clone());
                }
                catch (JsonException) { return (false, null); }
            case "User":
                return await ResolveUserDefaultAsync(raw, isMulti: false, currentUserId, services, ct);
            case "MultiUser":
                return await ResolveUserDefaultAsync(raw, isMulti: true, currentUserId, services, ct);
            default:
                return (true, raw);
        }
    }

    // User / MultiUser columns store the plain core.[User].Id: "CurrentUser" is the acting user's id, "SpecificUser" a configured user's id.
    private static async Task<(bool Apply, object? Value)> ResolveUserDefaultAsync(string raw, bool isMulti, long currentUserId, IServiceProvider services, CancellationToken ct)
    {
        string? mode = null, userPublicId = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String) mode = m.GetString();
            if (doc.RootElement.TryGetProperty("userPublicId", out var u) && u.ValueKind == JsonValueKind.String) userPublicId = u.GetString();
        }
        catch (JsonException) { return (false, null); }

        string? resolvedId = mode switch
        {
            "CurrentUser" => currentUserId.ToString(),
            "SpecificUser" when Guid.TryParse(userPublicId, out var specific) && services.GetService<IUserRepository>() is { } users =>
                (await UserFieldValueResolver.TryResolveLongIdAsync(users, specific, ct))?.ToString(),
            _ => null,
        };
        if (resolvedId is null) return (false, null);
        return (true, isMulti ? JsonSerializer.Serialize(new[] { resolvedId }) : resolvedId);
    }
}
