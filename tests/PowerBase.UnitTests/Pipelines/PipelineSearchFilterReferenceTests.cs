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
        new() { Id = 8, Fid = 8, Name = "Screen", TypeCode = "Text" }
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

    [Fact]
    public async Task FilterValue_TypedLiteral_IsUnchanged()
    {
        var filter = await RunSearchAsync("{\"trigger\":{}}", ("fid_6", "is", "Ronak"));

        Assert.Equal("Ronak", ValueFor(filter, 6));
    }
}
