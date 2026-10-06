using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Imports;

public sealed class ImportFileRepository(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IImportFileRepository
{
    public async Task<long> CreateAsync(ImportFile file, CancellationToken ct = default)
    {
        file.PublicId = Guid.NewGuid();
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        file.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO meta.ImportFile (PublicId, UploadedByUserId, FileName, StoragePath, Format, SizeBytes)
            OUTPUT INSERTED.Id
            VALUES (@PublicId, @UploadedByUserId, @FileName, @StoragePath, @Format, @SizeBytes)
            """, file, cancellationToken: ct));
        return file.Id;
    }

    public async Task<ImportFile?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ImportFile>(new CommandDefinition(
            "SELECT * FROM meta.ImportFile WHERE PublicId = @publicId", new { publicId }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM meta.ImportFile WHERE PublicId = @publicId", new { publicId }, cancellationToken: ct));
    }

    public async Task<int> CountByUserAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM meta.ImportFile WHERE UploadedByUserId = @userId", new { userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportFile>> ListOlderThanAsync(DateTime before, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ImportFile>(new CommandDefinition(
            "SELECT TOP (@take) * FROM meta.ImportFile WHERE CreatedOn < @before ORDER BY CreatedOn",
            new { before, take }, cancellationToken: ct))).AsList();
    }
}
