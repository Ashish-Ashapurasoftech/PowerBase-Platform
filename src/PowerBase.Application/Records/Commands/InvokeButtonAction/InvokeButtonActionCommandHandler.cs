using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Records.Commands.InvokeButtonAction;

/// <summary>
/// Executes an Action Button click: resolves its configured gates and writes (Field-Type
/// spec §"Shared Anatomy"), applies them under the Rule-1 privileged-write exception, and
/// returns the updated field values so the client can apply them in place (Rule 2 — no
/// page refresh).
/// </summary>
public sealed class InvokeButtonActionCommandHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IRecordWriteService _writeService;
    private readonly IActionButtonValueResolver _valueResolver;
    private readonly IQueryContext _queryContext;

    public InvokeButtonActionCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IRecordWriteService writeService,
        IActionButtonValueResolver valueResolver,
        IQueryContext queryContext)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _writeService = writeService;
        _valueResolver = valueResolver;
        _queryContext = queryContext;
    }

    public async Task<InvokeButtonActionResult> HandleAsync(InvokeButtonActionCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var buttonField = fields.FirstOrDefault(f => f.Fid == command.ButtonFid)
            ?? throw new NotFoundException("Field", command.ButtonFid);
        if (!PhysicalNaming.IsActionButtonTypeCode(buttonField.TypeCode))
            throw new BadRequestException("NOT_AN_ACTION_BUTTON", "The specified field is not an Action Button.");

        var settings = ParseSettings(buttonField.Settings)
            ?? throw new BadRequestException("BUTTON_NOT_CONFIGURED", "This button has not been configured.");

        // Current record row — drives gates, field-kind ValueSources, and confirms the record exists.
        var row = await _recordRepo.GetByPublicIdAsync(table, fields, command.RecordPublicId, ct);

        await RunGatesAsync(settings, table, fields, row, command, ct);

        var writes = await ResolveWritesAsync(settings, table, fields, row, command, ct);
        if (writes.Count == 0)
            throw new BadRequestException("NOTHING_TO_WRITE", "This button has no configured fields to write.");

        // Defense in depth: never allow a button to target a system or computed field, even
        // if settings were hand-crafted to point at one.
        var invalidTargets = fields
            .Where(f => f.Fid.HasValue && writes.ContainsKey((long)f.Fid.Value) && (f.IsSystem || PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
            .Select(f => f.Fid!.Value)
            .ToList();
        if (invalidTargets.Count > 0)
            throw new BadRequestException("INVALID_TARGET_FIELD",
                $"This button cannot write to system/computed fields: {string.Join(", ", invalidTargets)}");

        // Rule 1 — privileged-write exception: record visibility/ownership still enforced,
        // but field-edit permission is bypassed for exactly the button's configured targets.
        await _enforcer.EnsureButtonWriteAllowedAsync(table, fields, command.RecordPublicId, writes.Keys.ToHashSet(), ct);

        var label = buttonField.Label ?? buttonField.Name;
        var effective = await _writeService.ApplyAsync(
            table, fields, command.RecordPublicId, writes,
            AuditActions.ButtonInvoked, $"Button '{label}' invoked on {table.Name}", ct);

        var redirect = settings.Redirect is not null
            ? (await _valueResolver.ResolveAsync(settings.Redirect, table, fields, row, FormulaType.Text, ct))?.ToString()
            : null;

        return new InvokeButtonActionResult { UpdatedFields = effective, Redirect = string.IsNullOrWhiteSpace(redirect) ? null : redirect };
    }

    private async Task RunGatesAsync(
        ActionButtonSettings settings,
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<string, object?> row,
        InvokeButtonActionCommand command,
        CancellationToken ct)
    {
        if (settings.BoolGateFid is int boolFid)
        {
            var raw = row.TryGetValue(PhysicalNaming.ColumnName(boolFid), out var bv) ? bv : null;
            if (!IsTruthy(raw))
                throw new ActionGateException("This action is not currently available.");
        }

        // Link Expiration is enforced here — server-side, at the moment of click — regardless
        // of a stale ClientNow or whatever the client UI still displays (spec July-2 clarification).
        // Fail CLOSED: a configured expiration whose Start cannot be resolved to a real
        // timestamp is a misconfiguration, not "no expiration" — silently skipping the
        // check here would let an expiration setting that looks enabled never actually expire.
        if (settings.LinkExpiration is { Minutes: int minutes } exp)
        {
            var startRaw = await _valueResolver.ResolveAsync(exp.Start, table, fields, row, FormulaType.DateTime, ct);
            if (ParseDateTime(startRaw) is not DateTime start)
                throw new LinkExpiredException("This button's expiration start time is not configured correctly.");
            if (DateTime.UtcNow > start.AddMinutes(minutes))
                throw new LinkExpiredException();
        }

        if (settings.PasswordGate is not null)
        {
            var expectedRaw = await _valueResolver.ResolveAsync(settings.PasswordGate, table, fields, row, FormulaType.Text, ct);
            var expected = expectedRaw?.ToString();
            // A PasswordGate that resolves to blank/unset is treated as "no gate configured"
            // — not "the password must be blank". This matters for two reasons: (1) it's
            // what an admin means by leaving the box empty while wiring up the button, and
            // (2) it makes the same rule apply uniformly whether PasswordGate is a 'data'
            // kind (checked client-side too, see ActionButtonComponent.needsCaptureDialog)
            // or a 'field'/'formula' kind that can only be resolved here.
            if (!string.IsNullOrEmpty(expected)
                && !string.Equals(expected, command.Password ?? string.Empty, StringComparison.Ordinal))
                throw new ActionGateException("Incorrect password.");
        }
    }

    private async Task<Dictionary<long, object?>> ResolveWritesAsync(
        ActionButtonSettings settings,
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<string, object?> row,
        InvokeButtonActionCommand command,
        CancellationToken ct)
    {
        var writes = new Dictionary<long, object?>();
        var fieldsByFid = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);

        switch (settings.Variant)
        {
            case ActionButtonVariants.Signature:
            case ActionButtonVariants.File:
                if (settings.CaptureFid is int fileFid)
                {
                    if (string.IsNullOrWhiteSpace(command.CapturedFileRef))
                        throw new BadRequestException("CAPTURE_REQUIRED", "A file/signature must be captured before this button can be used.");
                    writes[fileFid] = command.CapturedFileRef;
                }
                break;

            case ActionButtonVariants.Prompt:
                if (settings.CaptureFid is int promptFid)
                {
                    if (string.IsNullOrWhiteSpace(command.PromptValue))
                        throw new BadRequestException("PROMPT_VALUE_REQUIRED", "A value is required to use this button.");
                    if (settings.PromptType == PromptTypes.EnterData
                        && settings.PromptOptions is { Length: > 0 } options
                        && !options.Contains(command.PromptValue, StringComparer.Ordinal))
                        throw new BadRequestException("PROMPT_VALUE_INVALID", "The submitted value is not one of the configured options.");
                    writes[promptFid] = CoerceForField(fieldsByFid, promptFid, command.PromptValue);
                }
                break;

            case ActionButtonVariants.Data:
            default:
                break; // no capture UI — only AddData below
        }

        if (settings.AddData is { Length: > 0 })
        {
            foreach (var item in settings.AddData)
            {
                if (item.TargetFid is not int targetFid) continue;
                var raw = await _valueResolver.ResolveAsync(item.Value, table, fields, row, FormulaType.Text, ct);
                writes[targetFid] = CoerceForField(fieldsByFid, targetFid, raw);
            }
        }

        if (settings.TimestampFid is int tsFid)
            writes[tsFid] = DateTime.UtcNow;

        if (settings.LocationCapture is { TargetFid: int locFid } loc
            && command.GeoLat is double lat && command.GeoLng is double lng)
        {
            if (!string.IsNullOrWhiteSpace(loc.RestrictToState)
                && !string.Equals(loc.RestrictToState, command.GeoState, StringComparison.OrdinalIgnoreCase))
                throw new ActionGateException("Location is outside the allowed region for this action.");
            writes[locFid] = lat.ToString(CultureInfo.InvariantCulture) + "," + lng.ToString(CultureInfo.InvariantCulture);
        }

        if (settings.IpCaptureFid is int ipFid)
            writes[ipFid] = _queryContext.IpAddress;

        return writes;
    }

    private static bool IsTruthy(object? raw)
    {
        if (raw is null) return false;
        if (raw is bool b) return b;
        try { return Convert.ToBoolean(raw, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    /// <summary>
    /// Coerces a resolved write value (a raw string when it came from a 'data' or 'formula'
    /// ValueSource, or the free-text Prompt answer) to the target field's actual CLR type
    /// before it's written. Without this, a Boolean target field ends up storing/returning
    /// the literal string an admin typed (e.g. "True") instead of a real boolean — which
    /// then renders inconsistently, since different UI surfaces compare it against
    /// different casings/representations of "true". A field's <see cref="RecordWriteService"/>
    /// write and the value echoed back in <see cref="InvokeButtonActionResult.UpdatedFields"/>
    /// must agree with what actually lands in the column.
    /// </summary>
    private static object? CoerceForField(IReadOnlyDictionary<long, AppField> fieldsByFid, int fid, object? raw)
    {
        if (raw is null) return null;
        if (!fieldsByFid.TryGetValue(fid, out var field)) return raw;

        return field.TypeCode switch
        {
            "Boolean" => CoerceBoolean(raw),
            "Number" or "Currency" or "Percent" or "Rating" or "Duration" => CoerceDecimal(raw),
            _ => raw,
        };
    }

    private static object? CoerceBoolean(object? raw)
    {
        if (raw is bool b) return b;
        var s = raw?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || s == "1";
    }

    private static object? CoerceDecimal(object? raw)
    {
        if (raw is decimal or double or float or int or long) return raw;
        var s = raw?.ToString();
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : raw;
    }

    /// <summary>
    /// Parses a Link Expiration Start value and normalizes it to UTC so it can be compared
    /// against <see cref="DateTime.UtcNow"/>.
    ///
    /// Normalizing is essential, not cosmetic: a value carrying an offset (or parsed as
    /// server-local) would otherwise be compared numerically against a UTC "now", so on a
    /// server in, say, UTC+5:30 an already-expired button would read as hours in the future
    /// and never expire. A bare timestamp with no offset is deliberately interpreted as UTC
    /// rather than server-local, so behavior does not silently change with the server's
    /// time zone.
    /// </summary>
    private static DateTime? ParseDateTime(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;

            case DateTime dt:
                return dt.Kind switch
                {
                    DateTimeKind.Utc => dt,
                    DateTimeKind.Local => dt.ToUniversalTime(),
                    // Unspecified (e.g. straight out of SQL Server) — treat as UTC.
                    _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                };

            case DateTimeOffset dto:
                return dto.UtcDateTime;

            case string s when !string.IsNullOrWhiteSpace(s):
                // An explicit offset / 'Z' present → honour it, then convert to UTC.
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedOffset))
                    return parsedOffset.UtcDateTime;

                // No offset info → assume the author meant UTC.
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

                return null;

            default:
                return null;
        }
    }

    private static ActionButtonSettings? ParseSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<ActionButtonSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }
}
