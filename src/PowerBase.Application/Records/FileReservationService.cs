using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records;

public sealed record FileReservationResult(
    bool IsReserved, bool IsReservedByMe, bool CanRelease,
    string? ReservedBy, string? Comment, DateTime? ReservedOn, string Value);

public sealed class FileReservationService
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IFileRecordWriteService _writeService;
    private readonly IRolePermissionEnforcer _permissionEnforcer;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRepository _appRepo;
    private readonly IQueryContext _queryContext;
    private readonly IFileStorageService _fileStorage;

    public FileReservationService(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo, IFileRecordWriteService writeService,
        IRolePermissionEnforcer permissionEnforcer, IAppUserRepository appUserRepo,
        IAppRepository appRepo, IQueryContext queryContext, IFileStorageService fileStorage)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _writeService = writeService;
        _permissionEnforcer = permissionEnforcer;
        _appUserRepo = appUserRepo;
        _appRepo = appRepo;
        _queryContext = queryContext;
        _fileStorage = fileStorage;
    }

    public async Task<FileReservationResult> GetAsync(Guid tablePublicId, Guid recordPublicId, long fid, CancellationToken ct)
    {
        var loaded = await LoadAsync(tablePublicId, recordPublicId, fid, requireModify: false, ct);
        return await ToResultAsync(loaded.Value, loaded.Table.AppId, ct);
    }

    public async Task<FileReservationResult> ReserveAsync(Guid tablePublicId, Guid recordPublicId, long fid, string? comment, CancellationToken ct)
    {
        var loaded = await LoadAsync(tablePublicId, recordPublicId, fid, requireModify: true, ct);
        var existing = FileReservationContract.Read(loaded.Value);
        if (existing != null && existing.UserId != _queryContext.UserId)
            throw new UnauthorizedActionException($"This file is reserved by {existing.UserName}.");

        var value = FileReservationContract.Reserve(loaded.Value, new FileReservation(
            _queryContext.UserId, _queryContext.UserName, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), DateTime.UtcNow));
        await _writeService.ApplyFileWriteAsync(loaded.Table, loaded.Fields, recordPublicId,
            new Dictionary<long, object?> { [fid] = value }, AuditActions.Updated,
            "File attachment reserved", ct, suppressInterception: true);
        return await ToResultAsync(value, loaded.Table.AppId, ct);
    }

    public async Task<FileReservationResult> ReleaseAsync(Guid tablePublicId, Guid recordPublicId, long fid, CancellationToken ct)
    {
        var loaded = await LoadAsync(tablePublicId, recordPublicId, fid, requireModify: true, ct);
        var existing = FileReservationContract.Read(loaded.Value);
        if (existing == null) return await ToResultAsync(loaded.Value?.ToString() ?? string.Empty, loaded.Table.AppId, ct);
        var isAdmin = await IsAppManagerAsync(loaded.Table.AppId, ct);
        if (existing.UserId != _queryContext.UserId && !isAdmin)
            throw new UnauthorizedActionException($"This file is reserved by {existing.UserName}.");

        var value = FileReservationContract.Release(loaded.Value);
        await _writeService.ApplyFileWriteAsync(loaded.Table, loaded.Fields, recordPublicId,
            new Dictionary<long, object?> { [fid] = value }, AuditActions.Updated,
            "File attachment reservation released", ct, suppressInterception: true,
            bypassFileReservation: true);
        return await ToResultAsync(value, loaded.Table.AppId, ct);
    }

    public async Task<string?> DeleteRevisionAsync(Guid tablePublicId, Guid recordPublicId, long fid, string path, CancellationToken ct)
        => await DeleteRevisionsAsync(tablePublicId, recordPublicId, fid, [path], ct);

    public async Task<string?> DeleteRevisionsAsync(Guid tablePublicId, Guid recordPublicId, long fid,
        IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var loaded = await LoadAsync(tablePublicId, recordPublicId, fid, requireModify: true, ct);
        var reservation = FileReservationContract.Read(loaded.Value);
        if (reservation != null && reservation.UserId != _queryContext.UserId &&
            !await IsAppManagerAsync(loaded.Table.AppId, ct))
            throw new UnauthorizedActionException($"This file is reserved by {reservation.UserName}.");
        string? value;
        try { value = FileReservationContract.DeleteRevisions(loaded.Value, paths); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        await _writeService.ApplyFileWriteAsync(loaded.Table, loaded.Fields, recordPublicId,
            new Dictionary<long, object?> { [fid] = value }, AuditActions.Updated,
            "File attachment revision deleted", ct, suppressInterception: true,
            bypassFileReservation: true);
        foreach (var path in paths)
            await _fileStorage.DeleteAsync(path, ct);
        return value;
    }

    private async Task<(PowerBase.Domain.Entities.AppTable Table, IReadOnlyList<PowerBase.Domain.Entities.AppField> Fields, object? Value)> LoadAsync(
        Guid tablePublicId, Guid recordPublicId, long fid, bool requireModify, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var field = fields.FirstOrDefault(f => f.Fid == fid && string.Equals(f.TypeCode, "File", StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException("File field", fid);
        var access = await _permissionEnforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (!access.CanView || !access.VisibleFields.Any(f => f.Id == field.Id))
                throw new UnauthorizedActionException("You do not have permission to view this file.");
            if (requireModify && (access.ModifyScope == RecordScopes.None || !access.EditableFieldIds.Contains(fid)))
                throw new UnauthorizedActionException("You do not have permission to reserve this file.");
            if ((access.ViewScope == RecordScopes.OwnRecords || requireModify && access.ModifyScope == RecordScopes.OwnRecords))
                await _permissionEnforcer.EnsureRecordOwnedAsync(table, recordPublicId, ct);
        }
        var row = await _recordRepo.GetByPublicIdAsync(table, fields, recordPublicId, ct);
        row.TryGetValue(PhysicalNaming.GetPhysicalColumnName(field), out var value);
        if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
            throw new InvalidOperationException("A file must be attached before it can be reserved.");
        return (table, fields, value);
    }

    private async Task<FileReservationResult> ToResultAsync(object? value, long appId, CancellationToken ct)
    {
        var reservation = FileReservationContract.Read(value);
        var isAdmin = await IsAppManagerAsync(appId, ct);
        return new FileReservationResult(
            reservation != null, reservation?.UserId == _queryContext.UserId,
            reservation?.UserId == _queryContext.UserId || isAdmin,
            reservation?.UserName, reservation?.Comment, reservation?.ReservedOn,
            value?.ToString() ?? string.Empty);
    }

    private async Task<bool> IsAppManagerAsync(long appId, CancellationToken ct)
    {
        if (_queryContext.IsSuperAdmin || _queryContext.IsTenantAdmin) return true;
        var app = await _appRepo.GetByIdAsync(appId, ct);
        if (app.OwnerId == _queryContext.UserId) return true;
        return string.Equals(await _appUserRepo.GetUserRoleNameAsync(appId, _queryContext.UserId, ct),
            "Administrator", StringComparison.OrdinalIgnoreCase);
    }
}
