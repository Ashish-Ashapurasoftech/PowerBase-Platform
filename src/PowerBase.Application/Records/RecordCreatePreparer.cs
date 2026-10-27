using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;
using PowerBase.Formula;

namespace PowerBase.Application.Records;

/// <summary>
/// Everything a record create checks and prepares before it writes: the submitted field ids and the
/// user's permission to write them, Reference values resolved to parent row Ids, default values,
/// User picks resolved to internal ids, Required / Unique / format, the table's Custom Data Rule and
/// its Form Rules. Shared by <c>CreateRecordCommandHandler</c> (one record) and
/// <c>BulkCreateRecordsCommandHandler</c> (Grid Edit's new rows), so a record gets the same rules
/// whichever way it is added.
/// </summary>
public sealed class RecordCreatePreparer
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IQueryContext _queryContext;
    private readonly IUserRepository _userRepo;
    private readonly IAppRepository _appRepo;
    private readonly FormulaEngine _engine;
    private readonly IFormRuleRepository _formRuleRepo;
    private readonly IFormRepository _formRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IReportRepository? _reportRepo;
    private readonly ISnapshotCaptureService? _snapshots;

    public RecordCreatePreparer(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRelationshipRepository relRepo,
        IRolePermissionEnforcer enforcer,
        IQueryContext queryContext,
        IUserRepository userRepo,
        IAppRepository appRepo,
        FormulaEngine engine,
        IFormRuleRepository formRuleRepo,
        IFormRepository formRepo,
        IAppUserRepository appUserRepo,
        IReportRepository? reportRepo = null,
        ISnapshotCaptureService? snapshots = null)
    {
        _snapshots = snapshots;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _relRepo = relRepo;
        _enforcer = enforcer;
        _queryContext = queryContext;
        _userRepo = userRepo;
        _appRepo = appRepo;
        _engine = engine;
        _formRuleRepo = formRuleRepo;
        _formRepo = formRepo;
        _appUserRepo = appUserRepo;
        _reportRepo = reportRepo;
    }

    /// <summary>Rejects field ids that aren't on the table, computed fields (Formula, Lookup, Summary…),
    /// and fields this user may not write — and the whole write when the user can't add records.</summary>
    public async Task EnsureWritableAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> submittedFids, CancellationToken ct)
    {
        var tableFieldIds = new HashSet<long>(fields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value));
        var unknownIds = submittedFids.Where(k => !tableFieldIds.Contains(k)).ToList();
        if (unknownIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Unknown field IDs: {string.Join(", ", unknownIds)}"] });

        var computedIds = submittedFids
            .Where(k => fields.Any(f => f.Fid.HasValue && (long)f.Fid.Value == k && PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
            .ToList();
        if (computedIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Formula fields are read-only and cannot be set: {string.Join(", ", computedIds)}"] });

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (!access.CanAdd)
                throw new UnauthorizedActionException("You do not have permission to add records to this table.");
            var blocked = submittedFids.Where(k => !access.EditableFieldIds.Contains(k)).ToList();
            if (blocked.Count > 0)
                throw new UnauthorizedActionException("You do not have permission to write to one or more of the specified fields.");
        }
    }

    /// <summary>The values about to be stored for one new record: the submitted values, Reference values
    /// resolved to parent row Ids, defaults for fields that weren't submitted, and User picks resolved
    /// to internal user ids. Throws <see cref="ValidationException"/> for a Reference that points nowhere.</summary>
    public async Task<Dictionary<long, object?>> BuildEffectiveValuesAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> submitted, CancellationToken ct)
    {
        // Reference fields must point at an existing parent record; a value submitted as a
        // human key is resolved to the parent row Id here.
        var refOverrides = await ReferenceWriteValidator.ValidateAsync(fields, submitted, _tableRepo, _fieldRepo, _recordRepo, _relRepo, ct);

        // Inject default values for fields that were not submitted (e.g. hidden None-access fields).
        // This ensures required fields with defaults are always populated regardless of role restrictions.
        var effectiveValues = new Dictionary<long, object?>(submitted);

        // Apply translations from the validator (e.g. swapping custom keys for the physical Record ID#).
        foreach (var kvp in refOverrides)
            effectiveValues[kvp.Key] = kvp.Value;

        // Snapshot fields are filled by the system when the record is created; a client may not supply them
        // (unless editable after the snapshot). Checked before defaults, so a default can't masquerade as a supplied value.
        SnapshotWriteGuard.EnsureNotSuppliedOnCreate(fields, effectiveValues);
        foreach (var field in fields)
        {
            if (field.IsSystem || field.IsDeleted || !field.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(field.TypeCode)) continue;
            if (SnapshotWriteGuard.IsReadOnlySnapshot(field)) continue;
            if (effectiveValues.ContainsKey((long)field.Fid.Value)) continue;
            if (string.IsNullOrWhiteSpace(field.DefaultValue)) continue;

            var (apply, value) = await ResolveDefaultValueAsync(field, ct);
            if (apply)
                effectiveValues[(long)field.Fid.Value] = value;
        }

        // User/MultiUser values submitted from the record form's picker arrive as userPublicId
        // Guid(s) — resolve to the long id the column actually stores. See
        // UserFieldValueResolver's doc comment for why.
        await UserFieldValueResolver.ResolveAsync(_userRepo, fields, effectiveValues, ct);

        // Snapshot: capture each snapshot field's source value now, once — it stays as it is afterwards.
        if (_snapshots is not null)
            await _snapshots.ApplyOnCreateAsync(table, fields, effectiveValues, ct);

        return effectiveValues;
    }

    /// <summary>The app's configured Date Formatting, looked up (not hard-coded) so Date/DateTime text
    /// values sent by a client that bypasses the UI are validated against this specific app's format.</summary>
    public async Task<string> GetAppDateFormatAsync(AppTable table, CancellationToken ct)
    {
        var app = await _appRepo.GetByIdAsync(table.AppId, ct);
        return AppFormattingSettings.GetDateFormatString(app.Formatting);
    }

    /// <summary>Required / Unique / format, then the Custom Data Rule, then Form Rules — throws on the
    /// first that fails. Used by single-record create.</summary>
    public async Task ValidateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> effectiveValues, string dateFormat, CancellationToken ct)
    {
        // Field-level Required / Unique / Format constraints (Quickbase-style) — checked against
        // the final values about to be persisted, after defaults and reference-override resolution.
        await RecordConstraintValidator.ValidateAsync(table, fields, effectiveValues, _recordRepo, isCreate: true, excludeRecordId: null, ct, appDateFormat: dateFormat);

        // Custom Data Rule — the table's own formula-based save gate (Add/Update only, never
        // Delete or other tables' writes). Runs after the built-in constraints above so a plain
        // Required/Unique violation is reported first.
        await CustomDataRuleValidator.ValidateAsync(table, fields, effectiveValues, _tableRepo, _fieldRepo, _recordRepo, _engine, ct);

        // Form Rules — server-side mirror of the Add/Edit Record form's client-side rule
        // evaluation, so a client that bypasses the UI and calls this API directly still can't
        // violate a Require/Prevent Save form rule. No "before" values exist yet on create, so
        // 'changed'/'notChanged' conditions never match here.
        await FormRuleServerValidator.ValidateAsync(
            table, fields, effectiveValues, oldValuesByFid: null, _queryContext.UserId,
            _formRuleRepo, _formRepo, _tableRepo, _fieldRepo, _recordRepo, _userRepo, _appUserRepo, _engine, ct);
    }

    /// <summary>The same checks as <see cref="ValidateAsync"/>, but every failure is collected instead of
    /// throwing, so a multi-record write can report all of them before deciding to write anything. A
    /// Custom Data Rule message is returned with field id 0 (it belongs to the record, not to a field).
    /// <paramref name="reportId"/> / <paramref name="isGridEditSave"/> narrow Form Rules to the report's
    /// Grid Edit rules, exactly as a Grid Edit update does (see FormRuleServerValidator).</summary>
    public async Task<List<RecordConstraintViolation>> CollectViolationsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> effectiveValues, string dateFormat,
        CancellationToken ct, Guid? reportId = null, bool isGridEditSave = false)
    {
        var violations = await RecordConstraintValidator.CollectViolationsAsync(
            table, fields, effectiveValues, _recordRepo, isCreate: true, excludeRecordId: null, ct, appDateFormat: dateFormat);

        try
        {
            await CustomDataRuleValidator.ValidateAsync(table, fields, effectiveValues, _tableRepo, _fieldRepo, _recordRepo, _engine, ct);
        }
        catch (ValidationException e)
        {
            violations.AddRange(e.Errors.Values.SelectMany(messages => messages)
                .Select(message => new RecordConstraintViolation(Guid.Empty, 0, "CustomDataRule", message)));
        }

        violations.AddRange(await FormRuleServerValidator.CollectViolationsAsync(
            table, fields, effectiveValues, oldValuesByFid: null, _queryContext.UserId,
            _formRuleRepo, _formRepo, _tableRepo, _fieldRepo, _recordRepo, _userRepo, _appUserRepo, _engine, ct,
            reportRepo: _reportRepo, reportId: reportId, isGridEditSave: isGridEditSave));

        return violations;
    }

    /// <summary>Interprets a field's stored DefaultValue against its type — most types are a literal
    /// string copied verbatim, but Boolean/Range/User/MultiUser carry a structured encoding (see
    /// <see cref="PowerBase.Application.Fields.FieldGeneralSettingsCapability"/> for the shapes) that
    /// needs coercion/resolution before it can be written. Returns Apply=false when no default should be
    /// applied (malformed JSON, or a User/MultiUser default whose mode is "None").</summary>
    private async Task<(bool Apply, object? Value)> ResolveDefaultValueAsync(AppField field, CancellationToken ct)
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
                return await ResolveUserDefaultAsync(raw, isMulti: false, ct);

            case "MultiUser":
                return await ResolveUserDefaultAsync(raw, isMulti: true, ct);

            default:
                return (true, raw);
        }
    }

    private async Task<(bool Apply, object? Value)> ResolveUserDefaultAsync(string raw, bool isMulti, CancellationToken ct)
    {
        string? mode = null;
        string? userPublicId = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
                mode = m.GetString();
            if (doc.RootElement.TryGetProperty("userPublicId", out var u) && u.ValueKind == JsonValueKind.String)
                userPublicId = u.GetString();
        }
        catch (JsonException) { return (false, null); }

        // User/MultiUser columns store the plain long core.[User].Id, never a Guid PublicId —
        // confirmed against every read/filter path (see RunReportQueryHandler.
        // ResolveUserFieldValuesAsync's doc comment for the full chain of evidence). This
        // previously wrote .PublicId.ToString() for "CurrentUser" (a live bug — _queryContext.
        // UserId already IS that long id, no lookup needed) and the raw settings-configured Guid
        // as-is for "SpecificUser" (needs resolving via GetByPublicIdAsync first).
        string? resolvedId = mode switch
        {
            "CurrentUser" => _queryContext.UserId.ToString(),
            "SpecificUser" when Guid.TryParse(userPublicId, out var specificGuid) =>
                (await UserFieldValueResolver.TryResolveLongIdAsync(_userRepo, specificGuid, ct))?.ToString(),
            _ => null, // "None" or an unrecognized mode — apply no default.
        };

        if (resolvedId is null) return (false, null);
        return (true, isMulti ? JsonSerializer.Serialize(new[] { resolvedId }) : resolvedId);
    }
}
