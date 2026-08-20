using System;
using System.Collections.Generic;
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
}
