using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>Run history: the Record ID# on record steps, system fields matched by FID (not by internal id), and the rejected records of a Copy Records step.</summary>
public class PipelineAuditFormatterRecordIdTests
{
    private readonly PipelineAuditFormatter _formatter;
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly AppTable _table = new() { Id = 1, AppId = 1, PublicId = Guid.NewGuid(), Name = "A1" };

    public PipelineAuditFormatterRecordIdTests()
    {
        _formatter = new PipelineAuditFormatter(Substitute.For<IPipelineRepository>(), Substitute.For<IAppRepository>(), _tableRepo, _fieldRepo,
            Substitute.For<IUserRepository>(), _recordRepo);
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        // The same shape as a real table: the internal ids are NOT the FIDs ("Record ID#" has internal Id 1 but FID 3; "Date Created" has FID 1).
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 1, Fid = 3, Name = "S_recordId", Label = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
            new() { Id = 2, Fid = 1, Name = "S_dateCreated", Label = "Date Created", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn" },
            new() { Id = 3, Fid = 2, Name = "S_dateModified", Label = "Date Modified", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "ModifiedOn" },
            new() { Id = 4, Fid = 4, Name = "S_recordOwner", Label = "Record Owner", TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy" },
            new() { Id = 6, Fid = 6, Name = "C_name", Label = "name", TypeCode = "Text" },
        });
    }

    private (JsonElement Input, JsonElement Output, JsonDocument Keep1, JsonDocument Keep2) Format(string subtype, object input, object output, string status = "Success")
    {
        var result = _formatter.FormatStepRun(new PipelineStep { Id = 1, Type = subtype == "new-event" ? "trigger" : "action", Subtype = subtype },
            JsonSerializer.Serialize(input), JsonSerializer.Serialize(output), status, "test", DateTime.UtcNow, DateTime.UtcNow);
        var i = JsonDocument.Parse(result.InputContextJson);
        var o = JsonDocument.Parse(result.OutputContextJson);
        return (i.RootElement.GetProperty("Input"), o.RootElement.GetProperty("Output"), i, o);
    }

    // ── system fields are matched by FID ─────────────────────────────────────────────────

    [Fact]
    public void CreateRecord_ShowsTheRecordIdAndEverySystemFieldUnderItsOwnLabel()
    {
        var recordGuid = Guid.NewGuid();
        var (_, output, _, _) = Format("create-record",
            new { TableId = _table.PublicId.ToString(), FieldMappings = new Dictionary<string, object> { ["fid_6"] = "Mehul" } },
            new Dictionary<string, object?>
            {
                ["CreatedRecordPublicId"] = recordGuid.ToString(), ["RecordPublicId"] = recordGuid.ToString(), ["PublicId"] = recordGuid.ToString(),
                ["Id"] = 25, ["fid_3"] = 25, ["CreatedOn"] = "2026-10-12T05:13:49.249", ["CreatedBy"] = 40021, ["ModifiedOn"] = null, ["ModifiedBy"] = null,
                ["fid_1"] = "2026-10-12T05:13:49.249Z", ["fid_2"] = null, ["fid_4"] = 40021, ["fid_6"] = "Mehul"
            });

        output.GetProperty("Record ID").GetInt64().Should().Be(25);
        var fields = output.GetProperty("Fields");
        // The regression: fid_1 used to match "Record ID#" (internal Id 1), so Record ID# showed a date and Date Created was empty.
        fields.GetProperty("Record ID#").GetInt64().Should().Be(25);
        fields.GetProperty("Date Created").GetProperty("iso").GetString().Should().StartWith("2026-10-12T05:13:49");
        fields.TryGetProperty("Date Modified", out var modified).Should().BeTrue();
        modified.ValueKind.Should().Be(JsonValueKind.Null);
        fields.GetProperty("Record Owner").GetInt64().Should().Be(40021);
        fields.GetProperty("name").GetString().Should().Be("Mehul");
    }

    [Fact]
    public void CreateRecord_FieldsDoNotRepeatTheEnginesInternalAliasKeys()
    {
        var (_, output, _, _) = Format("create-record",
            new { TableId = _table.PublicId.ToString(), FieldMappings = new Dictionary<string, object>() },
            new Dictionary<string, object?>
            {
                ["CreatedRecordPublicId"] = Guid.NewGuid().ToString(), ["RecordPublicId"] = Guid.NewGuid().ToString(), ["PublicId"] = Guid.NewGuid().ToString(),
                ["Id"] = 5, ["fid_3"] = 5, ["CreatedOn"] = "2026-01-01T00:00:00", ["CreatedBy"] = 1, ["ModifiedOn"] = null, ["ModifiedBy"] = null, ["fid_6"] = "x"
            });

        var fields = output.GetProperty("Fields");
        foreach (var alias in new[] { "Id", "PublicId", "RecordPublicId", "CreatedRecordPublicId", "CreatedOn", "CreatedBy", "ModifiedOn", "ModifiedBy", "FieldCount" })
            fields.TryGetProperty(alias, out _).Should().BeFalse($"'{alias}' duplicates a labelled field");
        // The record's identity is still there, once, at the top.
        output.GetProperty("CreatedRecordPublicId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void UpdateRecord_ShowsTheRecordIdOnInputAndOutput_AndTheWholeUpdatedRecord()
    {
        var target = Guid.NewGuid();
        var (input, output, _, _) = Format("update-record",
            new { TableId = _table.PublicId.ToString(), TargetRecordId = target.ToString(), FieldMappings = new Dictionary<string, object> { ["fid_6"] = "new" } },
            new Dictionary<string, object?> { ["UpdatedRecordPublicId"] = target.ToString(), ["Id"] = 31, ["fid_3"] = 31, ["fid_1"] = "2026-02-02T00:00:00", ["fid_6"] = "new", ["FieldCount"] = 1 });

        input.GetProperty("Record ID").GetInt64().Should().Be(31);
        output.GetProperty("Record ID").GetInt64().Should().Be(31);
        output.GetProperty("Fields").GetProperty("Record ID#").GetInt64().Should().Be(31);
        output.GetProperty("Fields").TryGetProperty("FieldCount", out _).Should().BeFalse();
    }

    [Fact]
    public void UpdateRecord_WithoutAnIdInTheOutput_ResolvesTheRecordIdFromTheRecordsPublicId()
    {
        var target = Guid.NewGuid();
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [target] = 44 });

        var (input, output, _, _) = Format("update-record",
            new { TableId = _table.PublicId.ToString(), TargetRecordId = target.ToString(), FieldMappings = new Dictionary<string, object>() },
            new Dictionary<string, object?> { ["UpdatedRecordPublicId"] = target.ToString(), ["fid_6"] = "x" });   // a run recorded before the id was part of the output

        input.GetProperty("Record ID").GetInt64().Should().Be(44);
        output.GetProperty("Record ID").GetInt64().Should().Be(44);
    }

    [Fact]
    public void DeleteRecord_ShowsTheRecordIdTheEngineCapturedBeforeDeleting()
    {
        var target = Guid.NewGuid();
        var (input, output, _, _) = Format("delete-record",
            new { TableId = _table.PublicId.ToString(), TargetRecordId = target.ToString() },
            new { DeletedRecordPublicId = target.ToString(), DeletedRecordId = 9, DeletedRecordName = "Raj" });

        input.GetProperty("Record ID").GetInt64().Should().Be(9);
        output.GetProperty("Record ID").GetInt64().Should().Be(9);
        output.GetProperty("Status").GetString().Should().Be("Deleted");
    }

    [Fact]
    public void DeleteRecord_WithAnOldOutput_StillFormatsWithoutARecordId()
    {
        var target = Guid.NewGuid();
        var act = () => Format("delete-record",
            new { TableId = _table.PublicId.ToString(), TargetRecordId = target.ToString() },
            new { DeletedRecordPublicId = target.ToString(), DeletedRecordName = "Raj" });

        act.Should().NotThrow();
    }

    [Fact]
    public void LookUpRecord_ShowsTheRecordId()
    {
        var (input, output, _, _) = Format("look-up-record",
            new { TablePublicId = _table.PublicId.ToString(), RecordId = Guid.NewGuid().ToString() },
            new Dictionary<string, object?> { ["Id"] = 12, ["fid_3"] = 12, ["fid_6"] = "x" });

        output.GetProperty("Record ID").GetInt64().Should().Be(12);
        input.GetProperty("Record ID").GetInt64().Should().Be(12);
    }

    [Fact]
    public void Trigger_ShowsTheRecordIdFromTheEventValues_AndTheDatesUnderTheirOwnLabels()
    {
        var record = Guid.NewGuid();
        var (input, output, _, _) = Format("new-event",
            new
            {
                EventType = "Added", TablePublicId = _table.PublicId.ToString(), RecordPublicId = record.ToString(),
                NewValues = new Dictionary<string, object?> { ["fid_3"] = 25, ["fid_1"] = "2026-10-12T05:13:49Z", ["fid_6"] = "Mehul" }
            },
            new { });

        input.GetProperty("record_id").GetInt64().Should().Be(25);
        output.GetProperty("Record ID").GetInt64().Should().Be(25);
        output.GetProperty("Record ID#").GetInt64().Should().Be(25);
        output.GetProperty("Date Created").GetProperty("iso").GetString().Should().StartWith("2026-10-12");
    }

    [Fact]
    public void Trigger_WithoutTheIdInTheEventValues_ResolvesItFromThePublicId()
    {
        var record = Guid.NewGuid();
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [record] = 77 });

        var (input, output, _, _) = Format("new-event",
            new { EventType = "Added", TablePublicId = _table.PublicId.ToString(), RecordPublicId = record.ToString(), NewValues = new Dictionary<string, object?> { ["fid_6"] = "x" } },
            new { });

        input.GetProperty("record_id").GetInt64().Should().Be(77);
        output.GetProperty("Record ID").GetInt64().Should().Be(77);
    }

    [Fact]
    public void Trigger_TechnicalDetailsKeepThePublicIdTheRunListReadsBack()
    {
        var record = Guid.NewGuid();
        var result = _formatter.FormatStepRun(new PipelineStep { Id = 1, Type = "trigger", Subtype = "new-event" },
            JsonSerializer.Serialize(new { EventType = "Added", TablePublicId = _table.PublicId.ToString(), RecordPublicId = record.ToString(), NewValues = new Dictionary<string, object?> { ["fid_3"] = 5 } }),
            "{}", "Success", "test", DateTime.UtcNow, DateTime.UtcNow);

        using var parsed = JsonDocument.Parse(result.InputContextJson);
        var technical = parsed.RootElement.GetProperty("TechnicalDetails");
        // The run history list resolves each run's record from exactly these two keys.
        technical.GetProperty("RecordPublicId").GetString().Should().Be(record.ToString());
        technical.GetProperty("TablePublicId").GetString().Should().Be(_table.PublicId.ToString());
    }

    // ── Copy Records: rejected records ───────────────────────────────────────────────────

    private static object FailedRecord(int i) => new
    {
        SourcePublicId = Guid.NewGuid().ToString(), SourceRecordId = (long)(100 + i), MergeValue = "k" + i,
        Reason = "'name' must be unique — this value is already in use.", Values = new Dictionary<string, string?> { ["name"] = "Raj" + i, ["City"] = null }
    };

    private (JsonElement Output, string Log) FormatCopy(string status, object output)
    {
        var result = _formatter.FormatStepRun(new PipelineStep { Id = 1, Type = "action", Subtype = "copy-records" },
            JsonSerializer.Serialize(new { SourceTable = Guid.NewGuid().ToString(), DestinationTable = Guid.NewGuid().ToString(), SourceFields = new[] { "fid_6" }, DestinationFields = new[] { "fid_6" }, MergeField = "fid_6" }),
            JsonSerializer.Serialize(output), status, "test", DateTime.UtcNow, DateTime.UtcNow);
        return (JsonDocument.Parse(result.OutputContextJson).RootElement.GetProperty("Output"), result.LogMessage);
    }

    [Fact]
    public void CopyRecords_Failed_ListsTheRejectedRecordsWithTheirReasonAndCounts()
    {
        var (output, log) = FormatCopy("Failed", new
        {
            ErrorMessage = "Copy Records: 600 records could not be copied (5 inserted, 0 updated).", ExceptionType = "PipelineNonRetryableException",
            InsertedCount = 5, UpdatedCount = 0, ErrorCount = 600, FailedRecords = Enumerable.Range(0, 25).Select(FailedRecord).ToList(),
            FailedRecordsTruncated = false, FailedRecordPages = 3, FailedRecordsStored = 600
        });

        output.GetProperty("Status").GetString().Should().Be("Failed");
        output.GetProperty("Inserted Record Count").GetInt64().Should().Be(5);
        output.GetProperty("Error Count").GetInt64().Should().Be(600);
        var failed = output.GetProperty("Failed Records");
        failed.GetArrayLength().Should().Be(25);
        failed[0].GetProperty("Source Record ID#").GetInt64().Should().Be(100);
        failed[0].GetProperty("Merge Value").GetString().Should().Be("k0");
        failed[0].GetProperty("Reason").GetString().Should().Contain("must be unique");
        failed[0].GetProperty("Source Values").GetProperty("name").GetString().Should().Be("Raj0");
        output.GetProperty("Failed Records Note").GetString().Should().Contain("25 of 600").And.Contain("All failed records");
        log.Should().StartWith("Copy Records failed");
    }

    [Fact]
    public void CopyRecords_CompletedWithErrors_ListsTheRejectedRecordsToo()
    {
        var (output, _) = FormatCopy("Success", new
        {
            InsertedCount = 3, UpdatedCount = 0, ErrorCount = 2, Errors = new[] { "x", "x" },
            FailedRecords = new[] { FailedRecord(1), FailedRecord(2) }, FailedRecordPages = 1, FailedRecordsStored = 2
        });

        output.GetProperty("Status").GetString().Should().Be("Completed with errors");
        output.GetProperty("Failed Records").GetArrayLength().Should().Be(2);
        output.TryGetProperty("Failed Records Note", out _).Should().BeFalse("every rejected record is shown, nothing to point at");
    }

    [Fact]
    public void CopyRecords_NoRejections_HasNoFailedRecordsSection()
    {
        var (output, _) = FormatCopy("Success", new { InsertedCount = 3, UpdatedCount = 1, ErrorCount = 0, Errors = new string[0] });

        output.GetProperty("Status").GetString().Should().Be("Copied");
        output.TryGetProperty("Failed Records", out _).Should().BeFalse();
    }

    [Fact]
    public void CopyRecords_AnOldFailedRun_WithoutDetails_StillFormats()
    {
        var (output, _) = FormatCopy("Failed", new { ErrorMessage = "Copy Records: 17 records could not be copied (4245 inserted, 0 updated).", ExceptionType = "PipelineNonRetryableException" });

        output.GetProperty("Status").GetString().Should().Be("Failed");
        output.TryGetProperty("Failed Records", out _).Should().BeFalse();
    }

    [Fact]
    public void CopyRecords_FailedRecordsSurviveTheAuditOutputLimit()
    {
        // 500 records with long values is far above the usual 32,000-character audit cap, which would have truncated the list to 20 items.
        var many = Enumerable.Range(0, 500).Select(i => new
        {
            SourcePublicId = Guid.NewGuid().ToString(), SourceRecordId = (long)i, MergeValue = "k" + i, Reason = new string('r', 200),
            Values = new Dictionary<string, string?> { ["name"] = new string('n', 100) }
        }).ToList();

        var (output, _) = FormatCopy("Failed", new { ErrorMessage = "x", InsertedCount = 0, UpdatedCount = 0, ErrorCount = 500, FailedRecords = many, FailedRecordPages = 2, FailedRecordsStored = 500 });

        output.GetProperty("Failed Records").GetArrayLength().Should().Be(500);
    }
}
