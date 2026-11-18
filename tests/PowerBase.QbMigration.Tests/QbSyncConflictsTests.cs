using FluentAssertions;
using PowerBase.Application.QbMigration;
using static PowerBase.Application.QbMigration.QbConflictKind;

namespace PowerBase.QbMigration.Tests;

public class QbSyncConflictsTests
{
    private static readonly DateTime Since = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
    private static Guid Stable(long id) => new($"00000000-0000-0000-0000-{id:D12}");
    private static QbPbRowState Row(long id, DateTime? modified = null, bool deleted = false, Guid? publicId = null) =>
        new(id, publicId ?? Stable(id), deleted, modified, Since.AddDays(-60));

    private static List<QbConflict> Find(IEnumerable<QbPbRowState> rows, Dictionary<long, DateTime?> qb, params Guid[] audited) =>
        QbSyncConflicts.Find(rows, Stable, qb, audited.ToHashSet(), Since);

    [Fact]
    public void A_record_created_in_powerbase_has_a_public_id_the_migration_never_gives()
    {
        Find([Row(7, publicId: Guid.NewGuid())], new() { [7] = Since.AddMinutes(5) })
            .Should().Equal(new QbConflict(7, CreatedInPowerBase));
        Find([Row(8, deleted: true, publicId: Guid.NewGuid())], []).Should().BeEmpty("created and deleted in PowerBase: nothing to lose");
    }

    [Fact]
    public void A_deleted_record_quickbase_still_has_was_deleted_in_powerbase()
    {
        Find([Row(6, deleted: true), Row(9, deleted: true)], new() { [6] = Since.AddDays(-30) })
            .Should().Equal(new QbConflict(6, DeletedInPowerBase));
    }

    [Fact]
    public void Synced_records_carry_quickbase_dates_so_they_are_not_conflicts()
    {
        var rows = new[] { Row(1, Since.AddMinutes(3)), Row(2, Since.AddMinutes(3)) };
        // 1: unchanged in QuickBase since; 2: changed again in QuickBase later (the sync will copy it, not a PowerBase edit).
        Find(rows, new() { [1] = Since.AddMinutes(3), [2] = Since.AddMinutes(40) }).Should().BeEmpty();
    }

    [Fact]
    public void A_powerbase_edit_stamps_a_date_later_than_quickbase_has()
    {
        var rows = new[] { Row(1, Since.AddMinutes(20)), Row(2, Since.AddMinutes(20)), Row(3, Since.AddMinutes(20).AddMilliseconds(500)) };
        Find(rows, new() { [1] = Since.AddDays(-5), [2] = null, [3] = Since.AddMinutes(20) })
            .Should().Equal(new QbConflict(1, EditedInPowerBase), new QbConflict(2, EditedInPowerBase));
    }

    [Fact]
    public void The_activity_log_catches_an_edit_quickbase_changed_again_afterwards()
    {
        Find([Row(4, Since.AddMinutes(10))], new() { [4] = Since.AddMinutes(30) }, Stable(4))
            .Should().Equal(new QbConflict(4, EditedInPowerBase));
    }
}
