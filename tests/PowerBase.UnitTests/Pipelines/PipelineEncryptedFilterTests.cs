using System.Reflection;
using System.Text.Json;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Regression coverage for the Pipeline module's encrypted-field filtering bug: an encrypted
/// field's physical column stores ciphertext, so a SQL LIKE/= condition against it can never
/// match. The "search-records" step must split any encrypted-field conditions out of the SQL
/// filter tree and evaluate them in memory against decrypted candidate rows instead — the same
/// pattern RunReportQueryHandler already uses for formula (compute-on-read) fields.
/// </summary>
public class PipelineEncryptedFilterTests
{
    private readonly PipelineEngine _engine;
    private readonly IRecordRepository _recordRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IPipelineRecordSearchService _searchService;
    private readonly AppTable _table = new() { Id = 100, PublicId = Guid.NewGuid() };

    public PipelineEncryptedFilterTests()
    {
        _recordRepo = Substitute.For<IRecordRepository>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _searchService = Substitute.For<IPipelineRecordSearchService>();
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(_searchService);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());

        _engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(),
            _recordRepo,
            Substitute.For<IRecordWriteService>(),
            _tableRepo,
            _fieldRepo,
            Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(),
            serviceProvider,
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    private static List<AppField> Fields() => new()
    {
        new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text", IsEncrypted = true },
        new() { Id = 7, Fid = 7, Name = "Status", TypeCode = "Text", IsEncrypted = false }
    };

    private static PipelineStep SearchStep(object config) => new()
    {
        Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records",
        ConfigJson = JsonSerializer.Serialize(config)
    };

    private static Dictionary<string, object?> Row(string? name, string? status = null, Guid? publicId = null) => new()
    {
        ["Id"] = 1L,
        ["PublicId"] = publicId ?? Guid.NewGuid(),
        ["f_6"] = name,
        ["f_7"] = status
    };

    private async Task<(string ResultJson, FilterGroup? PhysicalFilterSent)> RunSearchAsync(object config, IEnumerable<IReadOnlyDictionary<string, object?>> candidateRows)
    {
        FilterGroup? captured = null;
        var candidateList = candidateRows.ToList();
        _searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<FilterGroup?>(3);
                // Simulate what the real (SQL-backed) SearchAsync would do: apply the physical
                // filter tree server-side before returning candidates. FormulaFilterSorter's
                // per-condition matcher reads straight from the row dict for any non-computed
                // field, so it doubles here as a stand-in SQL evaluator for the mock.
                IReadOnlyList<IReadOnlyDictionary<string, object?>> narrowed = captured == null
                    ? candidateList
                    : FormulaFilterSorter.ApplyFormulaFilters(
                        candidateList.Select(r => (Row: r, Computed: (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>())),
                        captured, Fields()).Select(p => p.Row).ToList();
                return Task.FromResult(narrowed);
            });
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(Fields());

        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var step = SearchStep(config);
        var json = await (Task<string>)method.Invoke(_engine, new object[]
        {
            step, "{}", new Dictionary<string, object>(), new List<PipelineStep> { step }, new Dictionary<string, object>(),
            1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            _recordRepo, _tableRepo, _fieldRepo, Substitute.For<IRecordWriteService>(),
            Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(),
            _searchService, CancellationToken.None
        })!;
        return (json, captured);
    }

    private static object FilterGroupConfig(params (string field, string op, string value)[] rules) => new
    {
        TableId = "PLACEHOLDER",
        FilterGroups = new List<object>
        {
            new { LogicalOp = "AND", Rules = rules.Select(r => (object)new { Field = r.field, Operator = r.op, Value = r.value }).ToList() }
        }
    };

    private object ConfigWithFilters((string field, string op, string value)[] rules)
    {
        var cfg = FilterGroupConfig(rules);
        var json = JsonSerializer.Serialize(cfg).Replace("PLACEHOLDER", _table.PublicId.ToString());
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public async Task EncryptedField_Contains_MatchesDecryptedCandidateInMemory()
    {
        var candidates = new[] { Row("Ronak Dhamsaniya"), Row("Someone Else") };
        var (json, physicalFilter) = await RunSearchAsync(ConfigWithFilters(new[] { ("fid_6", "contains", "ronak") }), candidates);

        Assert.Contains("Ronak Dhamsaniya", json);
        Assert.DoesNotContain("Someone Else", json);
        // The encrypted condition must never reach SQL as a physical WHERE clause.
        Assert.True(physicalFilter is null || !ContainsFieldCondition(physicalFilter, 6));
    }

    [Fact]
    public async Task EncryptedField_Equals_MatchesExactDecryptedValueCaseInsensitively()
    {
        var candidates = new[] { Row("Ronak"), Row("ronak"), Row("Not Ronak") };
        var (json, _) = await RunSearchAsync(ConfigWithFilters(new[] { ("fid_6", "is", "RONAK") }), candidates);

        using var doc = JsonDocument.Parse(json);
        var records = doc.RootElement.GetProperty("records");
        Assert.Equal(2, records.GetArrayLength());
    }

    [Fact]
    public async Task EncryptedAndNormalFilter_And_BothConditionsMustMatch()
    {
        var candidates = new[]
        {
            Row("Ronak Dhamsaniya", "Active"),
            Row("Ronak Dhamsaniya", "Inactive"),
            Row("Someone Else", "Active")
        };
        var (json, physicalFilter) = await RunSearchAsync(
            ConfigWithFilters(new[] { ("fid_6", "contains", "ronak"), ("fid_7", "is", "Active") }), candidates);

        using var doc = JsonDocument.Parse(json);
        var records = doc.RootElement.GetProperty("records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal("Active", records[0].GetProperty("fid_7").GetString());
        // Normal (non-encrypted) field condition must still be pushed to SQL.
        Assert.NotNull(physicalFilter);
        Assert.True(ContainsFieldCondition(physicalFilter!, 7));
        Assert.False(ContainsFieldCondition(physicalFilter!, 6));
    }

    [Fact]
    public async Task EncryptedAndNormalFilter_Or_EitherConditionMayMatch()
    {
        var group = new
        {
            TableId = _table.PublicId.ToString(),
            FilterGroups = new List<object>
            {
                new
                {
                    LogicalOp = "OR",
                    Rules = new List<object>
                    {
                        new { Field = "fid_6", Operator = "contains", Value = "ronak" },
                        new { Field = "fid_7", Operator = "is", Value = "Active" }
                    }
                }
            }
        };
        var candidates = new[]
        {
            Row("Ronak Dhamsaniya", "Inactive"), // matches via encrypted contains
            Row("Someone Else", "Active"),        // matches via normal equals
            Row("Someone Else", "Inactive")       // matches neither
        };
        var (json, _) = await RunSearchAsync(group, candidates);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task EncryptedField_NoMatch_ReturnsEmptyResults()
    {
        var candidates = new[] { Row("Someone Else"), Row("Another Person") };
        var (json, _) = await RunSearchAsync(ConfigWithFilters(new[] { ("fid_6", "contains", "ronak") }), candidates);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task EncryptedField_DecryptionFailure_LeavesCiphertextAndSafelyNonMatches()
    {
        // FieldEncryptionContext swallows decrypt failures and leaves the raw ciphertext in
        // place. From the pipeline's point of view that just looks like a row whose value
        // doesn't contain the searched plaintext — it must never crash or falsely match.
        var candidates = new[] { Row("U2FsdGVkX1+abcXYZciphertextlooking==") };
        var (json, _) = await RunSearchAsync(ConfigWithFilters(new[] { ("fid_6", "contains", "ronak") }), candidates);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task UnencryptedField_Filter_StillPushesToSqlUnchanged()
    {
        var candidates = new[] { Row("Ronak", "Active") };
        var (json, physicalFilter) = await RunSearchAsync(ConfigWithFilters(new[] { ("fid_7", "is", "Active") }), candidates);

        Assert.Contains("Active", json);
        Assert.NotNull(physicalFilter);
        Assert.True(ContainsFieldCondition(physicalFilter!, 7));
    }

    private static bool ContainsFieldCondition(FilterGroup group, long fieldId)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition != null && node.Condition.FieldId == fieldId) return true;
            if (node.Group != null && ContainsFieldCondition(node.Group, fieldId)) return true;
        }
        return false;
    }
}
