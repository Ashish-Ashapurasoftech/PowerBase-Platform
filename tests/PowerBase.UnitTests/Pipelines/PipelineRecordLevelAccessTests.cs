using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Row-level and field-level rules for a PowerFlow acting as its owner: "own records" scope, the role's
/// record filter, hidden fields and non-editable fields. Mirrors what the Records API enforces.
/// </summary>
public class PipelineRecordLevelAccessTests
{
    private readonly AppTable _table = new() { Id = 7, PublicId = Guid.NewGuid(), AppId = 3, Name = "A1" };
    private readonly List<AppField> _fields;
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();
    private readonly IPipelineRecordSearchService _search = Substitute.For<IPipelineRecordSearchService>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();
    private readonly PipelineEngine _engine;

    public PipelineRecordLevelAccessTests()
    {
        _fields = new List<AppField>
        {
            new() { Id = 6, Fid = 6, Name = "Name", Label = "Name", TypeCode = "Text" },
            new() { Id = 7, Fid = 7, Name = "Secret", Label = "Secret", TypeCode = "Text" },
            new() { Id = 8, Fid = 8, Name = "Calc", Label = "Calc", TypeCode = "Formula" }
        };
        _tables.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(_fields);
        _services.GetService(typeof(IAppAccessService)).Returns(_access);
        _services.GetService(typeof(IAppTableRepository)).Returns(_tables);
        _services.GetService(typeof(IAppFieldRepository)).Returns(_fieldRepo);
        _services.GetService(typeof(IRolePermissionEnforcer)).Returns(_enforcer);

        _engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), _records, Substitute.For<IRecordWriteService>(), _tables, _fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(), _services, Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    private void Role(TableAccessContext access) =>
        _enforcer.GetTableAccessAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).Returns(access);

    private TableAccessContext Restricted(FilterGroup? viewFilter = null, long? ownOnly = null, string modify = RecordScopes.AllRecords,
        string view = RecordScopes.AllRecords, bool canAdd = true, bool canDelete = true, params int[] hiddenFids) => new()
    {
        Unrestricted = false, CanAdd = canAdd, CanDelete = canDelete, ViewScope = view, ModifyScope = modify,
        ViewFilter = viewFilter, RestrictToCreatedBy = ownOnly,
        VisibleFields = _fields.Where(f => !hiddenFids.Contains(f.Fid!.Value)).ToList(),
        EditableFieldIds = new HashSet<long> { 6 }
    };

    private static FilterGroup SomeFilter() => new()
    {
        Logic = "and",
        Nodes = new() { new() { Condition = new() { FieldId = 6, Operator = "eq", Value = "x" } } }
    };

    private static object? CallStatic(string name, params object?[] args) =>
        typeof(PipelineEngine).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);

    private void InStepScope(IServiceProvider? services = null)
    {
        var local = (AsyncLocal<IServiceProvider?>)typeof(PipelineEngine)
            .GetField("_stepServices", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_engine)!;
        local.Value = services ?? _services;
    }

    private Task EnforceRecord(PipelineRecordAccessKind kind, Guid record, IEnumerable<AppField>? written = null) =>
        (Task)typeof(PipelineEngine).GetMethod("EnforceRecordAccessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_engine, new object?[] { _records, _table, _fields, record, kind, written, CancellationToken.None })!;

    // ---- search-level restrictions -------------------------------------------------------------

    [Fact]
    public void RowRestrictions_Unrestricted_LeaveTheSearchAlone()
    {
        var tree = SomeFilter();

        CallStatic("ApplyRoleRowRestrictions", tree, null, _fields).Should().BeSameAs(tree);
        CallStatic("ApplyRoleRowRestrictions", tree, Restricted(), _fields).Should().BeSameAs(tree);
    }

    [Fact]
    public void RowRestrictions_OwnRecordsAndRoleFilter_AreAndedWithTheSearch()
    {
        var tree = SomeFilter();
        var roleFilter = SomeFilter();

        var combined = (FilterGroup)CallStatic("ApplyRoleRowRestrictions", tree, Restricted(roleFilter, ownOnly: 42), _fields)!;

        combined.Logic.Should().Be("and");
        combined.Nodes.Should().HaveCount(3);
        combined.Nodes[0].Group.Should().BeSameAs(tree);
        combined.Nodes[1].Group.Should().BeSameAs(roleFilter);
        combined.Nodes[2].Condition.Should().BeEquivalentTo(new FilterCondition { FieldId = 4, Operator = "eq", Value = "42" });
    }

    [Fact]
    public void RowRestrictions_WithNoUserFilter_StillAppliesTheRoleRestriction()
    {
        var combined = (FilterGroup)CallStatic("ApplyRoleRowRestrictions", null, Restricted(ownOnly: 42), _fields)!;

        combined.Nodes.Should().ContainSingle().Which.Condition!.FieldId.Should().Be(4);
    }

    [Fact]
    public void RowRestrictions_RoleFilterOnCalculatedField_FailsClosed()
    {
        var roleFilter = new FilterGroup { Logic = "and", Nodes = new() { new() { Condition = new() { FieldId = 8, Operator = "eq", Value = "1" } } } };

        var act = () => CallStatic("ApplyRoleRowRestrictions", null, Restricted(roleFilter), _fields);

        act.Should().Throw<TargetInvocationException>().WithInnerException<PipelineNonRetryableException>().WithMessage("*calculated fields*");
    }

    private async Task<string> RunStep(PipelineStep step)
    {
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<string>)method.Invoke(_engine, new object[]
        {
            step, "{}", new Dictionary<string, object>(), new List<PipelineStep> { step }, new Dictionary<string, object>(),
            1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            _records, _tables, _fieldRepo, Substitute.For<IRecordWriteService>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(),
            _search, CancellationToken.None
        })!;
    }

    [Fact]
    public async Task SearchRecords_AsRestrictedRole_SendsTheRoleRestrictionAndHidesUnreadableFields()
    {
        InStepScope();
        Role(Restricted(ownOnly: 42, hiddenFids: 7));
        FilterGroup? sent = null;
        _search.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<FilterGroup?>(3);
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = new List<IReadOnlyDictionary<string, object?>>
                    { new Dictionary<string, object?> { ["Id"] = 1L, ["PublicId"] = Guid.NewGuid(), ["f_6"] = "visible", ["f_7"] = "hidden" } };
                return Task.FromResult(rows);
            });
        var step = new PipelineStep { Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new { TableId = _table.PublicId.ToString() }) };

        var json = await RunStep(step);

        sent.Should().NotBeNull();
        sent!.Nodes.Should().Contain(node => node.Condition != null && node.Condition.FieldId == 4 && node.Condition.Value == "42");
        using var result = JsonDocument.Parse(json);
        var record = result.RootElement.GetProperty("records")[0];
        record.GetProperty("fid_6").GetString().Should().Be("visible");
        record.TryGetProperty("fid_7", out _).Should().BeFalse("the role cannot read that field");
    }

    [Fact]
    public async Task SearchRecords_AsUnrestrictedRole_ReturnsEverythingUnchanged()
    {
        InStepScope();
        Role(new TableAccessContext { Unrestricted = true });
        FilterGroup? sent = SomeFilter();
        _search.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<FilterGroup?>(3);
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = new List<IReadOnlyDictionary<string, object?>>
                    { new Dictionary<string, object?> { ["Id"] = 1L, ["PublicId"] = Guid.NewGuid(), ["f_6"] = "a", ["f_7"] = "b" } };
                return Task.FromResult(rows);
            });
        var step = new PipelineStep { Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new { TableId = _table.PublicId.ToString() }) };

        var json = await RunStep(step);

        sent.Should().BeNull();
        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("records")[0].TryGetProperty("fid_7", out _).Should().BeTrue();
    }

    // ---- look-up ---------------------------------------------------------------------------------

    private PipelineStep LookUp() => new()
    {
        Id = 2, RefId = "ref_lookup", Type = "query", Subtype = "look-up-record",
        ConfigJson = JsonSerializer.Serialize(new { TablePublicId = _table.PublicId.ToString(), RecordIdValue = "1" })
    };

    private void StubRow(Guid publicId) =>
        _records.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [1L] = new Dictionary<string, object?> { ["Id"] = 1L, ["PublicId"] = publicId, ["f_6"] = "v", ["f_7"] = "secret" }
            });

    [Fact]
    public async Task LookUp_RecordOutsideTheRoleFilter_IsDenied()
    {
        InStepScope();
        var id = Guid.NewGuid();
        StubRow(id);
        Role(Restricted(viewFilter: SomeFilter()));
        _records.ExistsWithViewFilterAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), id, Arg.Any<FilterGroup>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var act = () => RunStep(LookUp());

        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*not accessible*");
    }

    [Fact]
    public async Task LookUp_RecordInsideTheRoleFilter_ReturnsOnlyReadableFields()
    {
        InStepScope();
        var id = Guid.NewGuid();
        StubRow(id);
        Role(Restricted(ownOnly: 42, hiddenFids: 7));
        _records.ExistsWithViewFilterAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), id, Arg.Any<FilterGroup>(), 42L, Arg.Any<CancellationToken>())
            .Returns(true);

        var json = await RunStep(LookUp());

        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("fid_6").GetString().Should().Be("v");
        result.RootElement.TryGetProperty("fid_7", out _).Should().BeFalse();
    }

    // ---- update / delete / upload: record-level --------------------------------------------------

    [Fact]
    public async Task Modify_UnderOwnRecordsScope_ChecksOwnership()
    {
        InStepScope();
        Role(Restricted(modify: RecordScopes.OwnRecords));
        var record = Guid.NewGuid();
        _enforcer.EnsureRecordOwnedAsync(_table, record, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("You can only modify records you created."));

        var act = () => EnforceRecord(PipelineRecordAccessKind.Modify, record);

        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*only modify records you created*");
    }

    [Fact]
    public async Task Delete_UnderOwnRecordsScope_ChecksOwnership_AndPassesForOwnRecord()
    {
        InStepScope();
        Role(Restricted(view: RecordScopes.OwnRecords, ownOnly: 42));
        var record = Guid.NewGuid();
        _records.ExistsWithViewFilterAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), record, Arg.Any<FilterGroup>(), 42L, Arg.Any<CancellationToken>())
            .Returns(true);

        await EnforceRecord(PipelineRecordAccessKind.Delete, record);

        await _enforcer.Received(1).EnsureRecordOwnedAsync(_table, record, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Modify_RecordOutsideTheRoleFilter_IsDenied()
    {
        InStepScope();
        Role(Restricted(viewFilter: SomeFilter()));
        var record = Guid.NewGuid();
        _records.ExistsWithViewFilterAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), record, Arg.Any<FilterGroup>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var act = () => EnforceRecord(PipelineRecordAccessKind.Modify, record);

        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*not accessible*");
    }

    [Fact]
    public async Task Modify_WritingANonEditableField_IsDenied()
    {
        InStepScope();
        Role(Restricted());

        var act = () => EnforceRecord(PipelineRecordAccessKind.Modify, Guid.NewGuid(), new[] { _fields[1] });

        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*'Secret'*");
    }

    [Fact]
    public async Task Modify_WritingAnEditableField_Passes()
    {
        InStepScope();
        Role(Restricted());

        await EnforceRecord(PipelineRecordAccessKind.Modify, Guid.NewGuid(), new[] { _fields[0] });
    }

    [Fact]
    public async Task Delete_WithoutDeletePermission_IsDenied()
    {
        InStepScope();
        Role(Restricted(canDelete: false));

        var act = () => EnforceRecord(PipelineRecordAccessKind.Delete, Guid.NewGuid());

        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*delete records*");
    }

    [Fact]
    public async Task RecordLevel_UnrestrictedIdentity_SkipsEveryCheck()
    {
        InStepScope();
        Role(new TableAccessContext { Unrestricted = true });

        await EnforceRecord(PipelineRecordAccessKind.Delete, Guid.NewGuid());

        await _records.DidNotReceiveWithAnyArgs().ExistsWithViewFilterAsync(default!, default!, default, default, default, default);
        await _enforcer.DidNotReceiveWithAnyArgs().EnsureRecordOwnedAsync(default!, default, default);
    }

    [Fact]
    public async Task RecordLevel_WithoutAccessServices_IsSkipped()
    {
        InStepScope(Substitute.For<IServiceProvider>());

        await EnforceRecord(PipelineRecordAccessKind.Modify, Guid.NewGuid());
    }

    // ---- trigger source table + bulk upsert --------------------------------------------------------

    private Task Enforce(PipelineStep step) =>
        (Task)typeof(PipelineEngine).GetMethod("EnforceStepAccessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_engine, new object[] { step, _services, CancellationToken.None })!;

    [Theory]
    [InlineData("new-event")]
    [InlineData("new-bulk-event")]
    [InlineData("record-added")]
    public async Task TriggerStep_NeedsViewOnItsSourceTable(string subtype)
    {
        Role(Restricted(view: RecordScopes.None));
        var step = new PipelineStep { Type = "trigger", Subtype = subtype, ConfigJson = $"{{\"tablePublicId\":\"{_table.PublicId}\"}}" };

        var act = () => Enforce(step);

        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*view records in table 'A1'*");
    }

    [Fact]
    public async Task TriggerStep_WhenOwnerCanView_Passes()
    {
        Role(Restricted());
        var step = new PipelineStep { Type = "trigger", Subtype = "new-event", ConfigJson = $"{{\"tablePublicId\":\"{_table.PublicId}\"}}" };

        await Enforce(step);
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("webhook")]
    [InlineData("pipeline-called")]
    public async Task OtherTriggers_AreNotTableChecked(string subtype)
    {
        var step = new PipelineStep { Type = "trigger", Subtype = subtype, ConfigJson = $"{{\"tablePublicId\":\"{_table.PublicId}\"}}" };

        await Enforce(step);

        await _access.DidNotReceiveWithAnyArgs().RequireMembershipByTablePublicIdAsync(default, default);
    }

    [Fact]
    public async Task BulkUpsert_ForRoleWithRecordRestrictions_FailsClosed()
    {
        Role(Restricted(ownOnly: 42, view: RecordScopes.OwnRecords));
        var step = new PipelineStep { Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = $"{{\"tableLabel\":\"{_table.PublicId}\"}}" };

        var act = () => Enforce(step);

        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*record-level restrictions*");
    }

    [Fact]
    public async Task BulkUpsert_ForRoleWithoutRestrictions_Passes()
    {
        Role(Restricted());
        var step = new PipelineStep { Type = "action", Subtype = "prepare-bulk-upsert", ConfigJson = $"{{\"tableLabel\":\"{_table.PublicId}\"}}" };

        await Enforce(step);
    }
}
