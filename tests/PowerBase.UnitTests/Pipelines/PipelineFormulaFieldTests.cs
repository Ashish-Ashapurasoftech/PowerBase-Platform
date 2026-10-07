using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Infrastructure.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Regression coverage for Formula fields (Formula_Text / Formula_Number / Formula_Date /
/// Formula_DateTime) inside the Pipeline module. A formula field is compute-on-read — it has no
/// physical column and no stored value — so every place a pipeline reads a record has to compute
/// it: the On New Event payload (filters + conditions), bulk events, delete events, Search
/// Records (filter + output) and the Create/Update Record step outputs. It also covers the type
/// category used when a condition compares a formula result (number/date vs text).
/// </summary>
public class PipelineFormulaFieldTests
{
    private const long NameFid = 6;
    private const long FormulaFid = 8;

    private static readonly AppTable Table = new() { Id = 100, AppId = 1, PublicId = Guid.NewGuid() };

    /// <summary>(type code, matching value, non-matching value, operator, filter operand)</summary>
    public static IEnumerable<object[]> Variants() => new[]
    {
        new object[] { "Formula_Text", "CST00014", "CST00099", "is", "CST00014" },
        new object[] { "Formula_Number", 25m, 5m, "greater_than", "10" },
        new object[] { "Formula_Date", new DateTime(2026, 10, 7), new DateTime(2025, 1, 1), "greater_than", "2026-01-01" },
        new object[] { "Formula_DateTime", new DateTime(2026, 10, 7, 10, 30, 0), new DateTime(2028, 1, 1, 8, 0, 0), "less_than", "2027-01-01" },
    };

    private static List<AppField> Fields(string formulaTypeCode) => new()
    {
        new() { Id = NameFid, Fid = (int)NameFid, Name = "Name", TypeCode = "Text" },
        new() { Id = FormulaFid, Fid = (int)FormulaFid, Name = "CustomerNumber", TypeCode = formulaTypeCode,
                Settings = "{\"expression\":\"1\"}" }
    };

    // ───────────────────────── type category (condition / filter comparisons) ─────────────────────────

    [Theory]
    [InlineData("Formula_Text", "TEXT")]
    [InlineData("Formula_Number", "NUMBER")]
    [InlineData("Formula_Duration", "NUMBER")]
    [InlineData("Formula_Date", "DATE")]
    [InlineData("Formula_DateTime", "DATE")]
    [InlineData("Formula_Time", "DATE")]
    [InlineData("Formula_Bool", "BOOLEAN")]
    public void TypeCategory_FormulaVariants_UseTheirResultType(string typeCode, string expected)
    {
        // With a configured expression, and without settings at all (fallback by TypeCode suffix).
        var withSettings = new AppField { Id = 1, Fid = 1, Name = "F", TypeCode = typeCode, Settings = "{\"expression\":\"1\"}" };
        var withoutSettings = new AppField { Id = 2, Fid = 2, Name = "G", TypeCode = typeCode };

        Assert.Equal(expected, PipelineFilterEvaluator.GetTypeCategory(withSettings));
        Assert.Equal(expected, PipelineFilterEvaluator.GetTypeCategory(withoutSettings));
        Assert.Equal(expected, PipelineFilterEvaluator.GetTypeCategory(typeCode));
    }

    [Theory]
    [InlineData("Number", "NUMBER")]
    [InlineData("Date", "DATE")]
    [InlineData("DateTime", "DATE")]
    [InlineData("Text", "TEXT")]
    public void TypeCategory_GenericFormula_UsesResultTypeSetting(string resultType, string expected)
    {
        var field = new AppField { Id = 1, Fid = 1, Name = "F", TypeCode = "Formula",
            Settings = $"{{\"expression\":\"1\",\"resultType\":\"{resultType}\"}}" };
        Assert.Equal(expected, PipelineFilterEvaluator.GetTypeCategory(field));
    }

    [Fact]
    public void TypeCategory_NonFormulaFields_Unchanged()
    {
        Assert.Equal("NUMBER", PipelineFilterEvaluator.GetTypeCategory(new AppField { TypeCode = "Number" }));
        Assert.Equal("DATE", PipelineFilterEvaluator.GetTypeCategory(new AppField { TypeCode = "Date" }));
        Assert.Equal("TEXT", PipelineFilterEvaluator.GetTypeCategory(new AppField { TypeCode = "Text" }));
    }

    [Fact]
    public void Filter_FormulaNumber_ComparesNumericallyNotAsText()
    {
        // As TEXT, "9" > "10" is true (ordinal) — the bug this guards against.
        var fields = Fields("Formula_Number");
        var rule = new TriggerFilterRule { Field = "CustomerNumber", Operator = "greater_than", Value = "10" };
        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = new List<TriggerFilterRule> { rule } };

        Assert.False(PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [FormulaFid] = 9m }, fields));
        Assert.True(PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [FormulaFid] = 25m }, fields));
    }

    [Fact]
    public void Filter_FormulaDate_ComparesAsDates()
    {
        var fields = Fields("Formula_Date");
        var rule = new TriggerFilterRule { Field = "CustomerNumber", Operator = "less_than", Value = "2026-10-01" };
        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = new List<TriggerFilterRule> { rule } };

        Assert.True(PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [FormulaFid] = new DateTime(2026, 9, 1) }, fields));
        Assert.False(PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [FormulaFid] = new DateTime(2026, 11, 1) }, fields));
    }

    [Theory]
    [InlineData("Formula_Number", "greater_than")]
    [InlineData("Formula_Date", "greater_than")]
    [InlineData("Formula_DateTime", "less_than")]
    public void Validator_FormulaVariants_AllowTypedOperators(string typeCode, string op)
    {
        var errors = new Dictionary<string, List<string>>();
        var rule = new TriggerFilterRule { Field = "CustomerNumber", Operator = op, Value = "1" };
        PipelineFilterEvaluator.ValidateRule(rule, Fields(typeCode), errors, "Filters[0]");
        Assert.Empty(errors);
    }

    // ───────────────────────── trigger events (interceptor) ─────────────────────────

    private sealed class InterceptorHarness
    {
        public IPipelineRepository PipelineRepo { get; } = Substitute.For<IPipelineRepository>();
        public IRecordRepository RecordRepo { get; } = Substitute.For<IRecordRepository>();
        public IFormulaProjector Projector { get; } = Substitute.For<IFormulaProjector>();
        public List<PipelineOutboxItem> Outbox { get; } = new();
        public PipelineTriggerInterceptor Interceptor { get; }

        public InterceptorHarness(string op, string operand, bool onDeleted = false, bool withFilter = true)
        {
            var uow = Substitute.For<ITenantUnitOfWork>();
            uow.Transaction.Returns(Substitute.For<IDbTransaction>());
            var queryCtx = Substitute.For<IQueryContext>();
            PipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { new() { Id = 101, IsActive = true } });
            PipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
            {
                new() { Type = "trigger", Subtype = "new-event", ConfigJson = JsonSerializer.Serialize(new
                {
                    TablePublicId = Table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnModified = true,
                    TriggerOnDeleted = onDeleted, TriggerOnAnyField = true,
                    Filters = withFilter
                        ? new[] { new { Field = $"fid_{FormulaFid}", Operator = op, Value = operand } }
                        : null
                }) }
            });
            PipelineRepo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(Outbox.Add), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
            Interceptor = new PipelineTriggerInterceptor(PipelineRepo, RecordRepo, queryCtx, uow, null!, null!,
                Substitute.For<ILogger<PipelineTriggerInterceptor>>(), null, null, Projector);
        }

        /// <summary>The re-read row + projected formula value the interceptor will see for each record.</summary>
        public void Computes(IReadOnlyDictionary<Guid, object?> valuesByRecord)
        {
            var ids = valuesByRecord.Keys.Select((g, i) => (Guid: g, Id: 50L + i)).ToDictionary(x => x.Guid, x => x.Id);
            RecordRepo.GetRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(ids.ToDictionary(x => x.Key, x => x.Value));
            RecordRepo.GetBulkUpsertRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(ci => (IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>)ids.ToDictionary(
                    x => x.Value,
                    x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = x.Value, ["PublicId"] = x.Key, ["f_6"] = "row" }));
            var byRowId = ids.ToDictionary(x => x.Value, x => valuesByRecord[x.Key]);
            Projector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                    Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
                .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1)
                    .Select(r => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [FormulaFid] = byRowId[(long)r["Id"]!] })
                    .ToList());
        }

        public JsonElement Payload(int index = 0) => JsonDocument.Parse(Outbox[index].TriggerPayloadJson).RootElement;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Event_RecordAdded_FormulaValueIsInPayloadAndFilterMatches(string typeCode, object match, object _, string op, string operand)
    {
        var fields = Fields(typeCode);
        var h = new InterceptorHarness(op, operand);
        var id = Guid.NewGuid();
        h.Computes(new Dictionary<Guid, object?> { [id] = match });

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "row" }, "record-added");

        Assert.Single(h.Outbox);
        var newValues = h.Payload().GetProperty("NewValues");
        Assert.True(newValues.TryGetProperty($"fid_{FormulaFid}", out var value));
        Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
        // The payload is JSON: compare against how the same value serializes (ISO dates, bare numbers).
        Assert.Equal(JsonSerializer.Serialize(match).Trim('"'),
            value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
        // Condition/loop tokens read SelectedFieldValues.
        Assert.True(h.Payload().GetProperty("SelectedFieldValues").TryGetProperty($"fid_{FormulaFid}", out JsonElement _));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Event_RecordAdded_FormulaFilterDoesNotMatch_NoEvent(string typeCode, object _, object noMatch, string op, string operand)
    {
        var fields = Fields(typeCode);
        var h = new InterceptorHarness(op, operand);
        var id = Guid.NewGuid();
        h.Computes(new Dictionary<Guid, object?> { [id] = noMatch });

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "row" }, "record-added");

        Assert.Empty(h.Outbox);
    }

    [Fact]
    public async Task Event_RecordUpdated_FormulaValueIsInNewValues()
    {
        var fields = Fields("Formula_Text");
        var h = new InterceptorHarness("is", "CST00014");
        var id = Guid.NewGuid();
        h.Computes(new Dictionary<Guid, object?> { [id] = "CST00014" });

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "changed" },
            "record-updated", default, new Dictionary<long, object?> { [NameFid] = "old" }, new long[] { NameFid });

        Assert.Single(h.Outbox);
        Assert.Equal("CST00014", h.Payload().GetProperty("NewValues").GetProperty($"fid_{FormulaFid}").GetString());
    }

    [Fact]
    public async Task Event_RecordDeleted_FormulaValueIsInOldValues()
    {
        var fields = Fields("Formula_Text");
        var h = new InterceptorHarness("is", "CST00014", onDeleted: true);
        var id = Guid.NewGuid();
        h.Computes(new Dictionary<Guid, object?> { [id] = "CST00014" });

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "row" }, "record-deleted");

        Assert.Single(h.Outbox);
        Assert.Equal("CST00014", h.Payload().GetProperty("OldValues").GetProperty($"fid_{FormulaFid}").GetString());
    }

    [Fact]
    public async Task Event_BulkChanges_OnlyRecordsWhoseFormulaMatchesFire()
    {
        var fields = Fields("Formula_Text");
        var h = new InterceptorHarness("is", "CST00014");
        var hit = Guid.NewGuid();
        var miss = Guid.NewGuid();
        h.Computes(new Dictionary<Guid, object?> { [hit] = "CST00014", [miss] = "CST00015" });

        PipelineRecordChange Change(Guid g) => new(g, new Dictionary<long, object?>(),
            new Dictionary<long, object?> { [NameFid] = "x" }, new List<long>(), PipelineRecordEventType.Added);
        await h.Interceptor.InterceptBulkAsync(Table, fields, new[] { Change(hit), Change(miss) }, Guid.NewGuid(), Guid.NewGuid(), 1, default);

        Assert.Single(h.Outbox);
        Assert.Equal(hit.ToString(), h.Payload().GetProperty("RecordPublicId").GetString());
    }

    [Fact]
    public async Task Event_FormulaProjectionFails_EventIsStillCreatedAndWriteIsNotBlocked()
    {
        var fields = Fields("Formula_Text");
        var h = new InterceptorHarness("is", "x", withFilter: false);
        var id = Guid.NewGuid();
        h.RecordRepo.GetRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "row" }, "record-added");

        Assert.Single(h.Outbox);
        Assert.False(h.Payload().GetProperty("NewValues").TryGetProperty($"fid_{FormulaFid}", out JsonElement _));
    }

    [Fact]
    public async Task Event_TableWithoutFormulaFields_DoesNotReadRowsOrProject()
    {
        var fields = new List<AppField> { new() { Id = NameFid, Fid = (int)NameFid, Name = "Name", TypeCode = "Text" } };
        var h = new InterceptorHarness("is", "x", withFilter: false);

        await h.Interceptor.InterceptAsync(Table, fields, Guid.NewGuid(), new Dictionary<long, object?> { [NameFid] = "row" }, "record-added");

        Assert.Single(h.Outbox);
        h.Projector.DidNotReceiveWithAnyArgs().Project(default!, default!, default, default);
        await h.RecordRepo.DidNotReceiveWithAnyArgs().GetBulkUpsertRowsByIdsAsync(default!, default!, default!, default!, default);
    }

    // ───────────────────────── Search Records step ─────────────────────────

    private sealed class EngineHarness
    {
        public IRecordRepository RecordRepo { get; } = Substitute.For<IRecordRepository>();
        public IAppTableRepository TableRepo { get; } = Substitute.For<IAppTableRepository>();
        public IAppFieldRepository FieldRepo { get; } = Substitute.For<IAppFieldRepository>();
        public IPipelineRecordSearchService SearchService { get; } = Substitute.For<IPipelineRecordSearchService>();
        public IFormulaProjector Projector { get; } = Substitute.For<IFormulaProjector>();
        public ITenantUnitOfWork Uow { get; } = Substitute.For<ITenantUnitOfWork>();
        public IPipelineStepIdempotencyRepository Idempotency { get; } = Substitute.For<IPipelineStepIdempotencyRepository>();
        public IPipelineTriggerInterceptor TriggerInterceptor { get; } = Substitute.For<IPipelineTriggerInterceptor>();
        public IRecordWriteService WriteService { get; } = Substitute.For<IRecordWriteService>();
        public PipelineEngine Engine { get; }

        public EngineHarness(string formulaTypeCode)
        {
            TableRepo.GetByPublicIdAsync(Table.PublicId, Arg.Any<CancellationToken>()).Returns(Table);
            FieldRepo.ListByTableAsync(Table.Id, Arg.Any<CancellationToken>()).Returns(Fields(formulaTypeCode));
            Uow.Transaction.Returns(Substitute.For<IDbTransaction>());
            Idempotency.GetByExecutionKeyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<byte[]>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);

            var serviceProvider = Substitute.For<IServiceProvider>();
            serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(SearchService);
            serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
            serviceProvider.GetService(typeof(IFormulaProjector)).Returns(Projector);

            Engine = new PipelineEngine(
                Substitute.For<IPipelineRepository>(), RecordRepo, WriteService, TableRepo, FieldRepo,
                Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
                Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
                Substitute.For<ILogger<PipelineEngine>>(), TriggerInterceptor, Uow, Substitute.For<IPipelineAuditFormatter>(),
                Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
                Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Idempotency);
        }

        /// <summary>Row id → formula value, returned by the mocked projector for rows it is given.</summary>
        public void Computes(IReadOnlyDictionary<long, object?> formulaByRowId) =>
            Projector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                    Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
                .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1)
                    .Select(r => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [FormulaFid] = formulaByRowId[Convert.ToInt64(r["Id"])] })
                    .ToList());

        public async Task<string> RunStepAsync(PipelineStep step, Dictionary<string, object>? context = null)
        {
            var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return await (Task<string>)method.Invoke(Engine, new object[]
            {
                step, "{}", context ?? new Dictionary<string, object>(), new List<PipelineStep> { step }, new Dictionary<string, object>(),
                1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
                RecordRepo, TableRepo, FieldRepo, WriteService, TriggerInterceptor, Uow, Idempotency,
                Substitute.For<IFileStorageService>(), SearchService, CancellationToken.None
            })!;
        }

        public FilterGroup? CapturedFilter { get; private set; }

        /// <summary>Candidate rows as the SQL layer would return them for whatever physical filter is sent.</summary>
        public void SqlReturns(List<IReadOnlyDictionary<string, object?>> candidates, string formulaTypeCode)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> Narrow(FilterGroup? tree)
            {
                CapturedFilter = tree;
                // The mock SQL can only evaluate stored columns: a formula condition that leaked into
                // the SQL tree would find no computed value here and wrongly return nothing.
                return tree == null ? candidates
                    : FormulaFilterSorter.ApplyFormulaFilters(
                        candidates.Select(r => (Row: r, Computed: (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>())),
                        tree, Fields(formulaTypeCode)).Select(p => p.Row).ToList();
            }
            SearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(Narrow(ci.ArgAt<FilterGroup?>(3))));
            RecordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
                Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(Narrow(ci.ArgAt<FilterGroup?>(4))));
            RecordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<int>(p => p > 1), Arg.Any<int>(),
                Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(new List<IReadOnlyDictionary<string, object?>>());
        }
    }

    private static IReadOnlyDictionary<string, object?> Row(long id, Guid publicId, string name) =>
        new Dictionary<string, object?> { ["Id"] = id, ["PublicId"] = publicId, ["f_6"] = name };

    private static PipelineStep SearchStep(string field, string op, string value, int? maxResults = null)
    {
        var rule = field.Length == 0 ? "[]" : JsonSerializer.Serialize(new[] { new { Field = field, Operator = op, Value = value } });
        var json = $"{{\"TableId\":\"{Table.PublicId}\",\"FilterGroups\":[{{\"LogicalOp\":\"AND\",\"Rules\":{rule}}}]"
                   + (maxResults.HasValue ? $",\"MaxResults\":{maxResults}" : "") + "}";
        return new PipelineStep { Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records", ConfigJson = json };
    }

    private static bool HasCondition(FilterGroup? group, long fid) =>
        group != null && group.Nodes.Any(n => (n.Condition?.FieldId == fid) || HasCondition(n.Group, fid));

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task SearchRecords_FilterOnFormula_MatchesInMemoryAndKeepsItOutOfSql(string typeCode, object match, object noMatch, string op, string operand)
    {
        var h = new EngineHarness(typeCode);
        var hit = Guid.NewGuid();
        var miss = Guid.NewGuid();
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, hit, "hit"), Row(2, miss, "miss") }, typeCode);
        h.Computes(new Dictionary<long, object?> { [1] = match, [2] = noMatch });

        var json = await h.RunStepAsync(SearchStep($"fid_{FormulaFid}", op, operand));

        var records = JsonDocument.Parse(json).RootElement.GetProperty("records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal(hit.ToString(), records[0].GetProperty("RecordPublicId").GetString());
        Assert.True(records[0].TryGetProperty($"fid_{FormulaFid}", out JsonElement _), "formula value is exposed on the step output");
        Assert.False(HasCondition(h.CapturedFilter, FormulaFid), "a formula condition must never be sent to SQL");
    }

    [Fact]
    public async Task SearchRecords_FormulaAndStoredFilter_StoredGoesToSqlFormulaInMemory()
    {
        var h = new EngineHarness("Formula_Text");
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, a, "Ronak"), Row(2, b, "Ronak"), Row(3, c, "Other") }, "Formula_Text");
        h.Computes(new Dictionary<long, object?> { [1] = "CST00014", [2] = "CST00015", [3] = "CST00014" });

        var rules = JsonSerializer.Serialize(new[]
        {
            new { Field = "fid_6", Operator = "is", Value = "Ronak" },
            new { Field = $"fid_{FormulaFid}", Operator = "is", Value = "CST00014" }
        });
        var step = new PipelineStep { Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records",
            ConfigJson = $"{{\"TableId\":\"{Table.PublicId}\",\"FilterGroups\":[{{\"LogicalOp\":\"AND\",\"Rules\":{rules}}}]}}" };

        var json = await h.RunStepAsync(step);

        var records = JsonDocument.Parse(json).RootElement.GetProperty("records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal(a.ToString(), records[0].GetProperty("RecordPublicId").GetString());
        Assert.True(HasCondition(h.CapturedFilter, NameFid));
        Assert.False(HasCondition(h.CapturedFilter, FormulaFid));
    }

    [Fact]
    public async Task SearchRecords_MaxResultsAppliesAfterFormulaFilter()
    {
        var h = new EngineHarness("Formula_Text");
        var rows = Enumerable.Range(1, 4).Select(i => Row(i, Guid.NewGuid(), $"r{i}")).ToList();
        h.SqlReturns(rows, "Formula_Text");
        // Row 1 does not match; rows 2-4 do. A limit applied before the in-memory filter would return 0.
        h.Computes(new Dictionary<long, object?> { [1] = "no", [2] = "yes", [3] = "yes", [4] = "yes" });

        var json = await h.RunStepAsync(SearchStep($"fid_{FormulaFid}", "is", "yes", maxResults: 2));

        Assert.Equal(2, JsonDocument.Parse(json).RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task SearchRecords_NoFormulaFilter_StillExposesFormulaValuesInOutput()
    {
        var h = new EngineHarness("Formula_Number");
        var id = Guid.NewGuid();
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, id, "Ronak") }, "Formula_Number");
        h.Computes(new Dictionary<long, object?> { [1] = 42m });

        var json = await h.RunStepAsync(SearchStep("fid_6", "is", "Ronak"));

        var records = JsonDocument.Parse(json).RootElement.GetProperty("records");
        Assert.Equal(42m, records[0].GetProperty($"fid_{FormulaFid}").GetDecimal());
    }

    // ───────────────────────── Create / Update Record step outputs ─────────────────────────

    [Fact]
    public async Task CreateRecord_OutputAndRecordAddedEventCarryFormulaValue()
    {
        var h = new EngineHarness("Formula_Text");
        var created = Guid.NewGuid();
        h.RecordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>>()).Returns(created);
        h.RecordRepo.GetActiveRecordIdByPublicIdAsync(Arg.Any<AppTable>(), created, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(19L);
        h.RecordRepo.GetBulkUpsertRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [19L] = Row(19, created, "Ronak") });
        h.Computes(new Dictionary<long, object?> { [19] = "CST00019" });

        var step = new PipelineStep { Id = 1, RefId = "ref_create", Type = "action", Subtype = "create-record",
            ConfigJson = $"{{\"TableId\":\"{Table.PublicId}\",\"FieldMappings\":[{{\"Field\":\"fid_6\",\"Value\":\"Ronak\"}}]}}" };

        var json = await h.RunStepAsync(step, new Dictionary<string, object> { ["_MessageId"] = Guid.NewGuid() });

        Assert.Equal("CST00019", JsonDocument.Parse(json).RootElement.GetProperty($"fid_{FormulaFid}").GetString());
        await h.TriggerInterceptor.Received(1).InterceptAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), created,
            Arg.Is<IReadOnlyDictionary<long, object?>>(v => (string?)v[FormulaFid] == "CST00019"), "record-added", Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<long, object?>?>(), Arg.Any<IReadOnlyList<long>?>());
    }

    [Fact]
    public async Task UpdateRecord_OutputCarriesFormulaValue()
    {
        var h = new EngineHarness("Formula_Number");
        var target = Guid.NewGuid();
        h.WriteService.ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), target, Arg.Any<IReadOnlyDictionary<long, object?>>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(new Dictionary<long, object?> { [NameFid] = "changed" });
        h.RecordRepo.GetActiveRecordIdByPublicIdAsync(Arg.Any<AppTable>(), target, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(7L);
        h.RecordRepo.GetBulkUpsertRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [7L] = Row(7, target, "changed") });
        h.Computes(new Dictionary<long, object?> { [7] = 99m });

        var step = new PipelineStep { Id = 1, RefId = "ref_update", Type = "action", Subtype = "update-record",
            ConfigJson = $"{{\"TableId\":\"{Table.PublicId}\",\"TargetRecordId\":\"{target}\",\"FieldMappings\":[{{\"Field\":\"fid_6\",\"Value\":\"changed\"}}]}}" };

        var json = await h.RunStepAsync(step, new Dictionary<string, object> { ["_MessageId"] = Guid.NewGuid() });

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("changed", root.GetProperty($"fid_{NameFid}").GetString());
        Assert.Equal(99m, root.GetProperty($"fid_{FormulaFid}").GetDecimal());
    }
}
