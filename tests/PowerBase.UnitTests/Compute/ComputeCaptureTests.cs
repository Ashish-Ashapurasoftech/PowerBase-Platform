using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Compute;
using PowerBase.ComputeWorker;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Repositories;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Phase 2 of stored columns: change capture (ComputeOutbox), the queue message, and the helpers beside IsComputedTypeCode.</summary>
public class ComputeCaptureTests
{
    // ── PhysicalNaming: new helpers, old meaning untouched ───────────────────────────────────────────────

    [Fact]
    public void MaterializedColumnName_uses_the_c_prefix_never_f()
    {
        PhysicalNaming.MaterializedColumnName(105).Should().Be("c_105");
        PhysicalNaming.ColumnName(105).Should().Be("f_105");
    }

    [Theory]
    [InlineData("Formula"), InlineData("Lookup"), InlineData("Summary"), InlineData("ReportLink"), InlineData("ActionButton"), InlineData("Formula_Text")]
    public void IsComputedTypeCode_keeps_its_meaning(string typeCode) =>
        PhysicalNaming.IsComputedTypeCode(typeCode).Should().BeTrue();

    [Theory]
    [InlineData("Lookup", true), InlineData("Summary", true), InlineData("Formula", true), InlineData("Formula_Number", true),
     InlineData("ReportLink", false), InlineData("ActionButton", false), InlineData("Text", false), InlineData("Reference", false)]
    public void Only_lookup_summary_and_formula_can_be_stored(string typeCode, bool expected) =>
        PhysicalNaming.IsMaterializableTypeCode(typeCode).Should().Be(expected);

    [Fact]
    public void A_field_marked_not_allowed_can_never_be_stored()
    {
        var field = new AppField { Fid = 7, TypeCode = "Summary", MaterializeMode = MaterializeModes.NotAllowed };
        PhysicalNaming.CanMaterialize(field).Should().BeFalse();
        field.MaterializeMode = MaterializeModes.Off;
        PhysicalNaming.CanMaterialize(field).Should().BeTrue();
    }

    [Theory]
    [InlineData(MaterializeModes.On, MaterializeStates.Ready, true)]
    [InlineData(MaterializeModes.On, MaterializeStates.Building, false)]
    [InlineData(MaterializeModes.On, MaterializeStates.Failed, false)]
    [InlineData(MaterializeModes.On, MaterializeStates.None, false)]
    [InlineData(MaterializeModes.Off, MaterializeStates.Ready, false)]
    [InlineData(MaterializeModes.NotAllowed, MaterializeStates.Ready, false)]
    public void Reads_use_the_stored_column_only_when_on_and_ready(byte mode, byte state, bool expected) =>
        PhysicalNaming.IsMaterializedReady(new AppField { Fid = 9, TypeCode = "Lookup", MaterializeMode = mode, MaterializeState = state })
            .Should().Be(expected);

    [Fact]
    public void A_new_field_defaults_to_off_and_not_ready()
    {
        var field = new AppField { Fid = 1, TypeCode = "Summary" };
        field.MaterializeMode.Should().Be(MaterializeModes.Off);
        PhysicalNaming.IsMaterializedReady(field).Should().BeFalse();
    }

    // ── switches: every default keeps today's behaviour ──────────────────────────────────────────────────

    [Fact]
    public void Without_config_compute_and_replica_are_off()
    {
        var empty = new ConfigurationBuilder().Build();
        var compute = ComputeConfig.ReadCompute(empty);
        compute.Enabled.Should().BeFalse();
        compute.RepublishEnabled.Should().BeTrue();   // Phase 3: the worker consumes the rows, so lost messages are re-published
        compute.QueueName.Should().Be("compute-queue");
        ComputeConfig.ReadReplica(empty).Enabled.Should().BeFalse();
    }

    [Fact]
    public void Config_values_are_read_and_bad_values_keep_the_default()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Compute:Enabled"] = "true",
            ["Compute:GraceSeconds"] = "30",
            ["Compute:MaxAttempts"] = "not-a-number",
            ["ReadReplica:Enabled"] = "true",
            ["ReadReplica:MaxLagSeconds"] = "2",
        }).Build();
        var compute = ComputeConfig.ReadCompute(config);
        compute.Enabled.Should().BeTrue();
        compute.GraceSeconds.Should().Be(30);
        compute.MaxAttempts.Should().Be(new ComputeOptions().MaxAttempts);
        ComputeConfig.ReadReplica(config).MaxLagSeconds.Should().Be(2);
    }

    // ── outbox row shape ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Changed_fids_are_stored_sorted_and_distinct()
    {
        ComputeOutboxJson.SerializeFids([9, 7, 9]).Should().Be("[7,9]");
        ComputeOutboxJson.SerializeFids([]).Should().BeNull();
        ComputeOutboxJson.SerializeFids(null).Should().BeNull();
        ComputeOutboxJson.ParseFids("[7,9]").Should().Equal(7, 9);
    }

    [Fact]
    public void Old_parents_round_trip_including_a_missing_parent()
    {
        var json = ComputeOutboxJson.SerializeOldRefs(new Dictionary<int, long?> { [12] = 42, [8] = null });
        json.Should().Be("{\"8\":null,\"12\":42}");
        var back = ComputeOutboxJson.ParseOldRefs(json);
        back[12].Should().Be(42);
        back[8].Should().BeNull();
        ComputeOutboxJson.SerializeOldRefs(new Dictionary<int, long?>()).Should().BeNull();
    }

    [Fact]
    public void The_insert_writes_one_tuple_per_change_with_named_parameters()
    {
        var id = Guid.NewGuid();
        var (sql, parameters) = ComputeOutboxRepository.BuildInsert(
        [
            new ComputeChange(5, ComputeChangeReason.Update, RecordPublicId: id, ChangedFids: [3, 2], OldRefs: new Dictionary<int, long?> { [3] = 10 }),
            ComputeChange.TableWide(6)
        ]);

        sql.Should().Be("INSERT INTO meta.ComputeOutbox (TableId, RecordId, RecordPublicId, Reason, ChangedFids, OldRefs) VALUES " +
                        "(@t0, @r0, @p0, @x0, @f0, @o0), (@t1, @r1, @p1, @x1, @f1, @o1)");
        parameters.Get<long>("t0").Should().Be(5);
        parameters.Get<Guid?>("p0").Should().Be(id);
        parameters.Get<byte>("x0").Should().Be((byte)ComputeChangeReason.Update);
        parameters.Get<string>("f0").Should().Be("[2,3]");
        parameters.Get<string>("o0").Should().Be("{\"3\":10}");
        parameters.Get<long?>("r1").Should().BeNull();
        parameters.Get<Guid?>("p1").Should().BeNull();
        parameters.Get<byte>("x1").Should().Be((byte)ComputeChangeReason.TableWide);
    }

    [Fact]
    public void An_insert_chunk_stays_under_the_sql_server_parameter_limit() =>
        (ComputeOutboxRepository.InsertChunk * 6).Should().BeLessThan(2100);

    // ── large writes and the queue ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_large_write_becomes_one_table_wide_row()
    {
        var changes = Enumerable.Range(1, 1500).Select(i => new ComputeChange(5, ComputeChangeReason.Create, RecordId: i))
            .Append(new ComputeChange(8, ComputeChangeReason.Update, RecordId: 1))
            .ToList();

        var planned = ComputeOutboxPlanner.CollapseLargeBatch(changes, threshold: 1000);

        planned.Should().HaveCount(2);
        planned.Should().ContainSingle(c => c.TableId == 5 && c.IsTableWide);
        planned.Should().ContainSingle(c => c.TableId == 8 && c.RecordId == 1);
    }

    [Fact]
    public void A_small_write_keeps_one_row_per_record()
    {
        var changes = Enumerable.Range(1, 3).Select(i => new ComputeChange(5, ComputeChangeReason.Delete, RecordId: i)).ToList();
        ComputeOutboxPlanner.CollapseLargeBatch(changes, threshold: 1000).Should().HaveCount(3);
    }

    [Fact]
    public void The_relay_sends_one_message_per_table_not_per_row()
    {
        var messages = ComputeOutboxPlanner.ToMessages(44, [new(1, 5, 1), new(2, 5, 1), new(3, 9, 2)]);
        messages.Should().Equal(new ComputeQueueMessage(44, 5), new ComputeQueueMessage(44, 9));
    }

    [Fact]
    public void One_session_per_tenant_and_table()
    {
        var message = new ComputeQueueMessage(44, 2710);
        message.SessionId.Should().Be("44:2710");

        var bus = ServiceBusComputePublisher.ToServiceBusMessage(message);
        bus.SessionId.Should().Be("44:2710");
        ComputeQueueWorker.TryParse(bus.Body).Should().Be(message);
    }

    [Fact]
    public void An_unreadable_queue_message_is_rejected()
    {
        ComputeQueueWorker.TryParse(BinaryData.FromString("not json")).Should().BeNull();
        ComputeQueueWorker.TryParse(BinaryData.FromString(JsonSerializer.Serialize(new { TenantId = 0, TableId = 5 }))).Should().BeNull();
    }

    // ── old parent capture on a Reference change (OUTPUT deleted.f_ref) ──────────────────────────────────

    [Fact]
    public void Without_reference_fields_the_update_sql_is_unchanged()
    {
        // Line endings follow the source file's (the repository stores LF; a Windows checkout may have CRLF): compare without them.
        var sql = RecordRepository.BuildUpdateByPublicIdSql("data.t_5", ["f_7 = @f_7"], []).Replace("\r\n", "\n");
        sql.Should().Be("UPDATE data.t_5\nSET f_7 = @f_7, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy\nWHERE PublicId = @publicId AND IsDeleted = 0");
        sql.Should().NotContain("OUTPUT");
    }

    [Fact]
    public void With_a_reference_field_the_update_returns_the_old_parent()
    {
        var sql = RecordRepository.BuildUpdateByPublicIdSql("data.t_5", ["f_7 = @f_7", "f_9 = @f_9"], [7]).Replace("\r\n", "\n");
        sql.Should().Contain("OUTPUT deleted.f_7 AS f_7\nWHERE PublicId = @publicId AND IsDeleted = 0");
        sql.Should().NotContain("inserted.");
    }

    [Fact]
    public void Old_parents_are_read_from_the_output_row()
    {
        IDictionary<string, object?> row = new Dictionary<string, object?> { ["f_7"] = 42L, ["f_9"] = DBNull.Value };
        var old = RecordRepository.ReadOldRefs(row, [7, 9, 11]);
        old[7].Should().Be(42);
        old[9].Should().BeNull();
        old[11].Should().BeNull();
    }
}
