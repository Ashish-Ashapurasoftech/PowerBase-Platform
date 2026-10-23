using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Records;

/// <summary>Modify-scope Custom Rules: which records a role may edit / delete (TableAccessContext.ModifyFilter).</summary>
public class ModifyCustomRulesTests
{
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppRolePermissionRepository _permRepo = Substitute.For<IAppRolePermissionRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly AppTable _table = new() { Id = 5, AppId = 10, PublicId = Guid.NewGuid() };
    private readonly AppField _region = new() { Id = 1, PublicId = Guid.NewGuid(), Fid = 10, Name = "Region" };

    public ModifyCustomRulesTests()
    {
        _queryContext.UserId.Returns(100L);
        _queryContext.IsSuperAdmin.Returns(false);
        _appUserRepo.GetByAppAndUserAsync(_table.AppId, 100L, Arg.Any<CancellationToken>())
            .Returns(new AppUser { UserPublicId = Guid.NewGuid() });
    }

    private void StubRole(long roleId, string modifyScope, FilterGroup? modifyRule, string? rawModifyJson = null)
    {
        var perm = AppRoleTablePermission.Default(roleId, _table.Id);
        perm.ViewScope = RecordScopes.AllRecords;
        perm.ModifyScope = modifyScope;
        _permRepo.GetTablePermissionAsync(roleId, _table.Id, Arg.Any<CancellationToken>()).Returns(perm);
        _permRepo.GetFieldAccessMapAsync(roleId, _table.Id, Arg.Any<CancellationToken>()).Returns(new Dictionary<long, string>());
        _permRepo.GetRecordFilterAsync(roleId, _table.Id, Arg.Any<CancellationToken>()).Returns((AppRoleRecordFilter?)null);
        _permRepo.GetModifyRecordFilterAsync(roleId, _table.Id, Arg.Any<CancellationToken>()).Returns(
            modifyRule is null && rawModifyJson is null
                ? null
                : new AppRoleRecordFilter
                {
                    AppRoleId = roleId, AppTableId = _table.Id, Kind = "Modify",
                    FilterJson = rawModifyJson ?? RoleRecordFilterJson.SerializeGroup(modifyRule!),
                });
    }

    private static FilterGroup RegionIs(string value, int fid = 10) => new()
    {
        Logic = "and",
        Nodes = [new() { Condition = new() { FieldId = fid, Operator = "eq", Value = value } }],
    };

    private async Task<TableAccessContext> RunAsync(params long[] roleIds)
    {
        _appUserRepo.GetUserAppRoleIdsAsync(_table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(roleIds);
        var sut = new RolePermissionEnforcer(_queryContext, _appUserRepo, _permRepo, _recordRepo, _userRepo);
        return await sut.GetTableAccessAsync(_table, [_region], CancellationToken.None);
    }

    [Fact]
    public async Task ModifyAllRecordsWithRule_SetsModifyFilter()
    {
        StubRole(1, RecordScopes.AllRecords, RegionIs("West"));

        var access = await RunAsync(1);

        access.ModifyScope.Should().Be(RecordScopes.AllRecords);
        access.ModifyFilter.Should().NotBeNull();
        access.ModifyFilter!.Nodes.Should().ContainSingle().Which.Condition!.Value.Should().Be("West");
    }

    [Fact]
    public async Task ModifyAllRecordsWithoutRule_LeavesModifyFilterNull()
    {
        StubRole(1, RecordScopes.AllRecords, null);

        var access = await RunAsync(1);

        access.ModifyScope.Should().Be(RecordScopes.AllRecords);
        access.ModifyFilter.Should().BeNull();
    }

    [Fact]
    public async Task ModifyOwnRecords_IgnoresModifyRules()
    {
        StubRole(1, RecordScopes.OwnRecords, RegionIs("West"));

        var access = await RunAsync(1);

        access.ModifyScope.Should().Be(RecordScopes.OwnRecords);
        access.ModifyFilter.Should().BeNull();
    }

    [Fact]
    public async Task TwoRolesWithRules_AreCombinedWithOr()
    {
        StubRole(1, RecordScopes.AllRecords, RegionIs("North"));
        StubRole(2, RecordScopes.AllRecords, RegionIs("West"));

        var access = await RunAsync(1, 2);

        access.ModifyFilter.Should().NotBeNull();
        access.ModifyFilter!.Logic.Should().Be("or");
        access.ModifyFilter.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public async Task OneRoleWithoutRule_MakesModifyUnrestricted()
    {
        StubRole(1, RecordScopes.AllRecords, RegionIs("West"));
        StubRole(2, RecordScopes.AllRecords, null);

        var access = await RunAsync(1, 2);

        access.ModifyScope.Should().Be(RecordScopes.AllRecords);
        access.ModifyFilter.Should().BeNull();
    }

    [Fact]
    public async Task RoleWithNoModifyScope_DoesNotWidenAnotherRolesRule()
    {
        StubRole(1, RecordScopes.AllRecords, RegionIs("West"));
        StubRole(2, RecordScopes.None, null);

        var access = await RunAsync(1, 2);

        access.ModifyFilter.Should().NotBeNull();
        access.ModifyFilter!.Nodes.Should().ContainSingle().Which.Condition!.Value.Should().Be("West");
    }

    [Fact]
    public async Task RuleOnDeletedField_FailsClosed_UserCannotModify()
    {
        StubRole(1, RecordScopes.AllRecords, RegionIs("West", fid: 999));

        var access = await RunAsync(1);

        access.ModifyScope.Should().Be(RecordScopes.None);
        access.ModifyFilter.Should().BeNull();
    }

    [Fact]
    public async Task MalformedRule_FailsClosed_UserCannotModify()
    {
        StubRole(1, RecordScopes.AllRecords, null, rawModifyJson: "{ not json");

        var access = await RunAsync(1);

        access.ModifyScope.Should().Be(RecordScopes.None);
    }

    [Fact]
    public async Task UnusableRuleOfOneRole_DoesNotBlockAnotherRolesValidRule()
    {
        StubRole(1, RecordScopes.AllRecords, RegionIs("West", fid: 999));
        StubRole(2, RecordScopes.AllRecords, RegionIs("North"));

        var access = await RunAsync(1, 2);

        access.ModifyScope.Should().Be(RecordScopes.AllRecords);
        access.ModifyFilter!.Nodes.Should().ContainSingle().Which.Condition!.Value.Should().Be("North");
    }
}
