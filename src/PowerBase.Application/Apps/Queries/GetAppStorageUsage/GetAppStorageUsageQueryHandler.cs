using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Queries.GetAppStorageUsage;

public record AppStorageUsageResult(long DatabaseSizeBytes, long FileStorageSizeBytes);

public class GetAppStorageUsageQueryHandler
{
    private readonly IAppRepository _appRepo;

    public GetAppStorageUsageQueryHandler(IAppRepository appRepo)
    {
        _appRepo = appRepo;
    }

    public async Task<AppStorageUsageResult> HandleAsync(GetAppStorageUsageQuery query, CancellationToken ct = default)
    {
        // Throws NotFoundException if app doesn't exist
        await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);

        long dbSizeBytes = await _appRepo.GetDatabaseSizeBytesAsync(ct);
        long fileStorageSizeBytes = await _appRepo.GetFileStorageSizeBytesAsync(ct);

        return new AppStorageUsageResult(dbSizeBytes, fileStorageSizeBytes);
    }
}
