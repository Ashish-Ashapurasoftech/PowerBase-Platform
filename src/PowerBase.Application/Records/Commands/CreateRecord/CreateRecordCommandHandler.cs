using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.CreateRecord;

public class CreateRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;
    //private readonly IFormulaDefaultResolver _formulaDefaults;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IUserRepository _userRepo;

    public CreateRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        //IFormulaDefaultResolver formulaDefaults,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IQueryContext queryContext,
        IUserRepository userRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
        //_formulaDefaults = formulaDefaults;
        _triggerInterceptor = triggerInterceptor;
        _uow = uow;
        _queryContext = queryContext;
        _userRepo = userRepo;
    }

    public async Task<RecordResult> HandleAsync(CreateRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var tableFieldIds = new HashSet<long>(fields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value));
        var unknownIds = command.FieldValues.Keys.Where(k => !tableFieldIds.Contains(k)).ToList();
        if (unknownIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Unknown field IDs: {string.Join(", ", unknownIds)}"] });

        var computedIds = command.FieldValues.Keys
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
            var blocked = command.FieldValues.Keys.Where(k => !access.EditableFieldIds.Contains(k)).ToList();
            if (blocked.Count > 0)
                throw new UnauthorizedActionException("You do not have permission to write to one or more of the specified fields.");
        }

        // Reference fields must point at an existing parent record.
        var refOverrides = await ReferenceWriteValidator.ValidateAsync(fields, command.FieldValues, _tableRepo, _fieldRepo, _recordRepo, ct);

        // Inject default values for fields that were not submitted (e.g. hidden None-access fields).
        // This ensures required fields with defaults are always populated regardless of role restrictions.
        var effectiveValues = new Dictionary<long, object?>(command.FieldValues);

        // Apply translations from the validator (e.g. swapping custom keys for the physical Record ID#).
        foreach (var kvp in refOverrides)
            effectiveValues[kvp.Key] = kvp.Value;
        foreach (var field in fields)
        {
            if (field.IsSystem || field.IsDeleted || !field.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(field.TypeCode)) continue;
            if (effectiveValues.ContainsKey((long)field.Fid.Value)) continue;
            if (string.IsNullOrWhiteSpace(field.DefaultValue)) continue;

            var (apply, value) = await ResolveDefaultValueAsync(field, ct);
            if (apply)
                effectiveValues[(long)field.Fid.Value] = value;
        }

        // Field-level Required / Unique constraints (Quickbase-style) — checked against the final
        // values about to be persisted, after defaults and reference-override resolution.
        await RecordConstraintValidator.ValidateAsync(table, fields, effectiveValues, _recordRepo, isCreate: true, excludeRecordId: null, ct);

        Guid publicId;
        await _uow.BeginAsync(ct);
        try
        {
            publicId = await _recordRepo.CreateAsync(table, fields, effectiveValues, _uow.Transaction, ct);

            var recordId = await _recordRepo.GetRecordIdByPublicIdAsync(table, publicId, _uow.Transaction, ct);
            effectiveValues[3] = recordId;

            await _auditRepo.LogActivityAsync(
                AuditActions.Created, AuditEntityTypes.Record, publicId.ToString(), $"Record added in {table.Name} with ID {publicId}", appId: table.AppId, ct: ct);

            await _tableRepo.IncrementRecordCountAsync(table.Id, ct);

            await _triggerInterceptor.InterceptAsync(table, fields, publicId, effectiveValues, "record-added", ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        var fieldData = new Dictionary<string, object?>();
        foreach (var field in fields.Where(f => f.Fid.HasValue && effectiveValues.ContainsKey((long)f.Fid.Value)))
            fieldData[field.Fid!.Value.ToString()] = effectiveValues[(long)field.Fid.Value];

        return new RecordResult
        {
            Id = publicId,
            CreatedOn = DateTime.UtcNow,
            ModifiedOn = null,
            Fields = fieldData,
        };
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

        string? resolvedId = mode switch
        {
            "CurrentUser" => (await _userRepo.GetByIdAsync(_queryContext.UserId, ct)).PublicId.ToString(),
            "SpecificUser" => userPublicId,
            _ => null, // "None" or an unrecognized mode — apply no default.
        };

        if (resolvedId is null) return (false, null);
        return (true, isMulti ? JsonSerializer.Serialize(new[] { resolvedId }) : resolvedId);
    }
}
