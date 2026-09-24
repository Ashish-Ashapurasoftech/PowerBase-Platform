using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using System.Text.Json;

namespace PowerBase.Application.Records;

/// <summary>
/// Optional write capability for file reservation and revision-aware mutations. Keeping this
/// separate preserves the established <see cref="IRecordWriteService"/> contract.
/// </summary>
public interface IFileRecordWriteService
{
    Task<IReadOnlyDictionary<long, object?>> ApplyFileWriteAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string auditAction,
        string entityTitle,
        CancellationToken ct = default,
        System.Data.IDbTransaction? transaction = null,
        bool suppressInterception = false,
        Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null,
        IReadOnlyDictionary<string, object?>? existingRecord = null,
        bool bypassFileReservation = false);
}

/// <summary>
/// Adds file-specific rules while delegating all established record-write behaviour unchanged.
/// </summary>
public sealed class FileRecordWriteService : IRecordWriteService, IFileRecordWriteService
{
    private readonly RecordWriteService _inner;
    private readonly IRecordRepository _recordRepository;
    private readonly IQueryContext _queryContext;

    public FileRecordWriteService(
        RecordWriteService inner,
        IRecordRepository recordRepository,
        IQueryContext queryContext)
    {
        _inner = inner;
        _recordRepository = recordRepository;
        _queryContext = queryContext;
    }

    public Task<IReadOnlyDictionary<long, object?>> ApplyAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string auditAction,
        string entityTitle,
        CancellationToken ct = default,
        System.Data.IDbTransaction? transaction = null,
        bool suppressInterception = false,
        Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null,
        IReadOnlyDictionary<string, object?>? existingRecord = null)
        => _inner.ApplyAsync(table, fields, recordPublicId, fieldValues, auditAction, entityTitle,
            ct, transaction, suppressInterception, onIndexMessageCreated, existingRecord);

    public async Task<IReadOnlyDictionary<long, object?>> ApplyFileWriteAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string auditAction,
        string entityTitle,
        CancellationToken ct = default,
        System.Data.IDbTransaction? transaction = null,
        bool suppressInterception = false,
        Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null,
        IReadOnlyDictionary<string, object?>? existingRecord = null,
        bool bypassFileReservation = false)
    {
        var oldRecord = existingRecord ??
            await _recordRepository.GetByPublicIdAsync(table, fields, recordPublicId, ct);
        var effectiveValues = new Dictionary<long, object?>(fieldValues);

        if (!bypassFileReservation)
        {
            foreach (var field in fields.Where(field => field.Fid.HasValue &&
                         string.Equals(field.TypeCode, "File", StringComparison.OrdinalIgnoreCase)))
            {
                if (!effectiveValues.TryGetValue(field.Fid!.Value, out var newValue)) continue;
                oldRecord.TryGetValue(PhysicalNaming.GetPhysicalColumnName(field), out var oldValue);
                var reservation = FileReservationContract.Read(oldValue);
                if (reservation != null && reservation.UserId != _queryContext.UserId)
                    throw new UnauthorizedActionException(
                        $"This file is reserved by {reservation.UserName} and cannot be replaced.");
                effectiveValues[field.Fid.Value] =
                    FileReservationContract.PreserveReservation(
                        oldValue, newValue, _queryContext.UserName, DateTime.UtcNow,
                        GetRevisionLimit(field.Settings));
            }
        }

        return await _inner.ApplyAsync(table, fields, recordPublicId, effectiveValues, auditAction,
            entityTitle, ct, transaction, suppressInterception, onIndexMessageCreated, oldRecord);
    }

    private static int GetRevisionLimit(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return 3;
        try
        {
            var settings = JsonSerializer.Deserialize<FileSettings>(settingsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return settings?.KeepAllRevisions == true ? 100 : Math.Clamp(settings?.RevisionLimit ?? 3, 1, 100);
        }
        catch (JsonException) { return 3; }
    }
}
