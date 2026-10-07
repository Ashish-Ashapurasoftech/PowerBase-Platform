using System.Reflection;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// A PowerFlow acts as its owner, so a record step must respect the owner's app membership and
/// table-level role access exactly like the Records API does (Quickbase parity).
/// </summary>
public class PipelineAccessGuardTests
{
    private readonly Guid _tableId = Guid.NewGuid();
    private readonly AppTable _table;
    private readonly List<AppField> _fields;
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();
    private readonly PipelineEngine _engine;

    public PipelineAccessGuardTests()
    {
        _table = new AppTable { Id = 7, PublicId = _tableId, AppId = 3, Name = "A2" };
        _fields = new List<AppField>
        {
            new() { Id = 70, Fid = 6, Name = "Name", Label = "Name" },
            new() { Id = 71, Fid = 7, Name = "City", Label = "City" }
        };
        _tables.GetByPublicIdAsync(_tableId, Arg.Any<CancellationToken>()).Returns(_table);
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(_fields);
        _services.GetService(typeof(IAppAccessService)).Returns(_access);
        _services.GetService(typeof(IAppTableRepository)).Returns(_tables);
        _services.GetService(typeof(IAppFieldRepository)).Returns(_fieldRepo);
        _services.GetService(typeof(IRolePermissionEnforcer)).Returns(_enforcer);

        _engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            _tables, _fieldRepo, Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), Substitute.For<IServiceProvider>(),
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    private void GrantAccess(TableAccessContext access) =>
        _enforcer.GetTableAccessAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).Returns(access);

    private Task Enforce(PipelineStep step, IServiceProvider? services = null)
    {
        var method = typeof(PipelineEngine).GetMethod("EnforceStepAccessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(_engine, new object[] { step, services ?? _services, CancellationToken.None })!;
    }

    private PipelineStep Step(string subtype, string? mappingField = null, string type = "action")
    {
        var mappings = mappingField == null ? "" : $",\"fieldMappings\":[{{\"field\":\"{mappingField}\",\"value\":\"x\"}}]";
        return new PipelineStep { Id = 1, Type = type, Subtype = subtype, ConfigJson = $"{{\"tableId\":\"{_tableId}\"{mappings}}}" };
    }

    private static TableAccessContext Restricted(bool canAdd = true, bool canDelete = true,
        string view = RecordScopes.AllRecords, string modify = RecordScopes.AllRecords, params long[] editable) => new()
    {
        Unrestricted = false, CanAdd = canAdd, CanDelete = canDelete, ViewScope = view, ModifyScope = modify,
        EditableFieldIds = editable.ToHashSet()
    };

    [Fact]
    public async Task CreateRecord_WithoutAddPermission_FailsNonRetryablyWithPermissionMessage()
    {
        GrantAccess(Restricted(canAdd: false));

        var act = () => Enforce(Step("create-record"));

        var ex = await act.Should().ThrowAsync<PipelineNonRetryableException>();
        ex.WithMessage("*You don't have permission to perform this action*A2*");
    }

    [Fact]
    public async Task CreateRecord_WithAddPermission_Passes()
    {
        GrantAccess(Restricted(canAdd: true, editable: new long[] { 6 }));

        await Enforce(Step("create-record", "fid_6"));
    }

    [Fact]
    public async Task CreateRecord_MappingNonEditableField_Fails()
    {
        GrantAccess(Restricted(canAdd: true, editable: new long[] { 6 }));

        var act = () => Enforce(Step("create-record", "fid_7"));

        var ex = await act.Should().ThrowAsync<PipelineNonRetryableException>();
        ex.WithMessage("*'City'*");
    }

    [Fact]
    public async Task NonMemberOwner_FailsBeforeAnyTableAccess()
    {
        _access.RequireMembershipByTablePublicIdAsync(_tableId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("You are not a member of this app."));

        var act = () => Enforce(Step("create-record"));

        var ex = await act.Should().ThrowAsync<PipelineNonRetryableException>();
        ex.WithMessage("*not a member*");
        await _enforcer.DidNotReceiveWithAnyArgs().GetTableAccessAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("update-record")]
    [InlineData("delete-record")]
    [InlineData("search-records")]
    [InlineData("look-up-record")]
    [InlineData("prepare-bulk-upsert")]
    public async Task RecordSteps_WithNoAccess_Fail(string subtype)
    {
        GrantAccess(Restricted(canAdd: false, canDelete: false, view: RecordScopes.None, modify: RecordScopes.None));

        var act = () => Enforce(Step(subtype, type: subtype is "search-records" or "look-up-record" ? "query" : "action"));

        var ex = await act.Should().ThrowAsync<PipelineNonRetryableException>();
        ex.WithMessage("*You don't have permission*");
    }

    [Fact]
    public async Task DeleteRecord_OnlyNeedsDeletePermission()
    {
        GrantAccess(Restricted(canAdd: false, canDelete: true, view: RecordScopes.None, modify: RecordScopes.None));

        await Enforce(Step("delete-record"));
    }

    [Fact]
    public async Task BulkUpsert_RequiresBothAddAndModify()
    {
        GrantAccess(Restricted(canAdd: true, modify: RecordScopes.None));

        var act = () => Enforce(Step("prepare-bulk-upsert"));

        await act.Should().ThrowAsync<PipelineNonRetryableException>();
    }

    [Fact]
    public async Task UnrestrictedAccess_Passes()
    {
        GrantAccess(new TableAccessContext { Unrestricted = true });

        await Enforce(Step("create-record"));
    }

    [Theory]
    [InlineData("condition", "condition")]
    [InlineData("loop", "action")]
    [InlineData("send-email", "action")]
    [InlineData("make-request", "action")]
    [InlineData("schedule", "trigger")]
    public async Task StepsWithoutRecordAccess_AreNotChecked(string subtype, string type)
    {
        await Enforce(Step(subtype, type: type));

        await _access.DidNotReceiveWithAnyArgs().RequireMembershipByTablePublicIdAsync(default, default);
    }

    [Fact]
    public async Task MissingAccessServices_SkipTheCheck()
    {
        var bare = Substitute.For<IServiceProvider>();

        await Enforce(Step("create-record"), bare);
    }

    [Fact]
    public async Task StepWithoutTable_IsLeftToTheStepItself()
    {
        await Enforce(new PipelineStep { Type = "action", Subtype = "create-record", ConfigJson = "{}" });
        await Enforce(new PipelineStep { Type = "action", Subtype = "create-record", ConfigJson = null });
    }

    [Theory]
    [InlineData("add-bulk-upsert-row")]
    [InlineData("commit-upsert")]
    public async Task BulkUpsertRowAndCommit_NeedAddAndModifyOnTheirTable(string subtype)
    {
        GrantAccess(Restricted(canAdd: true, modify: RecordScopes.None));

        var act = () => Enforce(Step(subtype));

        await act.Should().ThrowAsync<PipelineNonRetryableException>();
    }

    [Fact]
    public void ActingUser_DefaultsToEngineIdentity_AndFollowsTheSavedAccountWhenSet()
    {
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(42L);
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            _tables, _fieldRepo, Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), queryContext,
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), Substitute.For<IServiceProvider>(),
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
        var property = typeof(PipelineEngine).GetProperty("StepActingUserId", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var local = (AsyncLocal<long?>)typeof(PipelineEngine).GetField("_stepActingUserId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;

        ((long)property.GetValue(engine)!).Should().Be(42L);
        local.Value = 999L; // token owner of a saved account
        ((long)property.GetValue(engine)!).Should().Be(999L);
        local.Value = null;
        ((long)property.GetValue(engine)!).Should().Be(42L);
    }

    [Fact]
    public async Task SuccessfulLookup_IsCachedPerScope()
    {
        GrantAccess(Restricted(canAdd: true));

        await Enforce(Step("create-record"));
        await Enforce(Step("create-record"));

        await _access.Received(1).RequireMembershipByTablePublicIdAsync(_tableId, Arg.Any<CancellationToken>());
    }
}
