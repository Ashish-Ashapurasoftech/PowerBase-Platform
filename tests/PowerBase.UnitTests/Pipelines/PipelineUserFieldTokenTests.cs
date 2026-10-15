using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>A User field (Record Owner, Last Modified By, a picked user) travels through a pipeline as a user id.
/// Mapped into a text field it must read as the person's name ("Dipak Patel", not "40021"); mapped into a User field it stays the id.</summary>
public class PipelineUserFieldTokenTests
{
    private static readonly AppTable Table = new() { Id = 100, AppId = 1, PublicId = Guid.NewGuid() };

    private static List<AppField> Fields() => new()
    {
        new() { Id = 1, Fid = 6, Name = "Name", TypeCode = "Text" },
        new() { Id = 2, Fid = 4, Name = "Record Owner", TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy" },
        new() { Id = 3, Fid = 7, Name = "Notes", TypeCode = "Text" },
        new() { Id = 4, Fid = 9, Name = "Assignee", TypeCode = "User" },
        new() { Id = 5, Fid = 10, Name = "Team", TypeCode = "MultiUser" },
    };

    private static async Task<(JsonElement Output, IReadOnlyDictionary<long, object?> Written)> RunAsync(string notesValue, string assigneeValue, string payload)
    {
        var recordRepo = Substitute.For<IRecordRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        var idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
        var interceptor = Substitute.For<IPipelineTriggerInterceptor>();
        var writeService = Substitute.For<IRecordWriteService>();
        var users = Substitute.For<IUserRepository>();
        users.GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [40021] = "Dipak Patel", [40022] = "Ronak Dhamsaniya" });
        users.GetByIdAsync(40021, Arg.Any<CancellationToken>()).Returns(new User { Id = 40021, FirstName = "Dipak", LastName = "Patel", Email = "dipak@example.com" });
        users.GetByIdAsync(40022, Arg.Any<CancellationToken>()).Returns(new User { Id = 40022, FirstName = "Ronak", LastName = "Dhamsaniya", Email = "ronak@example.com" });
        tableRepo.GetByPublicIdAsync(Table.PublicId, Arg.Any<CancellationToken>()).Returns(Table);
        fieldRepo.ListByTableAsync(Table.Id, Arg.Any<CancellationToken>()).Returns(Fields());
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        idempotency.GetByExecutionKeyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<byte[]>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var created = Guid.NewGuid();
        IReadOnlyDictionary<long, object?>? written = null;
        recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Do<IReadOnlyDictionary<long, object?>>(v => written = v),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>>()).Returns(created);
        recordRepo.GetActiveRecordIdByPublicIdAsync(Arg.Any<AppTable>(), created, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(19L);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IUserRepository)).Returns(users);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), recordRepo, writeService, tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), interceptor, uow, Substitute.For<IPipelineAuditFormatter>(),
            Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), idempotency);

        var trigger = new PipelineStep { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "record-added", ConfigJson = $"{{\"tableId\":\"{Table.PublicId}\"}}" };
        var create = new PipelineStep { Id = 2, RefId = "ref_create", Type = "action", Subtype = "create-record",
            ConfigJson = JsonSerializer.Serialize(new { TableId = Table.PublicId, FieldMappings = new[]
            {
                new { Field = "fid_7", Value = notesValue }, new { Field = "fid_9", Value = assigneeValue },
            } }) };
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var json = await (Task<string>)method.Invoke(engine, new object[]
        {
            create, payload, new Dictionary<string, object> { ["_MessageId"] = Guid.NewGuid() }, new List<PipelineStep> { trigger, create },
            new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            recordRepo, tableRepo, fieldRepo, writeService, interceptor, uow, idempotency,
            Substitute.For<IFileStorageService>(), Substitute.For<IPipelineRecordSearchService>(), CancellationToken.None
        })!;
        return (JsonDocument.Parse(json).RootElement.Clone(), written!);
    }

    [Fact]
    public async Task RecordOwnerToken_MappedIntoTextField_WritesTheUsersName_AndKeepsTheIdForAUserField()
    {
        var (_, written) = await RunAsync("Owner: {{trigger.fid_4}} - {{trigger.fid_6}}", "{{trigger.fid_4}}",
            "{\"trigger\":{\"fid_4\":40021,\"fid_6\":\"Ronak\"}}");

        Assert.Equal("Owner: Dipak Patel - Ronak", written[7]);
        Assert.Equal("40021", written[9]?.ToString());
    }

    [Fact]
    public async Task MultiUserToken_MappedIntoTextField_WritesAllNames()
    {
        var (_, written) = await RunAsync("{{trigger.fid_10}}", "", "{\"trigger\":{\"fid_10\":\"[40021,40022]\"}}");

        Assert.Equal("Dipak Patel, Ronak Dhamsaniya", written[7]);
    }

    [Fact]
    public async Task UserToken_WithNoValue_WritesNothingInsteadOfAnId()
    {
        var (_, written) = await RunAsync("{{trigger.fid_4}}", "", "{\"trigger\":{\"fid_4\":null}}");

        Assert.False(written.ContainsKey(7));
    }

    [Fact]
    public async Task NonUserTokens_AreUntouched()
    {
        var (_, written) = await RunAsync("{{trigger.fid_6}} / {{trigger.fid_7}}", "", "{\"trigger\":{\"fid_6\":\"Ronak\",\"fid_7\":\"Rajkot\"}}");

        Assert.Equal("Ronak / Rajkot", written[7]);
    }

    // ───────────────────────── sub-fields: Id / Name / First name / Last name / Email ─────────────────────────

    [Theory]
    [InlineData("id", "40021")]
    [InlineData("name", "Dipak Patel")]
    [InlineData("first_name", "Dipak")]
    [InlineData("last_name", "Patel")]
    [InlineData("email", "dipak@example.com")]
    public async Task UserSubField_ResolvesEachDetailOfThePerson(string detail, string expected)
    {
        var (_, written) = await RunAsync($"{{{{trigger.fid_4.{detail}}}}}", "", "{\"trigger\":{\"fid_4\":40021}}");

        Assert.Equal(expected, written[7]);
    }

    [Fact]
    public async Task UserSubFields_CanBeCombinedWithTextAndOtherFields()
    {
        var (_, written) = await RunAsync(
            "{{trigger.fid_6}} - {{trigger.fid_4.id}} - {{trigger.fid_4.first_name}} {{trigger.fid_4.last_name}} - {{trigger.fid_4.email}}", "",
            "{\"trigger\":{\"fid_6\":\"Ronak\",\"fid_4\":40021}}");

        Assert.Equal("Ronak - 40021 - Dipak Patel - dipak@example.com", written[7]);
    }

    [Fact]
    public async Task UserSubField_IntoAUserField_StillWritesTheId()
    {
        var (_, written) = await RunAsync("x", "{{trigger.fid_4.id}}", "{\"trigger\":{\"fid_4\":40021}}");

        Assert.Equal("40021", written[9]?.ToString());
    }

    [Fact]
    public async Task MultiUserSubField_JoinsEveryPerson()
    {
        var (_, written) = await RunAsync("{{trigger.fid_10.email}}", "", "{\"trigger\":{\"fid_10\":\"[40021,40022]\"}}");

        Assert.Equal("dipak@example.com, ronak@example.com", written[7]);
    }

    [Fact]
    public async Task UserSubField_WithNoUser_WritesNothing()
    {
        var (_, written) = await RunAsync("{{trigger.fid_4.email}}", "", "{\"trigger\":{\"fid_4\":null}}");

        Assert.False(written.ContainsKey(7));
    }

    [Fact]
    public async Task UserSubField_ForAnUnknownUser_DoesNotFailTheStep()
    {
        var (_, written) = await RunAsync("{{trigger.fid_4.id}}/{{trigger.fid_4.email}}", "", "{\"trigger\":{\"fid_4\":999}}");

        Assert.Equal("999/", written[7]);
    }

    [Fact]
    public async Task OrdinaryObjectProperties_NamedLikeUserDetails_AreNotTouched()
    {
        // fid_6 holds plain text, not a user id: the token is left for normal path resolution.
        var (_, written) = await RunAsync("{{trigger.fid_6}}", "", "{\"trigger\":{\"fid_6\":\"Ronak\"}}");

        Assert.Equal("Ronak", written[7]);
    }
}
