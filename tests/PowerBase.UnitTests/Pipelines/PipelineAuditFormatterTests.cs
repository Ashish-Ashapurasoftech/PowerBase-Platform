using System.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineAuditFormatterTests
{
    private readonly PipelineAuditFormatter _formatter;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IUserRepository _userRepo;
    private readonly IRecordRepository _recordRepo;

    public PipelineAuditFormatterTests()
    {
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _appRepo = Substitute.For<IAppRepository>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _userRepo = Substitute.For<IUserRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();

        _formatter = new PipelineAuditFormatter(
            _pipelineRepo,
            _appRepo,
            _tableRepo,
            _fieldRepo,
            _userRepo,
            _recordRepo
        );
    }

    [Theory]
    [InlineData("http")]
    [InlineData("quickbase")]
    public void MakeRequestHistoryPreservesLargeStructuredResponseAndRedactsSecrets(string mode)
    {
        var body = new { items = Enumerable.Range(0, 100).Select(i => new { id = i, text = new string('x', 500) }), access_token = "private-token" };
        var input = JsonSerializer.Serialize(new { RequestMode = mode, Method = "POST", Url = "https://example.com/api", HTTPStatus = 201,
            StatusMessage = "Created", ResponseSize = 51000, ResponseBody = body, ResponseBodyAvailable = true,
            ResponseHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["Set-Cookie"] = "private-cookie" } });
        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "make-request" }, input, "{}", "Success", "test", DateTime.UtcNow, DateTime.UtcNow);
        using var parsed = JsonDocument.Parse(result.OutputContextJson);
        var output = parsed.RootElement.GetProperty("Output");
        Assert.Equal(201, output.GetProperty("HTTP Status").GetInt32());
        Assert.Equal(100, output.GetProperty("Response Body").GetProperty("items").GetArrayLength());
        Assert.Equal(500, output.GetProperty("Response Body").GetProperty("items")[99].GetProperty("text").GetString()!.Length);
        Assert.DoesNotContain("private-token", result.OutputContextJson);
        Assert.DoesNotContain("private-cookie", result.OutputContextJson);
        Assert.DoesNotContain("TRUNCATED", result.OutputContextJson);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("\"plain text\"")]
    public void MakeRequestHistorySupportsLegacyResponseShapes(string body)
    {
        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "make-request" }, "{\"HTTPStatus\":200}", body, "Success", "test", null, null);
        using var parsed = JsonDocument.Parse(result.OutputContextJson);
        Assert.Equal(body, parsed.RootElement.GetProperty("Output").GetProperty("Response Body").GetRawText());
    }

    [Fact]
    public void MakeRequestFailureDoesNotInventSuccessfulHttpStatus()
    {
        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "make-request" }, "{}", "{\"ErrorMessage\":\"DNS failed\"}", "Failed", "test", null, null);
        using var parsed = JsonDocument.Parse(result.OutputContextJson);
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("Output").GetProperty("HTTP Status").ValueKind);
        Assert.Equal("DNS failed", parsed.RootElement.GetProperty("Output").GetProperty("Error").GetString());
        Assert.DoesNotContain("completed", result.LogMessage);
    }

    [Theory]
    [InlineData("create-record", "Create Record")]
    [InlineData("update-record", "Update Record")]
    public void FailedRecordStepHistoryShowsMappingReasonInsteadOfSuccess(string subtype, string action)
    {
        const string reason = "Field 'number' requires a valid Number value, but its mapping from 'steps.ref_request.phone' returned text that cannot be converted.";
        var result = _formatter.FormatStepRun(
            new PipelineStep { Id = 101, Type = "action", Subtype = subtype, Label = action },
            "{}", JsonSerializer.Serialize(new { ErrorMessage = reason, ExceptionType = "PipelineMappingException" }),
            "Failed", "test", DateTime.UtcNow, DateTime.UtcNow);

        using var parsed = JsonDocument.Parse(result.OutputContextJson);
        var output = parsed.RootElement.GetProperty("Output");
        output.GetProperty("Status").GetString().Should().Be("Failed");
        output.GetProperty("Error").GetString().Should().Be(reason);
        output.TryGetProperty("Record", out _).Should().BeFalse();
        result.LogMessage.Should().Be($"{action} failed: {reason}");
        result.LogMessage.Should().NotContain("Created record").And.NotContain("Updated record");
    }

    private AppTable StubTable(long id, string name, params (int Fid, string Label)[] fields)
    {
        var table = new AppTable { Id = id, PublicId = Guid.NewGuid(), Name = name };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>())
            .Returns(fields.Select(f => new AppField { Id = id * 100 + f.Fid, Fid = f.Fid, Name = f.Label, Label = f.Label }).ToList());
        return table;
    }

    [Fact]
    public void CopyRecordsHistoryShowsTableAndFieldNamesInsteadOfFids()
    {
        var source = StubTable(1, "A1", (6, "Name"), (7, "City"));
        var destination = StubTable(2, "A2", (6, "Full name"), (8, "Town"), (3, "Record ID#"));
        var input = JsonSerializer.Serialize(new
        {
            SourceTable = $"conn:{source.PublicId}", DestinationTable = destination.PublicId.ToString(),
            SourceFields = new[] { "fid_6", "fid_7" }, DestinationFields = new[] { "fid_6", "fid_8" },
            MergeField = "fid_6", TerminateOnError = "Yes"
        });
        var output = JsonSerializer.Serialize(new { InsertedCount = 3, UpdatedCount = 1, ErrorCount = 0, Errors = new string[0] });

        var result = _formatter.FormatStepRun(new PipelineStep { Id = 5, Type = "action", Subtype = "copy-records" },
            input, output, "Success", "test", DateTime.UtcNow, DateTime.UtcNow);

        using var parsed = JsonDocument.Parse(result.InputContextJson);
        var friendly = parsed.RootElement.GetProperty("Input");
        friendly.GetProperty("Source Table").GetString().Should().Be("A1");
        friendly.GetProperty("Destination Table").GetString().Should().Be("A2");
        friendly.GetProperty("Source Fields").EnumerateArray().Select(e => e.GetString()).Should().Equal("Name", "City");
        friendly.GetProperty("Destination Fields").EnumerateArray().Select(e => e.GetString()).Should().Equal("Full name", "Town");
        friendly.GetProperty("Field Mappings").EnumerateArray().Select(e => e.GetString()).Should().Equal("Name → Full name", "City → Town");
        friendly.GetProperty("Merge Field").GetString().Should().Be("Full name");
        parsed.RootElement.GetProperty("Metadata").GetProperty("field_labels").GetProperty("fid_8").GetString().Should().Be("Town");
        result.InputContextJson.Should().NotContain("\"fid_6\"]");
        using var outputDoc = JsonDocument.Parse(result.OutputContextJson);
        var friendlyOutput = outputDoc.RootElement.GetProperty("Output");
        friendlyOutput.GetProperty("Inserted Record Count").GetInt64().Should().Be(3);
        friendlyOutput.GetProperty("Updated Record Count").GetInt64().Should().Be(1);
        friendlyOutput.GetProperty("Status").GetString().Should().Be("Copied");
        result.LogMessage.Should().Contain("A1").And.Contain("A2");
    }

    [Fact]
    public void CopyRecordsHistoryFallsBackToReferencesWhenTablesCannotBeLoaded()
    {
        var input = JsonSerializer.Serialize(new
        {
            SourceTable = Guid.NewGuid().ToString(), DestinationTable = Guid.NewGuid().ToString(),
            SourceFields = new[] { "fid_6" }, DestinationFields = new[] { "fid_6" }, MergeField = "fid_6"
        });

        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "copy-records" },
            input, "{}", "Success", "test", null, null);

        using var parsed = JsonDocument.Parse(result.InputContextJson);
        parsed.RootElement.GetProperty("Input").GetProperty("Source Fields")[0].GetString().Should().Be("fid_6");
    }

    [Fact]
    public void CopyRecordsFailureShowsPermissionError()
    {
        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "copy-records" },
            "{}", JsonSerializer.Serialize(new { ErrorMessage = "You don't have permission to perform this action.", ExceptionType = "PipelineNonRetryableException" }),
            "Failed", "test", null, null);

        using var parsed = JsonDocument.Parse(result.OutputContextJson);
        parsed.RootElement.GetProperty("Output").GetProperty("Status").GetString().Should().Be("Failed");
        parsed.RootElement.GetProperty("Output").GetProperty("Error").GetString().Should().Contain("permission");
        result.LogMessage.Should().StartWith("Copy Records failed");
    }

    [Theory]
    [InlineData("query", "search-records")]
    [InlineData("query", "look-up-record")]
    [InlineData("action", "delete-record")]
    [InlineData("action", "loop")]
    [InlineData("action", "prepare-bulk-upsert")]
    [InlineData("action", "send-email")]
    [InlineData("trigger", "new-event")]
    public void FailedStepAlwaysShowsItsErrorInsteadOfSuccessText(string type, string subtype)
    {
        const string reason = "You don't have permission to perform this action. (You do not have permission to view records in table 'A2'.)";
        var result = _formatter.FormatStepRun(new PipelineStep { Id = 9, Type = type, Subtype = subtype, Label = "My step" },
            "{}", JsonSerializer.Serialize(new { ErrorMessage = reason, ExceptionType = "PipelineNonRetryableException" }),
            "Failed", "test", DateTime.UtcNow, DateTime.UtcNow);

        using var parsed = JsonDocument.Parse(result.OutputContextJson);
        var output = parsed.RootElement.GetProperty("Output");
        output.GetProperty("Status").GetString().Should().Be("Failed");
        output.GetProperty("Error").GetString().Should().Be(reason);
        output.TryGetProperty("Records Found", out _).Should().BeFalse();
        result.LogMessage.Should().Be($"My step failed: {reason}");
    }

    [Fact]
    public void FailedStepWithoutAnErrorMessageKeepsItsNormalFormatting()
    {
        var result = _formatter.FormatStepRun(new PipelineStep { Type = "query", Subtype = "search-records" },
            "{}", "{}", "Failed", "test", null, null);

        result.LogMessage.Should().NotContain("failed:");
    }

    [Fact]
    public void ForEachLoopUsesTheLoopHistoryFormat()
    {
        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "for-each" },
            JsonSerializer.Serialize(new { ItemCount = 4 }), JsonSerializer.Serialize(new { IterationCount = 4 }),
            "Success", "test", null, null);

        using var parsed = JsonDocument.Parse(result.OutputContextJson);
        parsed.RootElement.GetProperty("Output").GetProperty("Iterations").GetInt32().Should().Be(4);
        result.LogMessage.Should().Contain("Loop completed");
    }

    [Fact]
    public void AddBulkUpsertRowHistoryResolvesFieldLabelsFromStoredMetadata()
    {
        var input = JsonSerializer.Serialize(new
        {
            ParentUpsertStepRefId = "ref_prepare",
            FieldMappings = new Dictionary<string, object> { ["fid_6"] = "hardik", ["fid_7"] = "rajkot" },
            Metadata = new
            {
                table = new { name = "A2", table_id = Guid.NewGuid().ToString() },
                field_labels = new Dictionary<string, string> { ["fid_6"] = "Name", ["fid_7"] = "City" }
            }
        });

        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "add-bulk-upsert-row" },
            input, JsonSerializer.Serialize(new { RowCount = 2 }), "Success", "test", null, null);

        using var parsed = JsonDocument.Parse(result.InputContextJson);
        var friendly = parsed.RootElement.GetProperty("Input");
        friendly.GetProperty("Table").GetString().Should().Be("A2");
        friendly.GetProperty("Fields").GetProperty("Name").GetString().Should().Be("hardik");
        friendly.GetProperty("Fields").GetProperty("City").GetString().Should().Be("rajkot");
        friendly.GetProperty("Fields").TryGetProperty("fid_6", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateRecordInAnotherTenant_UsesStoredMetadataBecauseTheTableIsNotInTheOwnerTenant()
    {
        var tableId = Guid.NewGuid();
        // The owner tenant's database does not know this table.
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>())
            .Returns<AppTable>(_ => throw new PowerBase.Domain.Exceptions.NotFoundException("Table", tableId));
        var input = JsonSerializer.Serialize(new
        {
            TableId = tableId.ToString(),
            FieldMappings = new Dictionary<string, object> { ["fid_6"] = "hardik" },
            Metadata = new
            {
                table = new { name = "A2", table_id = tableId.ToString() },
                field_labels = new Dictionary<string, string> { ["fid_6"] = "Name" }
            }
        });

        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "create-record" },
            input, JsonSerializer.Serialize(new { CreatedRecordPublicId = Guid.NewGuid() }), "Success", "test", null, null);

        using var parsed = JsonDocument.Parse(result.InputContextJson);
        var friendly = parsed.RootElement.GetProperty("Input");
        friendly.GetProperty("Table").GetString().Should().Be("A2");
        friendly.GetProperty("Fields").GetProperty("Name").GetString().Should().Be("hardik");
    }

    [Fact]
    public void CopyRecordsAcrossTenants_UsesStoredMetadataForBothTables()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<AppTable>(_ => throw new PowerBase.Domain.Exceptions.NotFoundException("Table", Guid.Empty));
        var input = JsonSerializer.Serialize(new
        {
            SourceTable = source.ToString(), DestinationTable = destination.ToString(),
            SourceFields = new[] { "fid_6" }, DestinationFields = new[] { "fid_6" }, MergeField = "fid_6",
            Metadata = new
            {
                tables = new[]
                {
                    new { table = new { name = "A1", table_id = source.ToString() }, field_labels = new Dictionary<string, string> { ["fid_6"] = "Name" } },
                    new { table = new { name = "A2", table_id = destination.ToString() }, field_labels = new Dictionary<string, string> { ["fid_6"] = "Full name" } }
                }
            }
        });

        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "copy-records" },
            input, "{}", "Success", "test", null, null);

        using var parsed = JsonDocument.Parse(result.InputContextJson);
        var friendly = parsed.RootElement.GetProperty("Input");
        friendly.GetProperty("Source Table").GetString().Should().Be("A1");
        friendly.GetProperty("Destination Table").GetString().Should().Be("A2");
        friendly.GetProperty("Source Fields")[0].GetString().Should().Be("Name");
        friendly.GetProperty("Destination Fields")[0].GetString().Should().Be("Full name");
    }

    [Fact]
    public void AddBulkUpsertRowWithoutMetadataKeepsRawKeys()
    {
        var input = JsonSerializer.Serialize(new { FieldMappings = new Dictionary<string, object> { ["fid_6"] = "x" } });

        var result = _formatter.FormatStepRun(new PipelineStep { Type = "action", Subtype = "add-bulk-upsert-row" },
            input, "{}", "Success", "test", null, null);

        using var parsed = JsonDocument.Parse(result.InputContextJson);
        parsed.RootElement.GetProperty("Input").GetProperty("Fields").GetProperty("fid_6").GetString().Should().Be("x");
    }

    [Fact]
    public async Task InitializeAsync_CachesNamesAndMetadata_WithoutCausingNPlusOneQueries()
    {
        // Arrange
        var pipelineId = 1L;
        var userId = 5L;

        var pipeline = new Pipeline { Id = pipelineId, Name = "Sales Pipeline", AppId = 10L, PublicId = Guid.NewGuid() };
        var app = new App { Id = 10L, Name = "CRM App" };
        var user = new User { Id = userId, PublicId = Guid.NewGuid(), Name = "John Doe", Email = "john@example.com" };

        _pipelineRepo.GetByIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(pipeline);
        _appRepo.GetPublicIdByIdAsync(10L, Arg.Any<CancellationToken>()).Returns(app.PublicId);
        _appRepo.GetByPublicIdAsync(app.PublicId, Arg.Any<CancellationToken>()).Returns(app);
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        await _formatter.InitializeAsync(pipelineId, userId, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).GetByIdAsync(pipelineId, Arg.Any<CancellationToken>());
        await _userRepo.Received(1).GetByIdAsync(userId, Arg.Any<CancellationToken>());
    }

    private async Task<string> NewEventMessageAsync(List<AppField> fields, Dictionary<string, object> newValues, Guid recordId,
        Dictionary<string, object?>? storedRow = null)
    {
        var pipeline = new Pipeline { Id = 1, Name = "P", AppId = 10L, PublicId = Guid.NewGuid() };
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(pipeline);
        var tableGuid = Guid.NewGuid();
        var table = new AppTable { Id = 20, Name = "Order", PublicId = tableGuid };
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);
        if (storedRow != null)
            _recordRepo.GetByPublicIdAsync(table, fields, recordId, Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>()).ReturnsForAnyArgs(storedRow);
        var step = new PipelineStep { Id = 100, Type = "trigger", Subtype = "new-event", RefId = "t",
            ConfigJson = JsonSerializer.Serialize(new { TriggerOnAdded = true }) };
        var raw = JsonSerializer.Serialize(new { MessageId = Guid.NewGuid().ToString(), PipelineId = 1, EventType = "Added",
            TablePublicId = tableGuid.ToString(), RecordPublicId = recordId.ToString(), NewValues = newValues,
            EventTimestamp = "2026-08-14T14:30:49Z" });
        await _formatter.InitializeAsync(1, 0, CancellationToken.None);
        return _formatter.FormatStepRun(step, raw, null, "Success", "c", DateTime.UtcNow, DateTime.UtcNow).LogMessage;
    }

    private async Task<string> RecordStepMessageAsync(string subtype, object input, object output, Dictionary<string, object?>? storedRow = null, Guid? recordId = null)
    {
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, Name = "P", AppId = 10L, PublicId = Guid.NewGuid() });
        var tableGuid = (Guid)input.GetType().GetProperty("TableGuid")!.GetValue(input)!;
        var fields = new List<AppField> { new() { Id = 2, Fid = 6, Name = "Name", TypeCode = "Text" } };
        var table = new AppTable { Id = 20, Name = "Order", PublicId = tableGuid };
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);
        if (storedRow != null) _recordRepo.GetByPublicIdAsync(table, fields, recordId!.Value, default).ReturnsForAnyArgs(storedRow);
        await _formatter.InitializeAsync(1, 0, CancellationToken.None);
        var step = new PipelineStep { Id = 100, Type = "action", Subtype = subtype, RefId = "s", ConfigJson = "{}" };
        return _formatter.FormatStepRun(step, JsonSerializer.Serialize(input), JsonSerializer.Serialize(output), "Success", "c", DateTime.UtcNow, DateTime.UtcNow).LogMessage;
    }

    [Fact]
    public async Task DeleteHistory_UsesTheNameCapturedBeforeTheDelete_NotTheGuid()
    {
        var t = Guid.NewGuid(); var r = Guid.NewGuid();
        var message = await RecordStepMessageAsync("delete-record",
            new { TableGuid = t, TableId = t.ToString(), TargetRecordId = r.ToString() },
            new { DeletedRecordPublicId = r.ToString(), DeletedRecordName = "Acme order" });
        message.Should().Contain("\"Acme order\"").And.NotContain(r.ToString());
    }

    [Fact]
    public async Task LookupHistory_NamesTheRecord_NotTheGuid()
    {
        var t = Guid.NewGuid(); var r = Guid.NewGuid();
        var message = await RecordStepMessageAsync("look-up-record",
            new { TableGuid = t, TablePublicId = t.ToString(), RecordId = r.ToString() }, new { },
            new Dictionary<string, object?> { ["f_6"] = "Acme order" }, r);
        message.Should().Contain("\"Acme order\"").And.NotContain(r.ToString());
    }

    [Fact]
    public void RecordDisplayName_SkipsGuidLikeValues_AndFallsBackToTheIdOnlyWhenNothingIsReadable()
    {
        var id = Guid.NewGuid().ToString();
        var fields = new List<AppField> { new() { Id = 1, Fid = 5, Name = "Ref", TypeCode = "Text" } };
        PipelineRecordDisplayName.Resolve(new AppTable(), fields, new Dictionary<string, object?> { ["fid_5"] = "  " }, id).Should().Be(id);
    }

    [Fact]
    public async Task NewEventHistory_NamesTheRecordByItsFirstPlainValue_NotByItsGuid()
    {
        var id = Guid.NewGuid();
        var fields = new List<AppField> {
            new() { Id = 1, Fid = 5, Name = "Sum of Amount", TypeCode = "Summary" },
            new() { Id = 2, Fid = 6, Name = "Order No", TypeCode = "AutoNumber" } };
        var message = await NewEventMessageAsync(fields, new() { ["fid_5"] = 1800, ["fid_6"] = "ORD-1042" }, id);
        message.Should().Contain("\"ORD-1042\"").And.NotContain(id.ToString());
    }

    [Fact]
    public async Task NewEventHistory_ReadsTheStoredRecord_WhenTheEventCarriesNoUsableName()
    {
        var id = Guid.NewGuid();
        var fields = new List<AppField> {
            new() { Id = 1, Fid = 5, Name = "Sum of Amount", TypeCode = "Summary" },
            new() { Id = 2, Fid = 6, Name = "Name", TypeCode = "Text" } };
        var message = await NewEventMessageAsync(fields, new() { ["fid_5"] = 1800 }, id,
            new Dictionary<string, object?> { ["f_6"] = "Acme order" });
        message.Should().Contain("\"Acme order\"").And.NotContain(id.ToString());
    }

    [Fact]
    public async Task FormatStepRun_OnNewEventTriggerAdded_GeneratesCorrectTreeJson()
    {
        // Arrange
        var pipelineId = 1L;
        var pipeline = new Pipeline { Id = pipelineId, Name = "Sales Pipeline", AppId = 10L, PublicId = Guid.NewGuid() };
        _pipelineRepo.GetByIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var tableGuid = Guid.NewGuid();
        var table = new AppTable { Id = 20, Name = "Customer", PublicId = tableGuid };
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 6, Name = "Name", Label = "Customer Name", TypeCode = "text" },
            new() { Id = 2, Fid = 7, Name = "Price", Label = "Amount", TypeCode = "numeric" }
        };

        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var step = new PipelineStep
        {
            Id = 100,
            Type = "trigger",
            Subtype = "new-event",
            Label = "On Customer Created",
            RefId = "trg_1",
            ConfigJson = JsonSerializer.Serialize(new
            {
                TriggerOnAdded = true,
                TriggerOnModified = false,
                TriggerOnDeleted = false,
                SubsequentFields = new List<string> { "Name", "Price" }
            })
        };

        var rawInput = JsonSerializer.Serialize(new
        {
            MessageId = Guid.NewGuid().ToString(),
            BatchId = Guid.NewGuid().ToString(),
            PipelineId = 1,
            EventType = "Added",
            TablePublicId = tableGuid.ToString(),
            RecordPublicId = Guid.NewGuid().ToString(),
            NewValues = new Dictionary<string, object>
            {
                { "fid_6", "Acme Corp" },
                { "fid_7", 5000.50 }
            },
            OldValues = (object)null,
            EventTimestamp = "2026-08-14T14:30:49Z"
        });

        await _formatter.InitializeAsync(pipelineId, 0, CancellationToken.None);

        // Act
        var result = _formatter.FormatStepRun(step, rawInput, null, "Success", "corr_123", DateTime.UtcNow, DateTime.UtcNow);

        // Assert
        result.InputContextJson.Should().NotBeNullOrEmpty();
        
        using var doc = JsonDocument.Parse(result.InputContextJson);
        var root = doc.RootElement;

        // Check Header
        root.GetProperty("Header").GetProperty("Type").GetString().Should().Be("new-event");
        root.GetProperty("Header").GetProperty("Status").GetString().Should().Be("Success");

        // Check Input
        var input = root.GetProperty("Input");
        input.GetProperty("on_add_record").GetBoolean().Should().BeTrue();
        
        var change = input.GetProperty("change");
        change.GetProperty("current").Should().NotBeNull();
        change.GetProperty("previous").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task FormatStepRun_DatetimeFormatting_GeneratesConsistentEpochMillisecondsAndIso()
    {
        // Arrange
        var pipelineId = 1L;
        var pipeline = new Pipeline { Id = pipelineId, Name = "Sales Pipeline", AppId = 10L, PublicId = Guid.NewGuid() };
        _pipelineRepo.GetByIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var tableGuid = Guid.NewGuid();
        var table = new AppTable { Id = 20, Name = "Order", PublicId = tableGuid };
        var fields = new List<AppField>
        {
            new() { Id = 10, Fid = 12, Name = "OrderDate", Label = "Ordered On", TypeCode = "DATETIME" }
        };

        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var step = new PipelineStep
        {
            Id = 101,
            Type = "trigger",
            Subtype = "new-event",
            Label = "Trigger on Order",
            RefId = "trg_2"
        };

        var rawInput = JsonSerializer.Serialize(new
        {
            EventType = "Added",
            TablePublicId = tableGuid.ToString(),
            NewValues = new Dictionary<string, object>
            {
                { "fid_12", "2026-08-14T14:30:49Z" }
            }
        });

        await _formatter.InitializeAsync(pipelineId, 0, CancellationToken.None);

        // Act
        var result = _formatter.FormatStepRun(step, rawInput, null, "Success", "corr_123", DateTime.UtcNow, DateTime.UtcNow);

        // Assert
        result.InputContextJson.Should().Contain("\"@type\":\"datetime\"");
        result.InputContextJson.Should().Contain("\"time\":1786717849000"); // 1786717849000 is epoch ms of 2026-08-14T14:30:49Z
        result.InputContextJson.Should().Contain("\"iso\":\"2026-08-14T14:30:49Z\"");
    }

    [Fact]
    public void FormatStepRun_SensitiveHeadersAndTokens_AreCorrectlyRedacted()
    {
        // Arrange
        var step = new PipelineStep
        {
            Id = 200,
            Type = "action",
            Subtype = "make-request",
            Label = "Webhook Call",
            RefId = "req_1"
        };

        var rawInput = JsonSerializer.Serialize(new
        {
            Url = "https://api.thirdparty.com/webhook",
            Method = "POST",
            Headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer sensitive-token-abc" },
                { "Content-Type", "application/json" }
            },
            Body = "{\"name\":\"test\"}"
        });

        // Act
        var result = _formatter.FormatStepRun(step, rawInput, "{}", "Success", "corr_123", DateTime.UtcNow, DateTime.UtcNow);

        // Assert
        result.InputContextJson.Should().Contain("[REDACTED]");
        result.InputContextJson.Should().NotContain("sensitive-token-abc");
    }

    [Fact]
    public void FormatStepRun_UploadFileFailure_ReportsTheErrorInsteadOfAZeroByteSuccess()
    {
        var step = new PipelineStep
        {
            Id = 201,
            Type = "file",
            Subtype = "upload-file",
            Label = "Upload a File",
            RefId = "file_1"
        };
        var rawInput = JsonSerializer.Serialize(new
        {
            FileUrl = "https://example.com/document.pdf",
            FileName = "document.pdf"
        });
        var rawOutput = JsonSerializer.Serialize(new
        {
            ErrorMessage = "The source file could not be downloaded.",
            ExceptionType = "HttpRequestException"
        });

        var result = _formatter.FormatStepRun(
            step, rawInput, rawOutput, "Failed", "corr_123", DateTime.UtcNow, DateTime.UtcNow);

        result.LogMessage.Should().Be("Upload file failed: The source file could not be downloaded.");
        result.LogMessage.Should().NotContain("successfully");
        result.OutputContextJson.Should().Contain("The source file could not be downloaded.");
        result.OutputContextJson.Should().NotContain("Uploaded File Name");
        result.OutputContextJson.Should().NotContain("File Size");
    }

    [Fact]
    public void FormatStepRun_ConditionStep_FormatsCriteriaAndBranch()
    {
        // Arrange
        var step = new PipelineStep
        {
            Id = 300,
            Type = "control",
            Subtype = "condition",
            Label = "Check Amount",
            RefId = "cond_1"
        };

        var rawInput = JsonSerializer.Serialize(new
        {
            LeftOperand = "5000",
            Operator = ">",
            RightOperand = "2000"
        });

        var rawOutput = JsonSerializer.Serialize(new
        {
            Matched = true,
            EvaluatedBranch = "children"
        });

        // Act
        var result = _formatter.FormatStepRun(step, rawInput, rawOutput, "Success", "corr_123", DateTime.UtcNow, DateTime.UtcNow);

        // Assert
        result.OutputContextJson.Should().Contain("\"Matched\":true");
        result.OutputContextJson.Should().Contain("\"Executed Branch\":\"Yes\"");
        result.LogMessage.Should().Be("Condition matched. Executed the Yes branch.");
    }

    [Fact]
    public void FormatStepRun_CommitUpsert_WhenSuccess_FormatsInsertedAndUpdatedCounts()
    {
        // Arrange
        var step = new PipelineStep
        {
            Id = 400,
            Type = "action",
            Subtype = "commit-upsert",
            Label = "Commit Bulk Upsert",
            RefId = "commit_1"
        };

        var rawInput = JsonSerializer.Serialize(new { ParentUpsertStepRefId = "prep_1" });
        var rawOutput = JsonSerializer.Serialize(new { InsertedCount = 2, UpdatedCount = 1, Status = "Committed" });

        // Act
        var result = _formatter.FormatStepRun(step, rawInput, rawOutput, "Success", "corr_123", DateTime.UtcNow, DateTime.UtcNow);

        // Assert
        result.LogMessage.Should().Be("Committed bulk upsert. Inserted 2 records and updated 1 records.");
        result.OutputContextJson.Should().Contain("\"Inserted Record Count\":2");
        result.OutputContextJson.Should().Contain("\"Updated Record Count\":1");
    }

    [Fact]
    public void FormatStepRun_CommitUpsert_WhenFailed_SurfacesActualErrorMessage()
    {
        // Arrange
        var step = new PipelineStep
        {
            Id = 401,
            Type = "action",
            Subtype = "commit-upsert",
            Label = "Commit Bulk Upsert",
            RefId = "commit_1"
        };

        var rawInput = JsonSerializer.Serialize(new { ParentUpsertStepRefId = "prep_1" });
        var rawOutput = JsonSerializer.Serialize(new
        {
            ErrorMessage = "Invalid column name 'f_3'.",
            ExceptionType = "SqlException"
        });

        // Act
        var result = _formatter.FormatStepRun(step, rawInput, rawOutput, "Failed", "corr_123", DateTime.UtcNow, DateTime.UtcNow);

        // Assert
        result.LogMessage.Should().Be("Failed to commit bulk upsert: Invalid column name 'f_3'.");
        result.OutputContextJson.Should().Contain("Invalid column name");
        result.OutputContextJson.Should().Contain("\"Status\":\"Failed\"");
    }
}
