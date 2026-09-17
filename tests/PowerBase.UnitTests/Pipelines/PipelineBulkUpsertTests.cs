using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineBulkUpsertTests
{
    private readonly IRecordRepository _recordRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IDbTransaction _dbTx;
    private readonly IPipelineStepIdempotencyRepository _idempotencyRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IFileStorageService _fileStorageService;
    private readonly IPipelineRecordSearchService _recordSearchService;
    private readonly PipelineEngine _engine;

    private readonly AppTable _testTable;
    private readonly List<AppField> _testFields;
    private readonly Guid _tablePublicId = Guid.NewGuid();

    public PipelineBulkUpsertTests()
    {
        _recordRepo = Substitute.For<IRecordRepository>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
        _uow = Substitute.For<ITenantUnitOfWork>();
        _dbTx = Substitute.For<IDbTransaction>();
        _uow.Transaction.Returns(_dbTx);
        _idempotencyRepo = Substitute.For<IPipelineStepIdempotencyRepository>();
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _fileStorageService = Substitute.For<IFileStorageService>();
        _recordSearchService = Substitute.For<IPipelineRecordSearchService>();

        var execOptions = new PipelineExecutionOptions();
        var logger = Substitute.For<ILogger<PipelineEngine>>();
        var auditFormatter = Substitute.For<IPipelineAuditFormatter>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(_recordSearchService);

        _engine = new PipelineEngine(
            _pipelineRepo,
            _recordRepo,
            _recordWriteService,
            _tableRepo,
            _fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            _fileStorageService,
            Options.Create(execOptions),
            logger,
            _triggerInterceptor,
            _uow,
            auditFormatter,
            Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(),
            serviceProvider,
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            _idempotencyRepo
        );

        _testTable = new AppTable { Id = 10, AppId = 1, PublicId = _tablePublicId, Name = "Customers" };
        _testFields = new List<AppField>
        {
            new() { Id = 1, Fid = 3, Name = "Record ID#", TypeCode = "RecordId", PhysicalColumnName = "Id", IsSystem = true },
            new() { Id = 2, Fid = 6, Name = "Email", TypeCode = "Email" },
            new() { Id = 3, Fid = 7, Name = "FullName", TypeCode = "Text" },
            new() { Id = 4, Fid = 8, Name = "Age", TypeCode = "Numeric" },
            new() { Id = 5, Fid = 9, Name = "Notes", TypeCode = "TextMultiLine" },
            new() { Id = 6, Fid = 10, Name = "BirthDate", TypeCode = "Date" },
            new() { Id = 7, Fid = 11, Name = "Bio", TypeCode = "RichText" }
        };

        _tableRepo.GetByPublicIdAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(_testTable);
        _fieldRepo.ListByTableAsync(_testTable.Id, Arg.Any<CancellationToken>()).Returns(_testFields);
    }

    private async Task<string> RunStepAsync(
        PipelineStep step,
        Dictionary<string, object> contextDict,
        List<PipelineStep>? allSteps = null)
    {
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var task = (Task<string>)method!.Invoke(_engine, new object[] {
            step, "{}", contextDict, allSteps ?? new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_test",
            _recordRepo, _tableRepo, _fieldRepo, _recordWriteService, _triggerInterceptor, _uow, _idempotencyRepo, _fileStorageService, _recordSearchService, CancellationToken.None
        })!;

        return await task;
    }

    #region Mandatory Condition 1: Unified Transaction Connection

    [Fact]
    public async Task MandatoryCondition1_UnifiedTransaction_AllLookupsAndWritesUseSameUowTransaction()
    {
        // Arrange: prepare session, add 2 rows (1 insert, 1 update via merge key)
        var prepStep = new PipelineStep
        {
            RefId = "prep_1",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "fid_6"
            })
        };

        var contextDict = new Dictionary<string, object> { ["_CreatedBy"] = 42L };
        await RunStepAsync(prepStep, contextDict);

        var addRow1 = new PipelineStep
        {
            RefId = "row_1",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "steps.prep_1",
                rowValues = new Dictionary<string, object?> { ["fid_6"] = "alice@example.com", ["fid_7"] = "Alice" }
            })
        };
        var addRow2 = new PipelineStep
        {
            RefId = "row_2",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "steps.prep_1",
                rowValues = new Dictionary<string, object?> { ["f_6"] = "bob@example.com", ["fid_7"] = "Bob" }
            })
        };
        await RunStepAsync(addRow1, contextDict);
        await RunStepAsync(addRow2, contextDict);

        var existingBobGuid = Guid.NewGuid();
        var existingBobRecord = new Dictionary<string, object?>
        {
            ["Id"] = 102L,
            ["id"] = 102L,
            ["publicId"] = existingBobGuid,
            ["f_6"] = "bob@example.com",
            ["fid_7"] = "Old Bob"
        };

        // Existing lookup returns Bob on the exact same transaction
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["bob@example.com"] = existingBobRecord });

        var newAliceGuid = Guid.NewGuid();
        _recordRepo.CreateAsync(_testTable, _testFields, Arg.Any<IReadOnlyDictionary<long, object?>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(newAliceGuid);

        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(_testTable, Arg.Any<IReadOnlyCollection<Guid>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [newAliceGuid] = 101L });

        var commitStep = new PipelineStep
        {
            RefId = "commit_1",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "steps.prep_1"
            })
        };

        // Act
        var resultJson = await RunStepAsync(commitStep, contextDict);

        // Assert: UOW transaction was begun and committed
        await _uow.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());

        // Assert: GetRowsByColumnValuesAsync called on _dbTx
        await _recordRepo.Received(1).GetBulkUpsertRowsByColumnValuesAsync(
            _testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>());

        // Assert: CreateAsync called on _dbTx
        await _recordRepo.Received(1).CreateAsync(
            _testTable, _testFields, Arg.Any<IReadOnlyDictionary<long, object?>>(), _dbTx, Arg.Any<CancellationToken>());

        // Assert: ApplyAsync called for update on _dbTx with suppressInterception: true
        await _recordWriteService.Received(1).ApplyAsync(
            _testTable, _testFields, existingBobGuid, Arg.Any<IReadOnlyDictionary<long, object?>>(),
            AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), _dbTx, suppressInterception: true,
            onIndexMessageCreated: null, existingRecord: Arg.Any<IReadOnlyDictionary<string, object?>>());

        // Assert: InterceptBulkAsync called for inserted and modified changes
        await _triggerInterceptor.Received(1).InterceptBulkAsync(
            _testTable, _testFields, Arg.Is<IReadOnlyList<PipelineRecordChange>>(l => l.Count == 1 && l[0].EventType == PipelineRecordEventType.Added),
            Arg.Any<Guid>(), Arg.Any<Guid>(), 42L, Arg.Any<CancellationToken>());

        await _triggerInterceptor.Received(1).InterceptBulkAsync(
            _testTable, _testFields, Arg.Is<IReadOnlyList<PipelineRecordChange>>(l => l.Count == 1 && l[0].EventType == PipelineRecordEventType.Modified),
            Arg.Any<Guid>(), Arg.Any<Guid>(), 42L, Arg.Any<CancellationToken>());

        // Result validation: Structured contract
        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);
        result.GetProperty("committed").GetBoolean().Should().BeTrue();
        result.GetProperty("status").GetString().Should().Be("Committed");
        result.GetProperty("inserted_count").GetInt32().Should().Be(1);
        result.GetProperty("updated_count").GetInt32().Should().Be(1);
        result.GetProperty("total_records").GetInt32().Should().Be(2);
        result.GetProperty("created_record_ids").EnumerateArray().Select(x => x.GetInt64()).Should().ContainSingle().Which.Should().Be(101L);
        result.GetProperty("updated_record_ids").EnumerateArray().Select(x => x.GetInt64()).Should().ContainSingle().Which.Should().Be(102L);
        result.GetProperty("errors").GetArrayLength().Should().Be(0);

        // CamelCase aliases
        result.GetProperty("insertedCount").GetInt32().Should().Be(1);
        result.GetProperty("updatedCount").GetInt32().Should().Be(1);
        result.GetProperty("unchangedCount").GetInt32().Should().Be(0);
    }

    #endregion

    #region Mandatory Condition 2: Contract Normalization

    [Fact]
    public async Task MandatoryCondition2_ContractNormalization_SupportsCamelCasePascalCaseAndBothRowFormats()
    {
        // Test PascalCase prepare step
        var prepStepPascal = new PipelineStep
        {
            RefId = "prep_pascal",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                TableLabel = _tablePublicId.ToString(),
                MergeKeyFid = "f_6"
            })
        };

        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStepPascal, contextDict);

        // Add row using FieldMappings list format
        var addRowFieldMappings = new PipelineStep
        {
            RefId = "row_fm",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ParentUpsertStepRefId = "prep_pascal", // without steps. prefix
                FieldMappings = new[]
                {
                    new { Field = "f_6", Value = "charlie@example.com" },
                    new { Field = "7", Value = "Charlie" }
                }
            })
        };

        // Add row using rowValues map format with camelCase
        var addRowRowValues = new PipelineStep
        {
            RefId = "row_rv",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "steps.prep_pascal", // with steps. prefix
                rowValues = new Dictionary<string, object?>
                {
                    ["f_6"] = "david@example.com",
                    ["fid_8"] = 30
                }
            })
        };

        await RunStepAsync(addRowFieldMappings, contextDict);
        await RunStepAsync(addRowRowValues, contextDict);

        // Verify session normalized rows
        var sessions = (Dictionary<string, PipelineEngine.BulkUpsertSession>)contextDict["_bulkUpsertSessions"];
        var session = sessions["prep_pascal"];
        session.Rows.Should().HaveCount(2);

        // Row 1: fid 6 and fid 7
        session.Rows[0][6].Should().Be("charlie@example.com");
        session.Rows[0][7].Should().Be("Charlie");

        // Row 2: fid 6 and fid 8
        session.Rows[1][6].Should().Be("david@example.com");
        session.Rows[1][8].Should().Be(30m);
    }

    #endregion

    #region Mandatory Condition 3: Strict In-Batch Duplicate Detection

    [Fact]
    public async Task MandatoryCondition3_DuplicateDetection_RejectsBatchInMemoryBeforeTransaction()
    {
        // Arrange: prepare session with mergeKeyFid = fid_6
        var prepStep = new PipelineStep
        {
            RefId = "prep_dup",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };

        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        // Add 3 rows, where row 0 and row 2 have the same email
        var row0 = new PipelineStep
        {
            RefId = "r0",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_dup",
                rowValues = new Dictionary<string, object?> { ["f_6"] = "duplicate@example.com", ["fid_7"] = "First" }
            })
        };
        var row1 = new PipelineStep
        {
            RefId = "r1",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_dup",
                rowValues = new Dictionary<string, object?> { ["f_6"] = "unique@example.com", ["fid_7"] = "Middle" }
            })
        };
        var row2 = new PipelineStep
        {
            RefId = "r2",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_dup",
                rowValues = new Dictionary<string, object?> { ["f_6"] = "duplicate@example.com", ["fid_7"] = "Second" }
            })
        };

        await RunStepAsync(row0, contextDict);
        await RunStepAsync(row1, contextDict);
        await RunStepAsync(row2, contextDict);

        var commitStep = new PipelineStep
        {
            RefId = "commit_dup",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_dup" })
        };

        // Act & Assert
        var act = async () => await RunStepAsync(commitStep, contextDict);
        var ex = await act.Should().ThrowAsync<PipelineBulkUpsertException>();
        ex.Which.ErrorCode.Should().Be("DUPLICATE_MERGE_KEY_IN_BATCH");
        ex.Which.RowIndex.Should().Be(3);
        ex.Which.FieldFid.Should().Be(6);
        ex.WithMessage("*duplicate@example.com*");

        // Assert: Database transaction was NEVER opened
        await _uow.DidNotReceive().BeginAsync(Arg.Any<CancellationToken>());

        // Assert: Bulk session was cleaned up
        var sessions = (Dictionary<string, PipelineEngine.BulkUpsertSession>)contextDict["_bulkUpsertSessions"];
        sessions.ContainsKey("prep_dup").Should().BeFalse();
    }

    #endregion

    #region Plan V2 Cases 1–6 (Record ID vs Merge Key)

    [Fact]
    public async Task PlanV2_Case1_RecordIdOnly_Exists_UpdatesRecord()
    {
        // Case 1: Record ID only provided and exists -> UPDATE
        var prepStep = new PipelineStep
        {
            RefId = "prep_c1",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString() })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c1",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c1",
                rowValues = new Dictionary<string, object?> { ["fid_3"] = 42L, ["fid_7"] = "Updated Name" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        var existingGuid = Guid.NewGuid();
        var existingRow = new Dictionary<string, object?>
        {
            ["Id"] = 42L,
            ["publicId"] = existingGuid,
            ["fid_7"] = "Old Name"
        };

        _recordRepo.GetBulkUpsertRowsByIdsAsync(_testTable, _testFields, Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(42L)), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [42L] = existingRow });

        var commitStep = new PipelineStep
        {
            RefId = "commit_c1",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c1" })
        };

        var resultJson = await RunStepAsync(commitStep, contextDict);

        await _recordWriteService.Received(1).ApplyAsync(
            _testTable, _testFields, existingGuid, Arg.Any<IReadOnlyDictionary<long, object?>>(),
            AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), _dbTx, suppressInterception: true,
            onIndexMessageCreated: null, existingRecord: Arg.Any<IReadOnlyDictionary<string, object?>>());

        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);
        result.GetProperty("updated_count").GetInt32().Should().Be(1);
        result.GetProperty("updated_record_ids").EnumerateArray().Select(x => x.GetInt64()).Should().ContainSingle().Which.Should().Be(42L);
    }

    [Fact]
    public async Task PlanV2_Case1_RecordIdOnly_NotFound_ThrowsException()
    {
        // Case 1 not found: Record ID only provided & not found -> RECORD_ID_NOT_FOUND
        var prepStep = new PipelineStep
        {
            RefId = "prep_c1_nf",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString() })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c1_nf",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c1_nf",
                rowValues = new Dictionary<string, object?> { ["fid_3"] = 9999L, ["fid_7"] = "Ghost" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        _recordRepo.GetBulkUpsertRowsByIdsAsync(_testTable, _testFields, Arg.Any<IReadOnlyCollection<long>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>());

        var commitStep = new PipelineStep
        {
            RefId = "commit_c1_nf",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c1_nf" })
        };

        var act = async () => await RunStepAsync(commitStep, contextDict);
        var ex = await act.Should().ThrowAsync<PipelineBulkUpsertException>();
        ex.Which.ErrorCode.Should().Be("RECORD_ID_NOT_FOUND");
        ex.Which.RowIndex.Should().Be(1);
        ex.Which.FieldFid.Should().Be(3);
        ex.WithMessage("*Record ID 9999 was not found*");
    }

    [Fact]
    public async Task PlanV2_Case2_MergeKeyOnly_Exists_UpdatesRecord()
    {
        // Case 2: Merge Key only, exists -> UPDATE
        var prepStep = new PipelineStep
        {
            RefId = "prep_c2_ex",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c2_ex",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c2_ex",
                rowValues = new Dictionary<string, object?> { ["f_6"] = "frank@example.com", ["fid_7"] = "Frank Updated" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        var existingGuid = Guid.NewGuid();
        var existingRow = new Dictionary<string, object?>
        {
            ["Id"] = 77L,
            ["publicId"] = existingGuid,
            ["f_6"] = "frank@example.com",
            ["fid_7"] = "Frank Original"
        };

        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["frank@example.com"] = existingRow });

        var commitStep = new PipelineStep
        {
            RefId = "commit_c2_ex",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c2_ex" })
        };

        var resultJson = await RunStepAsync(commitStep, contextDict);

        await _recordWriteService.Received(1).ApplyAsync(
            _testTable, _testFields, existingGuid, Arg.Any<IReadOnlyDictionary<long, object?>>(),
            AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), _dbTx, suppressInterception: true,
            onIndexMessageCreated: null, existingRecord: Arg.Any<IReadOnlyDictionary<string, object?>>());

        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);
        result.GetProperty("updated_count").GetInt32().Should().Be(1);
        result.GetProperty("updated_record_ids").EnumerateArray().Select(x => x.GetInt64()).Should().ContainSingle().Which.Should().Be(77L);
    }

    [Fact]
    public async Task PlanV2_Case2_MergeKeyOnly_NotFound_InsertsRecord()
    {
        // Case 2: Merge Key only, not found -> INSERT
        var prepStep = new PipelineStep
        {
            RefId = "prep_c2_nf",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c2_nf",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c2_nf",
                rowValues = new Dictionary<string, object?> { ["f_6"] = "grace@example.com", ["fid_7"] = "Grace" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>()); // No match

        var newGuid = Guid.NewGuid();
        _recordRepo.CreateAsync(_testTable, _testFields, Arg.Any<IReadOnlyDictionary<long, object?>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(newGuid);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(_testTable, Arg.Any<IReadOnlyCollection<Guid>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [newGuid] = 88L });

        var commitStep = new PipelineStep
        {
            RefId = "commit_c2_nf",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c2_nf" })
        };

        var resultJson = await RunStepAsync(commitStep, contextDict);

        await _recordRepo.Received(1).CreateAsync(
            _testTable, _testFields, Arg.Any<IReadOnlyDictionary<long, object?>>(), _dbTx, Arg.Any<CancellationToken>());

        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);
        result.GetProperty("inserted_count").GetInt32().Should().Be(1);
        result.GetProperty("created_record_ids").EnumerateArray().Select(x => x.GetInt64()).Should().ContainSingle().Which.Should().Be(88L);
    }

    [Fact]
    public async Task PlanV2_Case3_BothIdentifiersProvided_IdentifySameRecord_UpdatesRecord()
    {
        // Case 3: Both Record ID and Merge Key provided and identify the SAME record -> UPDATE
        var prepStep = new PipelineStep
        {
            RefId = "prep_c3",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c3",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c3",
                rowValues = new Dictionary<string, object?> { ["fid_3"] = 50L, ["f_6"] = "eva@example.com", ["fid_7"] = "Eva Updated" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        var existingGuid = Guid.NewGuid();
        var existingRow = new Dictionary<string, object?>
        {
            ["Id"] = 50L,
            ["publicId"] = existingGuid,
            ["f_6"] = "eva@example.com",
            ["fid_7"] = "Eva Original"
        };

        _recordRepo.GetBulkUpsertRowsByIdsAsync(_testTable, _testFields, Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(50L)), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [50L] = existingRow });

        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["eva@example.com"] = existingRow });

        var commitStep = new PipelineStep
        {
            RefId = "commit_c3",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c3" })
        };

        var resultJson = await RunStepAsync(commitStep, contextDict);

        await _recordWriteService.Received(1).ApplyAsync(
            _testTable, _testFields, existingGuid,
            Arg.Any<IReadOnlyDictionary<long, object?>>(),
            AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), _dbTx, suppressInterception: true,
            onIndexMessageCreated: null, existingRecord: Arg.Any<IReadOnlyDictionary<string, object?>>());

        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);
        result.GetProperty("updated_count").GetInt32().Should().Be(1);
        result.GetProperty("updated_record_ids").EnumerateArray().Select(x => x.GetInt64()).Should().ContainSingle().Which.Should().Be(50L);
    }

    [Fact]
    public async Task PlanV2_Case4_RecordIdA_MergeKeyB_RejectsWithMismatchErrorCode()
    {
        // Case 4: Record ID identifies Record A (ID 10), but Merge Key identifies Record B (ID 20)
        // MUST reject the row/batch, NOT update Record A, and return RECORD_ID_MERGE_KEY_MISMATCH
        var prepStep = new PipelineStep
        {
            RefId = "prep_c4_mismatch",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c4_mismatch",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c4_mismatch",
                rowValues = new Dictionary<string, object?> { ["fid_3"] = 10L, ["f_6"] = "recordB@example.com", ["fid_7"] = "Malicious Mismatch" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        var recordAGuid = Guid.NewGuid();
        var recordA = new Dictionary<string, object?>
        {
            ["Id"] = 10L,
            ["publicId"] = recordAGuid,
            ["f_6"] = "recordA@example.com",
            ["fid_7"] = "Record A"
        };

        var recordBGuid = Guid.NewGuid();
        var recordB = new Dictionary<string, object?>
        {
            ["Id"] = 20L,
            ["publicId"] = recordBGuid,
            ["f_6"] = "recordB@example.com",
            ["fid_7"] = "Record B"
        };

        _recordRepo.GetBulkUpsertRowsByIdsAsync(_testTable, _testFields, Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(10L)), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [10L] = recordA });

        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["recordB@example.com"] = recordB });

        var commitStep = new PipelineStep
        {
            RefId = "commit_c4_mismatch",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c4_mismatch" })
        };

        // Act & Assert
        var act = async () => await RunStepAsync(commitStep, contextDict);
        var ex = await act.Should().ThrowAsync<PipelineBulkUpsertException>();
        ex.Which.ErrorCode.Should().Be("RECORD_ID_MERGE_KEY_MISMATCH");
        ex.Which.RowIndex.Should().Be(1);
        ex.Which.FieldFid.Should().Be(6);
        ex.Which.FieldName.Should().Be("Email");
        ex.WithMessage("*Record ID 10 (Record 10)*");
        ex.WithMessage("*Merge Key 'recordB@example.com' (Record 20)*");

        // Assert: Record A was NEVER updated
        await _recordWriteService.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default!, default!, default, default, default);
    }

    [Fact]
    public async Task PlanV2_Case5_RecordIdMissing_MergeKeyExisting_RejectsWithRecordIdNotFound()
    {
        // Case 5: Record ID is supplied but does not exist, while Merge Key identifies an existing record
        // MUST reject and NOT update the Merge Key record
        var prepStep = new PipelineStep
        {
            RefId = "prep_c5_miss",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c5_miss",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c5_miss",
                rowValues = new Dictionary<string, object?> { ["fid_3"] = 999L, ["f_6"] = "existing@example.com", ["fid_7"] = "Should Fail" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        var existingGuid = Guid.NewGuid();
        var existingRecord = new Dictionary<string, object?>
        {
            ["Id"] = 55L,
            ["publicId"] = existingGuid,
            ["f_6"] = "existing@example.com",
            ["fid_7"] = "Existing Person"
        };

        // Record ID 999 does not exist
        _recordRepo.GetBulkUpsertRowsByIdsAsync(_testTable, _testFields, Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(999L)), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>());

        // Merge key exists
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["existing@example.com"] = existingRecord });

        var commitStep = new PipelineStep
        {
            RefId = "commit_c5_miss",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c5_miss" })
        };

        // Act & Assert
        var act = async () => await RunStepAsync(commitStep, contextDict);
        var ex = await act.Should().ThrowAsync<PipelineBulkUpsertException>();
        ex.Which.ErrorCode.Should().Be("RECORD_ID_NOT_FOUND");
        ex.Which.RowIndex.Should().Be(1);
        ex.Which.FieldFid.Should().Be(3);
        ex.WithMessage("*Record ID 999 was not found*");

        // Assert: Existing record was NOT updated
        await _recordWriteService.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default!, default!, default, default, default);
    }

    [Fact]
    public async Task PlanV2_Case6_RecordIdExisting_NewUnusedMergeKey_UpdatesRecordAndChangesMergeKey()
    {
        // Case 6: Record ID exists and incoming Merge Key is unused -> UPDATE existing record and change Merge Key
        var prepStep = new PipelineStep
        {
            RefId = "prep_c6_newkey",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c6_newkey",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c6_newkey",
                rowValues = new Dictionary<string, object?> { ["fid_3"] = 60L, ["f_6"] = "brandnew@example.com", ["fid_7"] = "Changed Email User" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        var recordGuid = Guid.NewGuid();
        var existingRecord = new Dictionary<string, object?>
        {
            ["Id"] = 60L,
            ["publicId"] = recordGuid,
            ["f_6"] = "oldkey@example.com",
            ["fid_7"] = "User Original"
        };

        // Record ID 60 exists
        _recordRepo.GetBulkUpsertRowsByIdsAsync(_testTable, _testFields, Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(60L)), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [60L] = existingRecord });

        // Merge Key brandnew@example.com is unused (no match)
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        var commitStep = new PipelineStep
        {
            RefId = "commit_c6_newkey",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c6_newkey" })
        };

        var resultJson = await RunStepAsync(commitStep, contextDict);

        // Assert: Record 60 was updated with the new email
        await _recordWriteService.Received(1).ApplyAsync(
            _testTable, _testFields, recordGuid,
            Arg.Is<IReadOnlyDictionary<long, object?>>(d => (string)d[6]! == "brandnew@example.com"),
            AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), _dbTx, suppressInterception: true,
            onIndexMessageCreated: null, existingRecord: Arg.Any<IReadOnlyDictionary<string, object?>>());

        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);
        result.GetProperty("updated_count").GetInt32().Should().Be(1);
        result.GetProperty("updated_record_ids").EnumerateArray().Select(x => x.GetInt64()).Should().ContainSingle().Which.Should().Be(60L);
    }

    [Fact]
    public async Task PlanV2_Case6_Collision_RecordIdA_MergeKeyBelongsToB_RejectsWithMismatch()
    {
        // Case 6 Collision: Record ID A exists, but the incoming Merge Key belongs to Record B -> REJECT with RECORD_ID_MERGE_KEY_MISMATCH
        var prepStep = new PipelineStep
        {
            RefId = "prep_c6_collision",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeKeyFid = "f_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        var addRow = new PipelineStep
        {
            RefId = "r_c6_collision",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_c6_collision",
                rowValues = new Dictionary<string, object?> { ["fid_3"] = 100L, ["f_6"] = "belongs_to_200@example.com" }
            })
        };
        await RunStepAsync(addRow, contextDict);

        var record100 = new Dictionary<string, object?>
        {
            ["Id"] = 100L,
            ["publicId"] = Guid.NewGuid(),
            ["f_6"] = "original_100@example.com"
        };
        var record200 = new Dictionary<string, object?>
        {
            ["Id"] = 200L,
            ["publicId"] = Guid.NewGuid(),
            ["f_6"] = "belongs_to_200@example.com"
        };

        _recordRepo.GetBulkUpsertRowsByIdsAsync(_testTable, _testFields, Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(100L)), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [100L] = record100 });

        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["belongs_to_200@example.com"] = record200 });

        var commitStep = new PipelineStep
        {
            RefId = "commit_c6_collision",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_c6_collision" })
        };

        // Act & Assert
        var act = async () => await RunStepAsync(commitStep, contextDict);
        var ex = await act.Should().ThrowAsync<PipelineBulkUpsertException>();
        ex.Which.ErrorCode.Should().Be("RECORD_ID_MERGE_KEY_MISMATCH");
        ex.Which.RowIndex.Should().Be(1);
        ex.Which.FieldFid.Should().Be(6);
    }

    #endregion

    #region Field State Semantics (NOT_PROVIDED / NULL / EMPTY_STRING / VALUE)

    [Fact]
    public async Task FieldStateSemantics_TextPreservesEmptyString_NumericAndDateBecomeNull_NullClears_NotProvidedUntouched()
    {
        var prepStep = new PipelineStep
        {
            RefId = "prep_fs_types",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "f_6" })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        // Row provides:
        // - Text "" (fid 7) -> should remain ""
        // - TextMultiLine "" (fid 9) -> should remain ""
        // - RichText "" (fid 11) -> should remain ""
        // - Number "" (fid 8) -> should become null
        // - Date "" (fid 10) -> should become null
        // - fid 6 (Email) -> merge key
        var addRow = new PipelineStep
        {
            RefId = "r_fs_types",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_fs_types",
                rowValues = new Dictionary<string, object?>
                {
                    ["f_6"] = "semantics@example.com",
                    ["fid_7"] = "", // Text -> ""
                    ["fid_8"] = "", // Numeric -> null
                    ["fid_9"] = "", // TextMultiLine -> ""
                    ["fid_10"] = "", // Date -> null
                    ["fid_11"] = "" // RichText -> ""
                }
            })
        };
        await RunStepAsync(addRow, contextDict);

        // Verify session parsed rows
        var sessions = (Dictionary<string, PipelineEngine.BulkUpsertSession>)contextDict["_bulkUpsertSessions"];
        var session = sessions["prep_fs_types"];
        var row = session.Rows[0];

        // Text "" preserved
        row[7].Should().Be("");
        row[9].Should().Be("");
        row[11].Should().Be("");

        // Numeric and Date "" became null
        row[8].Should().BeNull();
        row[10].Should().BeNull();

        var existingGuid = Guid.NewGuid();
        var existingRow = new Dictionary<string, object?>
        {
            ["Id"] = 300L,
            ["publicId"] = existingGuid,
            ["f_6"] = "semantics@example.com",
            ["fid_7"] = "Previous Name",
            ["fid_8"] = 25,
            ["fid_9"] = "Previous Notes",
            ["fid_10"] = "2020-01-01",
            ["fid_11"] = "<p>Previous Bio</p>"
        };

        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["semantics@example.com"] = existingRow });

        var commitStep = new PipelineStep
        {
            RefId = "commit_fs_types",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_fs_types" })
        };

        await RunStepAsync(commitStep, contextDict);

        // Assert: ApplyAsync received empty string for text and null for numeric/date
        await _recordWriteService.Received(1).ApplyAsync(
            _testTable, _testFields, existingGuid,
            Arg.Is<IReadOnlyDictionary<long, object?>>(d =>
                (string)d[7]! == "" &&
                (string)d[9]! == "" &&
                (string)d[11]! == "" &&
                d[8] == null &&
                d[10] == null
            ),
            AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), _dbTx, suppressInterception: true,
            onIndexMessageCreated: null, existingRecord: Arg.Any<IReadOnlyDictionary<string, object?>>());
    }

    #endregion

    #region Zero-Query Add Bulk Upsert Row

    [Fact]
    public async Task ZeroQueryAddBulkUpsertRow_UsesCachedMetadata_MakesZeroDatabaseCalls()
    {
        // 1. Prepare bulk upsert caches metadata
        var prepStep = new PipelineStep
        {
            RefId = "prep_zq",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString() })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        // Clear mock invocations from prepare
        _tableRepo.ClearReceivedCalls();
        _fieldRepo.ClearReceivedCalls();
        _recordRepo.ClearReceivedCalls();

        // 2. Add 10 rows
        for (int i = 0; i < 10; i++)
        {
            var addRow = new PipelineStep
            {
                RefId = $"r_{i}",
                Type = "action",
                Subtype = "add-bulk-upsert-row",
                ConfigJson = JsonSerializer.Serialize(new
                {
                    parentUpsertStepRefId = "prep_zq",
                    rowValues = new Dictionary<string, object?> { ["fid_7"] = $"Row {i}" }
                })
            };
            await RunStepAsync(addRow, contextDict);
        }

        // Assert: Absolutely ZERO repository calls during all 10 add row steps
        await _tableRepo.DidNotReceiveWithAnyArgs().GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _fieldRepo.DidNotReceiveWithAnyArgs().ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default, default, default, default, default, default);
    }

    #endregion

    #region Root Prepare Execution & MergeField Mapping Tests



    [Fact]
    public async Task PrepareBulkUpsertConfig_MergeFieldProperty_ResolvesCustomMergeFieldFid6_AndPerformsInsertAndUpdate()
    {
        // Arrange: Prepare step uses frontend property "mergeField": "fid_6"
        var prepStep = new PipelineStep
        {
            RefId = "prep_custom_mf",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeField = "fid_6"
            })
        };
        var contextDict = new Dictionary<string, object>();
        await RunStepAsync(prepStep, contextDict);

        // Add 2 rows with merge key fid_6: row 1 (new), row 2 (existing)
        var addRow1 = new PipelineStep
        {
            RefId = "row_mf_1",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_custom_mf",
                rowValues = new Dictionary<string, object?> { ["fid_6"] = "newhotel@example.com", ["fid_7"] = "New Hotel" }
            })
        };
        var addRow2 = new PipelineStep
        {
            RefId = "row_mf_2",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_custom_mf",
                rowValues = new Dictionary<string, object?> { ["fid_6"] = "existing@example.com", ["fid_7"] = "Updated Hotel" }
            })
        };
        await RunStepAsync(addRow1, contextDict);
        await RunStepAsync(addRow2, contextDict);

        var existingGuid = Guid.NewGuid();
        var existingRow = new Dictionary<string, object?>
        {
            ["Id"] = 100L,
            ["PublicId"] = existingGuid,
            ["f_6"] = "existing@example.com",
            ["f_7"] = "Old Hotel Name"
        };

        // Mock lookup on physical column "f_6" (proving fid_6 is used as merge key, NOT "Id"/FID 3)
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["existing@example.com"] = existingRow });

        var commitStep = new PipelineStep
        {
            RefId = "commit_custom_mf",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "prep_custom_mf" })
        };

        // Act
        var outputJson = await RunStepAsync(commitStep, contextDict);

        // Assert: Lookup was strictly on column "f_6"
        await _recordRepo.Received(1).GetBulkUpsertRowsByColumnValuesAsync(
            _testTable, _testFields, "f_6",
            Arg.Is<IReadOnlyCollection<object>>(c => c.Contains("newhotel@example.com") && c.Contains("existing@example.com")),
            _dbTx, Arg.Any<CancellationToken>());

        // Assert: 1 Insert via CreateAsync, 1 Update via ApplyAsync
        await _recordRepo.Received(1).CreateAsync(
            _testTable, _testFields,
            Arg.Is<Dictionary<long, object?>>(d => (string)d[6]! == "newhotel@example.com" && (string)d[7]! == "New Hotel"),
            _dbTx, Arg.Any<CancellationToken>());

        await _recordWriteService.Received(1).ApplyAsync(
            _testTable, _testFields, existingGuid,
            Arg.Is<IReadOnlyDictionary<long, object?>>(d => (string)d[6]! == "existing@example.com" && (string)d[7]! == "Updated Hotel"),
            AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), _dbTx, suppressInterception: true,
            onIndexMessageCreated: null, existingRecord: Arg.Any<IReadOnlyDictionary<string, object?>>());
    }

    [Theory]
    [InlineData("MergeField", "fid_6")]
    [InlineData("MergeKeyFid", "fid_6")]
    [InlineData("MergeKeyField", "fid_6")]
    [InlineData("MergeKey", "fid_6")]
    public async Task PrepareBulkUpsertConfig_SupportsAllMergeFieldPropertyFormats(string propertyName, string propertyValue)
    {
        var configDict = new Dictionary<string, object>
        {
            ["tableLabel"] = _tablePublicId.ToString(),
            [propertyName] = propertyValue
        };

        var prepStep = new PipelineStep
        {
            RefId = $"prep_compat_{propertyName}",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(configDict)
        };
        var contextDict = new Dictionary<string, object>();

        // Act
        await RunStepAsync(prepStep, contextDict);

        // Assert: BulkUpsertSession correctly resolved MergeField with Fid = 6
        var sessions = (Dictionary<string, PipelineEngine.BulkUpsertSession>)contextDict["_bulkUpsertSessions"];

        var session = sessions[prepStep.RefId];
        session.Should().NotBeNull();
        session.MergeKeyFid.Should().Be("fid_6");
        session.MergeField!.Fid.Should().Be(6);
    }

    [Fact]
    public async Task CommitUpsert_WhenRowValuesIncludeFid3AsNull_InsertsRecordWithoutThrowing()
    {
        // Arrange
        var prepStep = new PipelineStep
        {
            RefId = "prep_with_null_fid3",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeField = "fid_6"
            })
        };

        var addStep = new PipelineStep
        {
            RefId = "add_with_null_fid3",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_with_null_fid3",
                rowValues = new Dictionary<string, object?>
                {
                    ["fid_6"] = "Jay Khodiyar Hotel & Restaurant",
                    ["fid_3"] = null
                }
            })
        };

        var commitStep = new PipelineStep
        {
            RefId = "commit_with_null_fid3",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_with_null_fid3"
            })
        };

        var contextDict = new Dictionary<string, object>();
        var newRecordGuid = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(newRecordGuid);

        var idMap = new Dictionary<Guid, long> { [newRecordGuid] = 42L };
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(idMap);

        var allSteps = new List<PipelineStep> { prepStep, addStep, commitStep };

        // Act
        await RunStepAsync(prepStep, contextDict, allSteps);
        await RunStepAsync(addStep, contextDict, allSteps);
        var commitOutputJson = await RunStepAsync(commitStep, contextDict, allSteps);

        // Assert
        commitOutputJson.Should().Contain("\"inserted_count\":1");
        commitOutputJson.Should().Contain("\"updated_count\":0");

        // Verify CreateAsync was called with row dictionary
        await _recordRepo.Received(1).CreateAsync(
            _testTable,
            _testFields,
            Arg.Any<IReadOnlyDictionary<long, object?>>(),
            _dbTx,
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task CommitUpsert_WhenReferencingAddBulkUpsertRowStep_ThrowsAndDoesNotCommitSession()
    {
        // Arrange
        var prepStep = new PipelineStep
        {
            RefId = "prep_aa",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                tableLabel = _tablePublicId.ToString(),
                mergeField = "fid_6"
            })
        };

        var addStep = new PipelineStep
        {
            RefId = "add_ab",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "prep_aa",
                rowValues = new Dictionary<string, object?>
                {
                    ["fid_6"] = "Hotel Test"
                }
            })
        };

        // Commit step specifically selects "add_ab" (Add a Bulk Upsert Row step) instead of "prep_aa"
        var commitStep = new PipelineStep
        {
            RefId = "commit_step",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new
            {
                parentUpsertStepRefId = "add_ab"
            })
        };

        var contextDict = new Dictionary<string, object>();
        var allSteps = new List<PipelineStep> { prepStep, addStep, commitStep };

        // Act & Assert
        await RunStepAsync(prepStep, contextDict, allSteps);
        await RunStepAsync(addStep, contextDict, allSteps);

        var res = await RunStepAsync(commitStep, contextDict, allSteps);
        res.Should().Contain("\"inserted_count\":0");
        res.Should().Contain("\"total_rows\":0");

        // Verify no record was created or updated in DB
        await _recordRepo.DidNotReceive().CreateAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(),
            Arg.Any<IDbTransaction>(),
            Arg.Any<CancellationToken>()
        );
        await _recordWriteService.DidNotReceive().ApplyAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IDbTransaction>(),
            Arg.Any<bool>()
        );
    }

    [Fact]
    public async Task SequentialRuns_SecondNewMergeKey_InsertsInsteadOfUpdatingPreviousRecord()
    {
        // EXECUTION 1: "Hotel A" is a brand new merge key -> INSERT
        var prep1 = new PipelineStep
        {
            RefId = "prep_1",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" })
        };
        var add1 = new PipelineStep
        {
            RefId = "add_1",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                bulkRecordSetStepId = "prep_1",
                rowValues = new Dictionary<string, object?> { ["fid_6"] = "Hotel A" }
            })
        };
        var commit1 = new PipelineStep
        {
            RefId = "commit_1",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "prep_1" })
        };

        var context1 = new Dictionary<string, object>();
        var guidA = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(guidA);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [guidA] = 101L });

        // Database initially has no "Hotel A"
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Is<IReadOnlyCollection<object>>(vals => vals.Contains("Hotel A")), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep1, context1, new List<PipelineStep> { prep1, add1, commit1 });
        await RunStepAsync(add1, context1, new List<PipelineStep> { prep1, add1, commit1 });
        var res1 = await RunStepAsync(commit1, context1, new List<PipelineStep> { prep1, add1, commit1 });

        res1.Should().Contain("\"inserted_count\":1");
        res1.Should().Contain("\"updated_count\":0");

        // EXECUTION 2: Separate run for "Hotel B" with empty/unprovided Record ID -> MUST BE INSERT
        var prep2 = new PipelineStep
        {
            RefId = "prep_1",
            Type = "action",
            Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" })
        };
        var add2 = new PipelineStep
        {
            RefId = "add_1",
            Type = "action",
            Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new
            {
                bulkRecordSetStepId = "prep_1",
                rowValues = new Dictionary<string, object?> { ["fid_6"] = "Hotel B", ["fid_3"] = "" }
            })
        };
        var commit2 = new PipelineStep
        {
            RefId = "commit_1",
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "prep_1" })
        };

        var context2 = new Dictionary<string, object>();
        var guidB = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(guidB);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [guidB] = 102L });

        // Database lookup for "Hotel B" returns empty (it is new)
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Is<IReadOnlyCollection<object>>(vals => vals.Contains("Hotel B")), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep2, context2, new List<PipelineStep> { prep2, add2, commit2 });
        await RunStepAsync(add2, context2, new List<PipelineStep> { prep2, add2, commit2 });
        var res2 = await RunStepAsync(commit2, context2, new List<PipelineStep> { prep2, add2, commit2 });

        res2.Should().Contain("\"inserted_count\":1");
        res2.Should().Contain("\"updated_count\":0");

        // EXECUTION 3: "Hotel B" again -> MUST BE UPDATE
        var context3 = new Dictionary<string, object>();
        var existingDictB = new Dictionary<string, object?>
        {
            ["Id"] = 102L,
            ["PublicId"] = guidB,
            ["f_6"] = "Hotel B"
        };
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Is<IReadOnlyCollection<object>>(vals => vals.Contains("Hotel B")), _dbTx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>> { ["Hotel B"] = existingDictB });

        await RunStepAsync(prep2, context3, new List<PipelineStep> { prep2, add2, commit2 });
        await RunStepAsync(add2, context3, new List<PipelineStep> { prep2, add2, commit2 });
        var res3 = await RunStepAsync(commit2, context3, new List<PipelineStep> { prep2, add2, commit2 });

        res3.Should().Contain("\"inserted_count\":0");
        res3.Should().Contain("\"updated_count\":1");
    }

    [Fact]
    public async Task Rule1_PrepareRoot_CommitPrepare_InsertsRow_And_CommitDirectAddRow_ReturnsZero()
    {
        // aa = Prepare, ab = Add Row -> aa (T1)
        var prep = new PipelineStep { RefId = "aa", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var add = new PipelineStep { RefId = "ab", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var commitAa = new PipelineStep { RefId = "commit_aa", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa" }) };
        var commitAb = new PipelineStep { RefId = "commit_ab", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab" }) };

        var allSteps = new List<PipelineStep> { prep, add, commitAa, commitAb };
        var ctx = new Dictionary<string, object>();

        var guid1 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(guid1);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [guid1] = 101L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep, ctx, allSteps);
        await RunStepAsync(add, ctx, allSteps);

        // Commit aa -> 1 inserted
        var resAa = await RunStepAsync(commitAa, ctx, allSteps);
        resAa.Should().Contain("\"inserted_count\":1");

        // Commit ab -> 0 rows (direct Add Row leaf)
        var resAb = await RunStepAsync(commitAb, ctx, allSteps);
        resAb.Should().Contain("\"inserted_count\":0");
        resAb.Should().Contain("\"total_rows\":0");
    }

    [Fact]
    public async Task Rule2_SiblingAddRows_CommitPrepare_InsertsBothRows_And_CommitDirectAddRows_ReturnZero()
    {
        // aa = Prepare, ab -> aa (T1), ac -> aa (T2)
        var prep = new PipelineStep { RefId = "aa", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var add1 = new PipelineStep { RefId = "ab", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var add2 = new PipelineStep { RefId = "ac", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T2" } }) };
        var commitAa = new PipelineStep { RefId = "commit_aa", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa" }) };
        var commitAb = new PipelineStep { RefId = "commit_ab", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab" }) };
        var commitAc = new PipelineStep { RefId = "commit_ac", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ac" }) };

        var allSteps = new List<PipelineStep> { prep, add1, add2, commitAa, commitAb, commitAc };
        var ctx = new Dictionary<string, object>();

        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(guid1, guid2);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [guid1] = 101L, [guid2] = 102L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep, ctx, allSteps);
        await RunStepAsync(add1, ctx, allSteps);
        await RunStepAsync(add2, ctx, allSteps);

        // Commit aa -> 2 inserted
        var resAa = await RunStepAsync(commitAa, ctx, allSteps);
        resAa.Should().Contain("\"inserted_count\":2");

        // Commit ab and ac -> 0 rows
        var resAb = await RunStepAsync(commitAb, ctx, allSteps);
        resAb.Should().Contain("\"inserted_count\":0");

        var resAc = await RunStepAsync(commitAc, ctx, allSteps);
        resAc.Should().Contain("\"inserted_count\":0");
    }

    [Fact]
    public async Task Rule3_FanOut_WhenMultipleAddRowsTargetSameAddRow_CommitResolvesSharedDownstreamCollection()
    {
        // aa = Prepare, ab -> aa (T1), ac -> ab (T2), ad -> ab (T3)
        var prep = new PipelineStep { RefId = "aa", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var ab = new PipelineStep { RefId = "ab", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var ac = new PipelineStep { RefId = "ac", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T2" } }) };
        var ad = new PipelineStep { RefId = "ad", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T3" } }) };

        var commitAa = new PipelineStep { RefId = "commit_aa", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa" }) };
        var commitAb = new PipelineStep { RefId = "commit_ab", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab" }) };
        var commitAc = new PipelineStep { RefId = "commit_ac", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ac" }) };
        var commitAd = new PipelineStep { RefId = "commit_ad", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ad" }) };

        var allSteps = new List<PipelineStep> { prep, ab, ac, ad, commitAa, commitAb, commitAc, commitAd };

        // Test Commit aa -> T1
        var ctx1 = new Dictionary<string, object>();
        var g1 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g1);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g1] = 101L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep, ctx1, allSteps);
        await RunStepAsync(ab, ctx1, allSteps);
        await RunStepAsync(ac, ctx1, allSteps);
        await RunStepAsync(ad, ctx1, allSteps);

        var resAa = await RunStepAsync(commitAa, ctx1, allSteps);
        resAa.Should().Contain("\"inserted_count\":1");

        // Test Commit ab -> T2 + T3 (2 rows)
        var ctx2 = new Dictionary<string, object>();
        var g2 = Guid.NewGuid(); var g3 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g2, g3);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g2] = 102L, [g3] = 103L });

        await RunStepAsync(prep, ctx2, allSteps);
        await RunStepAsync(ab, ctx2, allSteps);
        await RunStepAsync(ac, ctx2, allSteps);
        await RunStepAsync(ad, ctx2, allSteps);

        var resAb = await RunStepAsync(commitAb, ctx2, allSteps);
        resAb.Should().Contain("\"inserted_count\":2");

        // Test Commit ac -> T2 + T3 (2 rows)
        var ctx3 = new Dictionary<string, object>();
        await RunStepAsync(prep, ctx3, allSteps);
        await RunStepAsync(ab, ctx3, allSteps);
        await RunStepAsync(ac, ctx3, allSteps);
        await RunStepAsync(ad, ctx3, allSteps);

        var resAc = await RunStepAsync(commitAc, ctx3, allSteps);
        resAc.Should().Contain("\"inserted_count\":2");

        // Test Commit ad -> T2 + T3 (2 rows)
        var ctx4 = new Dictionary<string, object>();
        await RunStepAsync(prep, ctx4, allSteps);
        await RunStepAsync(ab, ctx4, allSteps);
        await RunStepAsync(ac, ctx4, allSteps);
        await RunStepAsync(ad, ctx4, allSteps);

        var resAd = await RunStepAsync(commitAd, ctx4, allSteps);
        resAd.Should().Contain("\"inserted_count\":2");
    }

    [Fact]
    public async Task Rule4_ThreeLevelChain_WhenChainedAddRows_CommitResolvesSharedDownstreamCollection()
    {
        // aa = Prepare, ab -> aa (T1), ac -> ab (T2), ad -> ac (T3)
        var prep = new PipelineStep { RefId = "aa", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var ab = new PipelineStep { RefId = "ab", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var ac = new PipelineStep { RefId = "ac", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T2" } }) };
        var ad = new PipelineStep { RefId = "ad", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ac", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T3" } }) };

        var commitAa = new PipelineStep { RefId = "commit_aa", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa" }) };
        var commitAb = new PipelineStep { RefId = "commit_ab", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab" }) };
        var commitAc = new PipelineStep { RefId = "commit_ac", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ac" }) };
        var commitAd = new PipelineStep { RefId = "commit_ad", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ad" }) };

        var allSteps = new List<PipelineStep> { prep, ab, ac, ad, commitAa, commitAb, commitAc, commitAd };

        // Test Commit aa -> T1
        var ctx1 = new Dictionary<string, object>();
        var g1 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g1);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g1] = 101L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep, ctx1, allSteps);
        await RunStepAsync(ab, ctx1, allSteps);
        await RunStepAsync(ac, ctx1, allSteps);
        await RunStepAsync(ad, ctx1, allSteps);

        var resAa = await RunStepAsync(commitAa, ctx1, allSteps);
        resAa.Should().Contain("\"inserted_count\":1");

        // Test Commit ab, ac, ad -> each resolves T2 + T3
        var ctx2 = new Dictionary<string, object>();
        var g2 = Guid.NewGuid(); var g3 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g2, g3);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g2] = 102L, [g3] = 103L });

        await RunStepAsync(prep, ctx2, allSteps);
        await RunStepAsync(ab, ctx2, allSteps);
        await RunStepAsync(ac, ctx2, allSteps);
        await RunStepAsync(ad, ctx2, allSteps);

        var resAb = await RunStepAsync(commitAb, ctx2, allSteps);
        resAb.Should().Contain("\"inserted_count\":2");

        var ctx3 = new Dictionary<string, object>();
        await RunStepAsync(prep, ctx3, allSteps);
        await RunStepAsync(ab, ctx3, allSteps);
        await RunStepAsync(ac, ctx3, allSteps);
        await RunStepAsync(ad, ctx3, allSteps);
        var resAc = await RunStepAsync(commitAc, ctx3, allSteps);
        resAc.Should().Contain("\"inserted_count\":2");

        var ctx4 = new Dictionary<string, object>();
        await RunStepAsync(prep, ctx4, allSteps);
        await RunStepAsync(ab, ctx4, allSteps);
        await RunStepAsync(ac, ctx4, allSteps);
        await RunStepAsync(ad, ctx4, allSteps);
        var resAd = await RunStepAsync(commitAd, ctx4, allSteps);
        resAd.Should().Contain("\"inserted_count\":2");
    }

    [Fact]
    public async Task FourLevelChain_WhenDeeplyChainedAddRows_AllDownstreamNodesResolveSharedCollection()
    {
        // aa -> ab(T1) -> ac(T2) -> ad(T3) -> ae(T4)
        var prep = new PipelineStep { RefId = "aa", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var ab = new PipelineStep { RefId = "ab", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var ac = new PipelineStep { RefId = "ac", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T2" } }) };
        var ad = new PipelineStep { RefId = "ad", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ac", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T3" } }) };
        var ae = new PipelineStep { RefId = "ae", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ad", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T4" } }) };

        var commitAe = new PipelineStep { RefId = "commit_ae", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ae" }) };

        var allSteps = new List<PipelineStep> { prep, ab, ac, ad, ae, commitAe };
        var ctx = new Dictionary<string, object>();

        var g2 = Guid.NewGuid(); var g3 = Guid.NewGuid(); var g4 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g2, g3, g4);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g2] = 102L, [g3] = 103L, [g4] = 104L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep, ctx, allSteps);
        await RunStepAsync(ab, ctx, allSteps);
        await RunStepAsync(ac, ctx, allSteps);
        await RunStepAsync(ad, ctx, allSteps);
        await RunStepAsync(ae, ctx, allSteps);

        var res = await RunStepAsync(commitAe, ctx, allSteps);
        res.Should().Contain("\"inserted_count\":3"); // T2 + T3 + T4
    }

    [Fact]
    public async Task MixedRootAndDownstream_WhenMultipleIndependentBranches_BranchesRemainIsolated()
    {
        // aa = Prepare
        // ab -> aa (T1), ac -> ab (T2)
        // ad -> aa (T3), ae -> ad (T4)
        var prep = new PipelineStep { RefId = "aa", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var ab = new PipelineStep { RefId = "ab", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var ac = new PipelineStep { RefId = "ac", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T2" } }) };
        var ad = new PipelineStep { RefId = "ad", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T3" } }) };
        var ae = new PipelineStep { RefId = "ae", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ad", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T4" } }) };

        var commitAa = new PipelineStep { RefId = "commit_aa", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa" }) };
        var commitAb = new PipelineStep { RefId = "commit_ab", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab" }) };
        var commitAd = new PipelineStep { RefId = "commit_ad", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ad" }) };

        var allSteps = new List<PipelineStep> { prep, ab, ac, ad, ae, commitAa, commitAb, commitAd };

        // Test Commit aa -> T1 + T3 (2 rows)
        var ctx1 = new Dictionary<string, object>();
        var g1 = Guid.NewGuid(); var g3 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g1, g3);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g1] = 101L, [g3] = 103L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep, ctx1, allSteps);
        await RunStepAsync(ab, ctx1, allSteps);
        await RunStepAsync(ac, ctx1, allSteps);
        await RunStepAsync(ad, ctx1, allSteps);
        await RunStepAsync(ae, ctx1, allSteps);

        var resAa = await RunStepAsync(commitAa, ctx1, allSteps);
        resAa.Should().Contain("\"inserted_count\":2"); // T1 + T3

        // Test Commit ab -> T2 only (1 row)
        var ctx2 = new Dictionary<string, object>();
        var g2 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g2);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g2] = 102L });

        await RunStepAsync(prep, ctx2, allSteps);
        await RunStepAsync(ab, ctx2, allSteps);
        await RunStepAsync(ac, ctx2, allSteps);
        await RunStepAsync(ad, ctx2, allSteps);
        await RunStepAsync(ae, ctx2, allSteps);

        var resAb = await RunStepAsync(commitAb, ctx2, allSteps);
        resAb.Should().Contain("\"inserted_count\":1"); // T2 only

        // Test Commit ad -> T4 only (1 row)
        var ctx3 = new Dictionary<string, object>();
        var g4 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g4);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g4] = 104L });

        await RunStepAsync(prep, ctx3, allSteps);
        await RunStepAsync(ab, ctx3, allSteps);
        await RunStepAsync(ac, ctx3, allSteps);
        await RunStepAsync(ad, ctx3, allSteps);
        await RunStepAsync(ae, ctx3, allSteps);

        var resAd = await RunStepAsync(commitAd, ctx3, allSteps);
        resAd.Should().Contain("\"inserted_count\":1"); // T4 only
    }

    [Fact]
    public async Task FanOutPlusRootSibling_WhenMixed_DoesNotCrossContaminateBranches()
    {
        // aa = Prepare
        // ab -> aa (T1), ac -> ab (T2), ad -> ab (T3)
        // ae -> aa (T4)
        var prep = new PipelineStep { RefId = "aa", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var ab = new PipelineStep { RefId = "ab", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var ac = new PipelineStep { RefId = "ac", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T2" } }) };
        var ad = new PipelineStep { RefId = "ad", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T3" } }) };
        var ae = new PipelineStep { RefId = "ae", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T4" } }) };

        var commitAa = new PipelineStep { RefId = "commit_aa", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "aa" }) };
        var commitAb = new PipelineStep { RefId = "commit_ab", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "ab" }) };

        var allSteps = new List<PipelineStep> { prep, ab, ac, ad, ae, commitAa, commitAb };

        // Test Commit aa -> T1 + T4 (2 rows)
        var ctx1 = new Dictionary<string, object>();
        var g1 = Guid.NewGuid(); var g4 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g1, g4);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g1] = 101L, [g4] = 104L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await RunStepAsync(prep, ctx1, allSteps);
        await RunStepAsync(ab, ctx1, allSteps);
        await RunStepAsync(ac, ctx1, allSteps);
        await RunStepAsync(ad, ctx1, allSteps);
        await RunStepAsync(ae, ctx1, allSteps);

        var resAa = await RunStepAsync(commitAa, ctx1, allSteps);
        resAa.Should().Contain("\"inserted_count\":2"); // T1 + T4

        // Test Commit ab -> T2 + T3 (2 rows)
        var ctx2 = new Dictionary<string, object>();
        var g2 = Guid.NewGuid(); var g3 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g2, g3);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g2] = 102L, [g3] = 103L });

        await RunStepAsync(prep, ctx2, allSteps);
        await RunStepAsync(ab, ctx2, allSteps);
        await RunStepAsync(ac, ctx2, allSteps);
        await RunStepAsync(ad, ctx2, allSteps);
        await RunStepAsync(ae, ctx2, allSteps);

        var resAb = await RunStepAsync(commitAb, ctx2, allSteps);
        resAb.Should().Contain("\"inserted_count\":2"); // T2 + T3
    }

    [Fact]
    public async Task ExactFiveStepUserPipeline_ChainingAndAudit_ResolvesSuccessfully()
    {
        // 1. Prepare Bulk Record Upsert ref_5602
        // 2. Add a Bulk Upsert Row      ref_2551 -> ref_5602 (T1)
        // 3. Add a Bulk Upsert Row      ref_2052 -> ref_2551 (T2)
        // 4. Add a Bulk Upsert Row      ref_1683 -> ref_2551 (T3)
        // 5. Commit Upsert              ref_1798 -> ref_2551 (T2 + T3)
        var s1 = new PipelineStep { RefId = "ref_5602", Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId.ToString(), mergeKeyFid = "fid_6" }) };
        var s2 = new PipelineStep { RefId = "ref_2551", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "ref_5602", parentUpsertStepRefId = "ref_5602", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T1" } }) };
        var s3 = new PipelineStep { RefId = "ref_2052", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "ref_2551", parentUpsertStepRefId = "ref_2551", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T2" } }) };
        var s4 = new PipelineStep { RefId = "ref_1683", Type = "action", Subtype = "add-bulk-upsert-row", ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "ref_2551", parentUpsertStepRefId = "ref_2551", rowValues = new Dictionary<string, object?> { ["fid_6"] = "T3" } }) };
        var s5 = new PipelineStep { RefId = "ref_1798", Type = "action", Subtype = "commit-upsert", ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "ref_2551", parentUpsertStepRefId = "ref_2551" }) };

        var allSteps = new List<PipelineStep> { s1, s2, s3, s4, s5 };
        var ctx = new Dictionary<string, object>();

        var g2 = Guid.NewGuid(); var g3 = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(g2, g3);
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, long> { [g2] = 102L, [g3] = 103L });
        _recordRepo.GetBulkUpsertRowsByColumnValuesAsync(_testTable, _testFields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _dbTx, Arg.Any<CancellationToken>()).Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        var r1 = await RunStepAsync(s1, ctx, allSteps);
        r1.Should().Contain("\"Status\":\"Prepared\"");

        var r2 = await RunStepAsync(s2, ctx, allSteps);
        r2.Should().Contain("\"Status\":\"RowAdded\"");
        r2.Should().Contain("\"RowCount\":1");

        var r3 = await RunStepAsync(s3, ctx, allSteps);
        r3.Should().Contain("\"Status\":\"RowAdded\"");
        r3.Should().Contain("\"RowCount\":1");

        var r4 = await RunStepAsync(s4, ctx, allSteps);
        r4.Should().Contain("\"Status\":\"RowAdded\"");
        r4.Should().Contain("\"RowCount\":2");

        var r5 = await RunStepAsync(s5, ctx, allSteps);
        r5.Should().Contain("\"inserted_count\":2");
        r5.Should().Contain("\"total_records\":2");
    }

    #endregion
    [Fact]
    public async Task SelectedBulkColumns_BlankMappingsClearOnlySelectedFields_AndAddDoesNotWrite()
    {
        var context = new Dictionary<string, object> { ["_CreatedBy"] = 42L };
        await RunStepAsync(new PipelineStep
        {
            RefId = "prepare", Type = "action", Subtype = "prepare-bulk-upsert",
            ConfigJson = JsonSerializer.Serialize(new { tableLabel = _tablePublicId, mergeField = "fid_6", fields = new[] { "fid_6", "fid_7" } })
        }, context);
        await RunStepAsync(new PipelineStep
        {
            RefId = "add", Type = "action", Subtype = "add-bulk-upsert-row",
            ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "prepare", rowValues = new Dictionary<string, object?> { ["fid_6"] = "new@example.com" } })
        }, context);
        await _recordRepo.DidNotReceive().CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await RunStepAsync(new PipelineStep
        {
            RefId = "commit", Type = "action", Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { bulkRecordSetStepId = "prepare" })
        }, context);
        await _recordRepo.Received(1).CreateAsync(_testTable, _testFields,
            Arg.Is<IReadOnlyDictionary<long, object?>>(values => values.ContainsKey(7) && (string?)values[7] == "" && !values.ContainsKey(8)),
            _dbTx, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "TEXT")]
    [InlineData(" ", "TEXT")]
    public void NonBulkParser_PreservesOriginalBlankValueBehavior(string value, string typeCode)
    {
        var method = typeof(PipelineEngine).GetMethod("ParseValueType", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(_engine, new object[] { value, typeCode, "Test Field" }).Should().BeNull();
    }

}
