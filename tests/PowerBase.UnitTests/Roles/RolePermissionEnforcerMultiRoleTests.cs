using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Roles;

/// <summary>
/// How RolePermissionEnforcer combines the table permissions of a user who holds several roles:
/// View "None" is an explicit deny, otherwise the roles' grants add up (union), and rules that cannot
/// be used grant nothing.
/// </summary>
public class RolePermissionEnforcerMultiRoleTests
{
    private const long UserId = 77;
    private const long RoleA = 1;
    private const long RoleB = 2;
    private const int RegionFid = 10;
    private const int OwnerFid = 4;

    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppRolePermissionRepository _permRepo = Substitute.For<IAppRolePermissionRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly RolePermissionEnforcer _enforcer;

    private readonly AppTable _table = new() { Id = 5, AppId = 3 };
    private readonly IReadOnlyList<AppField> _fields =
    [
        new AppField { Id = 100, PublicId = Guid.NewGuid(), Fid = OwnerFid, TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy" },
        new AppField { Id = 101, PublicId = Guid.NewGuid(), Fid = RegionFid, TypeCode = "Text", PhysicalColumnName = "f_10" },
    ];

    public RolePermissionEnforcerMultiRoleTests()
    {
        _queryContext.UserId.Returns(UserId);
        _queryContext.IsSuperAdmin.Returns(false);
        _appUserRepo.GetUserAppRoleIdsAsync(_table.AppId, UserId, Arg.Any<CancellationToken>())
            .Returns(new List<long> { RoleA, RoleB });
        _enforcer = new RolePermissionEnforcer(_queryContext, _appUserRepo, _permRepo, _recordRepo, _userRepo);
    }

    // ── View ──

    [Fact]
    public async Task View_AllRecords_plus_CustomRules_grants_every_record()
    {
        Role(RoleA, RecordScopes.AllRecords);
        Role(RoleB, RecordScopes.AllRecords, viewRules: RegionIs("West"));

        var access = await Access();

        access.ViewScope.Should().Be(RecordScopes.AllRecords);
        access.ViewFilter.Should().BeNull();
    }

    [Fact]
    public async Task View_two_CustomRules_roles_are_ORed()
    {
        Role(RoleA, RecordScopes.AllRecords, viewRules: RegionIs("West"));
        Role(RoleB, RecordScopes.AllRecords, viewRules: RegionIs("East"));

        var access = await Access();

        access.ViewScope.Should().Be(RecordScopes.AllRecords);
        Values(access.ViewFilter).Should().BeEquivalentTo("West", "East");
        access.ViewFilter!.Logic.Should().Be("or");
    }

    [Fact]
    public async Task View_None_on_any_role_denies_the_table()
    {
        Role(RoleA, RecordScopes.AllRecords, canDelete: true, modify: RecordScopes.AllRecords);
        Role(RoleB, RecordScopes.None);

        var access = await Access();

        access.CanView.Should().BeFalse();
        access.ModifyScope.Should().Be(RecordScopes.None);
        access.CanDelete.Should().BeFalse();
    }

    [Fact]
    public async Task View_CustomRules_plus_OwnRecords_grants_rules_or_own_records()
    {
        Role(RoleA, RecordScopes.AllRecords, viewRules: RegionIs("West"));
        Role(RoleB, RecordScopes.OwnRecords);

        var access = await Access();

        access.ViewScope.Should().Be(RecordScopes.AllRecords);
        access.RestrictToCreatedBy.Should().BeNull();
        Values(access.ViewFilter).Should().BeEquivalentTo("West", UserId.ToString());
        Conditions(access.ViewFilter).Should().Contain(c => c.FieldId == OwnerFid && c.Operator == "eq");
    }

    [Fact]
    public async Task View_rules_left_on_an_OwnRecords_role_grant_nothing_extra()
    {
        Role(RoleA, RecordScopes.OwnRecords, viewRules: RegionIs("East"));
        Role(RoleB, RecordScopes.OwnRecords);

        var access = await Access();

        access.ViewScope.Should().Be(RecordScopes.OwnRecords);
        access.RestrictToCreatedBy.Should().Be(UserId);
        access.ViewFilter.Should().BeNull();
    }

    [Fact]
    public async Task View_unusable_rules_grant_nothing()
    {
        Role(RoleA, RecordScopes.AllRecords, viewRules: "{ not json");
        Role(RoleB, RecordScopes.AllRecords, viewRules: RegionIs("West"));

        var access = await Access();

        Values(access.ViewFilter).Should().BeEquivalentTo("West");
    }

    [Fact]
    public async Task View_only_unusable_rules_deny()
    {
        Role(RoleA, RecordScopes.AllRecords, viewRules: "{ not json");
        Role(RoleB, RecordScopes.AllRecords, viewRules: RuleOnField(999, "West")); // field deleted

        var access = await Access();

        access.CanView.Should().BeFalse();
    }

    [Fact]
    public async Task View_role_with_no_permission_row_counts_as_All_records()
    {
        Role(RoleA, RecordScopes.AllRecords, viewRules: RegionIs("West"));
        // RoleB has no row for the table: the default (View All records) applies

        var access = await Access();

        access.ViewFilter.Should().BeNull();
    }

    // ── Modify ──

    [Fact]
    public async Task Modify_OwnRecords_plus_CustomRules_grants_rules_or_own_records()
    {
        Role(RoleA, RecordScopes.AllRecords, modify: RecordScopes.OwnRecords);
        Role(RoleB, RecordScopes.AllRecords, modify: RecordScopes.AllRecords, modifyRules: RegionIs("West"));

        var access = await Access();

        access.ModifyScope.Should().Be(RecordScopes.AllRecords);
        Values(access.ModifyFilter).Should().BeEquivalentTo("West", UserId.ToString());
    }

    [Fact]
    public async Task Modify_None_is_not_a_deny()
    {
        Role(RoleA, RecordScopes.AllRecords, modify: RecordScopes.None);
        Role(RoleB, RecordScopes.AllRecords, modify: RecordScopes.AllRecords);

        var access = await Access();

        access.ModifyScope.Should().Be(RecordScopes.AllRecords);
        access.ModifyFilter.Should().BeNull();
    }

    [Fact]
    public async Task Modify_only_unusable_rules_plus_OwnRecords_falls_back_to_own_records()
    {
        Role(RoleA, RecordScopes.AllRecords, modify: RecordScopes.OwnRecords);
        Role(RoleB, RecordScopes.AllRecords, modify: RecordScopes.AllRecords, modifyRules: "{ not json");

        var access = await Access();

        access.ModifyScope.Should().Be(RecordScopes.OwnRecords);
        access.ModifyFilter.Should().BeNull();
    }

    // ── helpers ──

    private Task<TableAccessContext> Access() => _enforcer.GetTableAccessAsync(_table, _fields);

    private void Role(long roleId, string view, string modify = RecordScopes.None, bool canDelete = false,
        string? viewRules = null, string? modifyRules = null)
    {
        _permRepo.GetTablePermissionAsync(roleId, _table.Id, Arg.Any<CancellationToken>())
            .Returns(new AppRoleTablePermission
            {
                AppRoleId = roleId, AppTableId = _table.Id, ViewScope = view, ModifyScope = modify, CanDelete = canDelete,
            });
        if (viewRules is not null)
            _permRepo.GetRecordFilterAsync(roleId, _table.Id, Arg.Any<CancellationToken>())
                .Returns(new AppRoleRecordFilter { AppRoleId = roleId, AppTableId = _table.Id, FilterJson = viewRules });
        if (modifyRules is not null)
            _permRepo.GetModifyRecordFilterAsync(roleId, _table.Id, Arg.Any<CancellationToken>())
                .Returns(new AppRoleRecordFilter { AppRoleId = roleId, AppTableId = _table.Id, FilterJson = modifyRules, Kind = "Modify" });
    }

    private static string RegionIs(string value) => RuleOnField(RegionFid, value);

    private static string RuleOnField(long fieldId, string value) => RoleRecordFilterJson.SerializeGroup(new FilterGroup
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = "eq", Value = value } }],
    });

    private static IEnumerable<FilterCondition> Conditions(FilterGroup? group) =>
        group is null
            ? []
            : group.Nodes.SelectMany(n => n.Condition is not null ? [n.Condition] : Conditions(n.Group));

    private static IEnumerable<string?> Values(FilterGroup? group) => Conditions(group).Select(c => c.Value);
}
