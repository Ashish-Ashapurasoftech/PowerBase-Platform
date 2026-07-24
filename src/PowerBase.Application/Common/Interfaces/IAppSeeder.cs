using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Creates a table with the standard PowerBase defaults: physical DDL, system fields
/// (Record ID#, Date Created/Modified, Record Owner, Last Modified By), default "List All" /
/// "List Changes" reports, a "Main Form", and default role permissions.
///
/// Shared by <c>CreateAppCommandHandler</c>, <c>CreateTableCommandHandler</c>, and the PBL
/// import engine so the seeding sequence exists in exactly one place.
/// </summary>
public interface IAppSeeder
{
    /// <summary>
    /// Persists <paramref name="table"/> (which must already have AppId/Name/labels/CreatedBy
    /// set, but no Id) and seeds it with the standard defaults. Returns the same instance with
    /// Id, PublicId, and PhysicalTableName populated.
    /// </summary>
    Task<AppTable> CreateTableWithDefaultsAsync(AppTable table, long userId, CancellationToken ct = default);
}
