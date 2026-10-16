using System.Reflection;
using System.Text.Json;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// A Search Records filter value can be picked from the Reference tab, like Quickbase's "Press / to filter by a full phrase":
/// a field of an earlier step, or a detail of the person a User / User List field holds (Id, First name, Last name, Email,
/// Screen name). The search runs with the resolved values.
/// </summary>
public class PipelineSearchFilterReferenceTests
{
    private readonly AppTable _table = new() { Id = 100, PublicId = Guid.NewGuid() };

    private static List<AppField> Fields() => new()
    {
        new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text" },
        new() { Id = 7, Fid = 7, Name = "Owner Email", TypeCode = "Email" },
        new() { Id = 4, Fid = 4, Name = "Record Owner", TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy" },
        new() { Id = 8, Fid = 8, Name = "Screen", TypeCode = "Text" },
        new() { Id = 9, Fid = 9, Name = "Team", Label = "List Of Users", TypeCode = "MultiUser" }
    };

    private async Task<FilterGroup?> RunSearchAsync(string payload, params (string Field, string Op, string Value)[] rules)
    {
        var recordRepo = Substitute.For<IRecordRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var searchService = Substitute.For<IPipelineRecordSearchService>();
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(40021, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 40021, Name = "dipak.p", FirstName = "Dipak", LastName = "Patel", Email = "dipak@example.com" });
        users.GetByIdAsync(40022, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 40022, Name = "ronak.d", FirstName = "Ronak", LastName = "Dhamsaniya", Email = "ronak@example.com" });
        tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(Fields());
        // The people the user columns hold: Record Owner (one id each) and the list (JSON array or comma text).
        recordRepo.GetDistinctFieldValuesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 4), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<string>)new List<string> { "40021", "40022" }, false));
        recordRepo.GetDistinctFieldValuesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 9), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<string>)new List<string> { "[40021,40022]", "40022" }, false));

        FilterGroup? captured = null;
        searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.ArgAt<FilterGroup?>(3); return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>()); });
        recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.ArgAt<FilterGroup?>(4); return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>()); });

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(searchService);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(users);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), recordRepo, Substitute.For<IRecordWriteService>(), tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());

        var trigger = new PipelineStep { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "record-added", ConfigJson = $"{{\"tableId\":\"{_table.PublicId}\"}}" };
        var search = new PipelineStep
        {
            Id = 2, RefId = "ref_search", Type = "query", Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new
            {
                TableId = _table.PublicId.ToString(),
                FilterGroups = new[] { new { LogicalOp = "AND", Rules = rules.Select(r => new { r.Field, Operator = r.Op, r.Value }).ToArray() } }
            })
        };
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task<string>)method.Invoke(engine, new object[]
        {
            search, payload, new Dictionary<string, object>(), new List<PipelineStep> { trigger, search }, new Dictionary<string, object>(),
            1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_2",
            recordRepo, tableRepo, fieldRepo, Substitute.For<IRecordWriteService>(),
            Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(),
            searchService, CancellationToken.None
        })!;
        return captured;
    }

    private static IEnumerable<FilterCondition> Conditions(FilterGroup? group) =>
        group?.Nodes.SelectMany(node => node.Condition != null ? new[] { node.Condition } : Conditions(node.Group)) ?? Enumerable.Empty<FilterCondition>();

    private static string? ValueFor(FilterGroup? group, long fieldId) => Conditions(group).Single(c => c.FieldId == fieldId).Value;

    [Fact]
    public async Task FilterValue_PickedFromAnEarlierStep_IsResolved()
    {
        var filter = await RunSearchAsync("{\"trigger\":{\"fid_6\":\"Ronak\"}}", ("fid_6", "is", "{{trigger.fid_6}}"));

        Assert.Equal("Ronak", ValueFor(filter, 6));
    }

    [Fact]
    public async Task FilterValue_MixingTypedTextAndAReference_IsResolved()
    {
        var filter = await RunSearchAsync("{\"trigger\":{\"fid_6\":\"Ronak\"}}", ("fid_6", "contains", "Mr {{trigger.fid_6}}"));

        Assert.Equal("Mr Ronak", ValueFor(filter, 6));
    }

    [Theory]
    [InlineData("email", "dipak@example.com")]
    [InlineData("first_name", "Dipak")]
    [InlineData("last_name", "Patel")]
    [InlineData("screen_name", "dipak.p")]
    [InlineData("id", "40021")]
    public async Task FilterValue_UserDetail_OfRecordOwner_IsResolved(string detail, string expected)
    {
        var filter = await RunSearchAsync("{\"trigger\":{\"fid_4\":40021}}", ("fid_8", "is", $"{{{{trigger.fid_4.{detail}}}}}"));

        Assert.Equal(expected, ValueFor(filter, 8));
    }

    [Fact]
    public async Task FilterOnAUserField_ByTheWholeUserField_ComparesTheUserId()
    {
        var filter = await RunSearchAsync("{\"trigger\":{\"fid_4\":40021}}", ("fid_4", "is", "{{trigger.fid_4}}"));

        Assert.Equal("40021", ValueFor(filter, 4));
    }

    [Fact]
    public async Task FilterValue_UserListDetail_JoinsEveryPerson()
    {
        var filter = await RunSearchAsync("{\"trigger\":{\"fid_4\":\"[40021,40022]\"}}", ("fid_7", "is", "{{trigger.fid_4.email}}"));

        Assert.Equal("dipak@example.com, ronak@example.com", ValueFor(filter, 7));
    }

    // ── filtering on a person's detail ("Record Owner › Email", "List Of Users › First Name") ──

    private static string Describe(FilterNode? node) => node == null ? "ALL"
        : node.Condition != null ? $"{node.Condition.Operator}({node.Condition.FieldId}{(node.Condition.Value == null ? "" : "," + node.Condition.Value)})"
        : $"{node.Group!.Logic}[{string.Join(" ", node.Group.Nodes.Select(Describe))}]";

    [Theory]
    [InlineData("fid_4.email", "is", "dipak@example.com", "and[in(4,[\"40021\"])]")]
    [InlineData("fid_4.screen_name", "starts-with", "ronak", "and[in(4,[\"40022\"])]")]
    [InlineData("fid_4.email", "contains", "example.com", "and[isNotEmpty(4)]")]
    [InlineData("fid_4.email", "is", "nobody@example.com", "and[eq(4,-1)]")]
    [InlineData("fid_4.first_name", "is-not", "Dipak", "and[or[in(4,[\"40022\"]) isEmpty(4)]]")]
    [InlineData("fid_9.first_name", "is", "Ronak", "and[includes(9,40022)]")]
    [InlineData("fid_9.email", "not-contains", "dipak", "and[or[notIncludes(9,40021) isEmpty(9)]]")]
    [InlineData("fid_4.id", "is", "40021", "and[in(4,[\"40021\"])]")]
    public async Task FilterField_UserDetail_SearchesTheMatchingUsersRecords(string field, string op, string value, string expected)
    {
        var filter = await RunSearchAsync("{\"trigger\":{}}", (field, op, value));

        Assert.Equal(expected, Describe(new FilterNode { Group = filter }));
    }

    [Fact]
    public async Task FilterField_UserDetail_ComparedWithAReference()
    {
        var filter = await RunSearchAsync("{\"trigger\":{\"fid_6\":\"RONAK@example.com\"}}", ("fid_4.email", "is", "{{trigger.fid_6}}"));

        Assert.Equal("and[in(4,[\"40022\"])]", Describe(new FilterNode { Group = filter }));
    }

    [Fact]
    public async Task FilterField_UserDetail_CombinesWithOtherRules()
    {
        var filter = await RunSearchAsync("{\"trigger\":{}}", ("fid_6", "is", "Lead"), ("fid_4.email", "is", "dipak@example.com"));

        Assert.Equal("and[eq(6,Lead) in(4,[\"40021\"])]", Describe(new FilterNode { Group = filter }));
    }

    [Fact]
    public async Task FilterField_NegatedUserDetail_ThatEveryoneFails_PutsNoCondition()
    {
        var filter = await RunSearchAsync("{\"trigger\":{}}", ("fid_6", "is", "Lead"), ("fid_4.email", "is-not", "nobody@example.com"));

        Assert.Equal("and[eq(6,Lead)]", Describe(new FilterNode { Group = filter }));
    }

    [Fact]
    public async Task FilterField_UserDetail_HasNoLimit_ManyUsersAreCheckedInMemory()
    {
        // 2,500 different Record Owners; the even ones have an "@even.com" email — 1,250 users on either side, more ids than one SQL
        // query may carry. Every user is read, and the rule is checked on each page of the search instead of being cut short.
        const int userCount = 2_500;
        var recordRepo = Substitute.For<IRecordRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var searchService = Substitute.For<IPipelineRecordSearchService>();
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var id = ci.ArgAt<long>(0); return new User { Id = id, Name = $"user{id}", Email = $"user{id}@{(id % 2 == 0 ? "even" : "odd")}.com" }; });
        tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(Fields());
        recordRepo.GetDistinctFieldValuesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 4), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<string>)Enumerable.Range(1, userCount).Select(i => i.ToString()).ToList(), false));
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (long owner = 2; owner <= 7; owner++)
            rows.Add(new Dictionary<string, object?> { ["Id"] = owner, ["PublicId"] = Guid.NewGuid(), ["CreatedBy"] = owner, ["f_6"] = $"Record {owner}" });
        FilterGroup? sqlFilter = null;
        recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
                Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { sqlFilter = ci.ArgAt<FilterGroup?>(4); return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rows); });
        recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<int>(p => p > 1), Arg.Any<int>(),
                Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>()));
        searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci => { sqlFilter = ci.ArgAt<FilterGroup?>(3); return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rows); });

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(searchService);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(users);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), recordRepo, Substitute.For<IRecordWriteService>(), tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());
        var search = new PipelineStep
        {
            Id = 2, RefId = "ref_search", Type = "query", Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new
            {
                TableId = _table.PublicId.ToString(),
                FilterGroups = new[] { new { LogicalOp = "AND", Rules = new[] { new { Field = "fid_4.email", Operator = "contains", Value = "@even.com" } } } }
            })
        };
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var json = await (Task<string>)method.Invoke(engine, new object[]
        {
            search, "{}", new Dictionary<string, object>(), new List<PipelineStep> { search }, new Dictionary<string, object>(),
            1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_2",
            recordRepo, tableRepo, fieldRepo, Substitute.For<IRecordWriteService>(),
            Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(),
            searchService, CancellationToken.None
        })!;

        // Every user of the column was read — no cap.
        await recordRepo.Received().GetDistinctFieldValuesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 4), int.MaxValue, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await users.Received(userCount).GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        // The SQL carries no 1,250-id list; the rule was applied to the rows in memory.
        Assert.DoesNotContain(Conditions(sqlFilter), c => c.FieldId == 4);
        using var doc = JsonDocument.Parse(json);
        var owners = doc.RootElement.GetProperty("records").EnumerateArray()
            .Select(r => r.GetProperty("fid_6").GetString()).ToList();
        Assert.Equal(new[] { "Record 2", "Record 4", "Record 6" }, owners);
    }

    [Fact]
    public async Task FilterValue_TypedLiteral_IsUnchanged()
    {
        var filter = await RunSearchAsync("{\"trigger\":{}}", ("fid_6", "is", "Ronak"));

        Assert.Equal("Ronak", ValueFor(filter, 6));
    }
}
