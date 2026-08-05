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
    /// <param name="seedDefaultViews">
    /// When false, the default "List All"/"List Changes" reports and "Main Form" are not created.
    /// Import passes false for a table whose source file defines its own forms/reports: seeding
    /// them too would leave the table with two "Main Form"s and two "List All"s, and — because
    /// the seeded copies are the ones marked IsDefault — the empty seeded form would be what
    /// users actually open. System fields and role permissions are always seeded regardless,
    /// since those are structural rather than something an import supplies.
    /// </param>
    Task<AppTable> CreateTableWithDefaultsAsync(AppTable table, long userId, bool seedDefaultViews = true, CancellationToken ct = default);
}
