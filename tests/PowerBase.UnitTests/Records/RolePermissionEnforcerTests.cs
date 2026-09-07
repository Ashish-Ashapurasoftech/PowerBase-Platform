using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Records;

public class RolePermissionEnforcerTests
{
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppRolePermissionRepository _permRepo = Substitute.For<IAppRolePermissionRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    public RolePermissionEnforcerTests()
    {
        _queryContext.UserId.Returns(100L);
        _queryContext.IsSuperAdmin.Returns(false);
    }

    private RolePermissionEnforcer CreateSut() => new(_queryContext, _appUserRepo, _permRepo, _recordRepo);

    private static AppTable MakeTable(long id = 5, long appId = 10) => new() { Id = id, AppId = appId, PublicId = Guid.NewGuid() };

    private static AppField MakeField(long id, Guid publicId, int? fid = null) =>
        new() { Id = id, PublicId = publicId, Fid = fid ?? (int)id, Name = $"Field{id}" };

    private void StubRole(long appId, long roleId, AppTable table, AppRoleRecordFilter? filter = null)
    {
        _permRepo.GetTablePermissionAsync(roleId, table.Id, Arg.Any<CancellationToken>())
            .Returns(AppRoleTablePermission.Default(roleId, table.Id));
        _permRepo.GetFieldAccessMapAsync(roleId, table.Id, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());
        _permRepo.GetRecordFilterAsync(roleId, table.Id, Arg.Any<CancellationToken>())
            .Returns(filter);
    }

    private static AppRoleRecordFilter MakeFilter(long roleId, long tableId, string conjunction, params RoleRecordFilterCondition[] conditions) => new()
    {
        AppRoleId = roleId,
        AppTableId = tableId,
        Conjunction = conjunction,
        FilterJson = JsonSerializer.Serialize(conditions.ToList()),
    };

    // RP-064: a role with no stored filter (or IsSuperAdmin) leaves ViewFilter null — table is fully visible per scope.
    [Fact]
    public async Task GetTableAccessAsync_SuperAdmin_ReturnsUnrestrictedWithNullViewFilter()
    {
        _queryContext.IsSuperAdmin.Returns(true);
        var table = MakeTable();

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, Array.Empty<AppField>(), CancellationToken.None);

        result.Unrestricted.Should().BeTrue();
        result.ViewFilter.Should().BeNull();
        await _permRepo.DidNotReceive().GetRecordFilterAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTableAccessAsync_NoRoleAssignedInApp_ReturnsUnrestricted()
    {
        var table = MakeTable();
        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<long>());

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, Array.Empty<AppField>(), CancellationToken.None);

        result.Unrestricted.Should().BeTrue();
        result.ViewFilter.Should().BeNull();
    }

    [Fact]
    public async Task GetTableAccessAsync_RoleHasNoStoredFilter_ViewFilterIsNull()
    {
        var table = MakeTable();
        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });
        StubRole(table.AppId, 1, table, filter: null);

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, Array.Empty<AppField>(), CancellationToken.None);

        result.Unrestricted.Should().BeFalse();
        result.ViewFilter.Should().BeNull();
    }

    // RP-055 / RP-062: a role's stored condition (field + operator + value) becomes a FilterCondition in the returned ViewFilter.
    [Fact]
    public async Task GetTableAccessAsync_SingleRoleWithFieldCondition_BuildsMatchingFilterCondition()
    {
        var table = MakeTable();
        var fieldPublicId = Guid.NewGuid();
        var field = MakeField(id: 200, publicId: fieldPublicId, fid: 55);
        var fields = new[] { field };

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });

        var condition = new RoleRecordFilterCondition(fieldPublicId, "eq", "Acme Corp", UseCurrentUser: false);
        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, "AND", condition));

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, fields, CancellationToken.None);

        result.ViewFilter.Should().NotBeNull();
        result.ViewFilter!.Logic.Should().Be("and");
        result.ViewFilter.Nodes.Should().ContainSingle();
        var node = result.ViewFilter.Nodes[0];
        node.Group.Should().BeNull();
        node.Condition.Should().NotBeNull();
        node.Condition!.FieldId.Should().Be(55); // uses Fid, not internal Id
        node.Condition.Operator.Should().Be("eq");
        node.Condition.Value.Should().Be("Acme Corp");
    }

    // RP-061: UseCurrentUser=true substitutes the caller's UserPublicId regardless of the stored Value.
    [Fact]
    public async Task GetTableAccessAsync_ConditionUsesCurrentUser_SubstitutesCallerUserPublicId()
    {
        var table = MakeTable();
        var fieldPublicId = Guid.NewGuid();
        var field = MakeField(id: 1, publicId: fieldPublicId);
        var callerPublicId = Guid.NewGuid();

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = callerPublicId });

        var condition = new RoleRecordFilterCondition(fieldPublicId, "eq", "ignored-stale-value", UseCurrentUser: true);
        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, "AND", condition));

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { field }, CancellationToken.None);

        result.ViewFilter!.Nodes[0].Condition!.Value.Should().Be(callerPublicId.ToString());
    }

    [Fact]
    public async Task GetTableAccessAsync_ConditionUsesCurrentUserButAppUserMissing_SubstitutesEmptyString()
    {
        var table = MakeTable();
        var fieldPublicId = Guid.NewGuid();
        var field = MakeField(id: 1, publicId: fieldPublicId);

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns((AppUser?)null);

        var condition = new RoleRecordFilterCondition(fieldPublicId, "eq", "x", UseCurrentUser: true);
        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, "AND", condition));

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { field }, CancellationToken.None);

        result.ViewFilter!.Nodes[0].Condition!.Value.Should().Be(string.Empty);
    }

    // RP-063: the role's stored Conjunction drives the FilterGroup.Logic for that role's own conditions.
    [Theory]
    [InlineData("OR", "or")]
    [InlineData("or", "or")]
    [InlineData("AND", "and")]
    [InlineData("anything-else", "and")]
    public async Task GetTableAccessAsync_RoleConjunction_MapsToFilterGroupLogic(string storedConjunction, string expectedLogic)
    {
        var table = MakeTable();
        var fieldPublicId = Guid.NewGuid();
        var field = MakeField(id: 1, publicId: fieldPublicId);

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });

        var condition = new RoleRecordFilterCondition(fieldPublicId, "eq", "v", UseCurrentUser: false);
        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, storedConjunction, condition));

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { field }, CancellationToken.None);

        result.ViewFilter!.Logic.Should().Be(expectedLogic);
    }

    // RP-065: two roles each with their own filter combine via an outer OR — a record visible under either role's rule is visible.
    [Fact]
    public async Task GetTableAccessAsync_MultipleRolesEachWithFilter_CombinesWithOuterOrUnion()
    {
        var table = MakeTable();
        var field1PublicId = Guid.NewGuid();
        var field2PublicId = Guid.NewGuid();
        var field1 = MakeField(id: 1, publicId: field1PublicId);
        var field2 = MakeField(id: 2, publicId: field2PublicId);

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1, 2 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });

        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, "AND",
            new RoleRecordFilterCondition(field1PublicId, "eq", "RegionA", UseCurrentUser: false)));
        StubRole(table.AppId, 2, table, MakeFilter(2, table.Id, "AND",
            new RoleRecordFilterCondition(field2PublicId, "eq", "RegionB", UseCurrentUser: false)));

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { field1, field2 }, CancellationToken.None);

        result.ViewFilter!.Logic.Should().Be("or");
        result.ViewFilter.Nodes.Should().HaveCount(2);
        result.ViewFilter.Nodes.Should().OnlyContain(n => n.Group != null && n.Condition == null);

        var innerConditions = result.ViewFilter.Nodes
            .Select(n => n.Group!.Nodes.Single().Condition!.Value)
            .ToList();
        innerConditions.Should().BeEquivalentTo(new[] { "RegionA", "RegionB" });
    }

    // Only one of the user's roles actually contributes a filter — result should be that role's group directly,
    // not wrapped in an extra single-child "or" group.
    [Fact]
    public async Task GetTableAccessAsync_OnlyOneOfTwoRolesHasFilter_ReturnsThatGroupUnwrapped()
    {
        var table = MakeTable();
        var fieldPublicId = Guid.NewGuid();
        var field = MakeField(id: 1, publicId: fieldPublicId);

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1, 2 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });

        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, "AND",
            new RoleRecordFilterCondition(fieldPublicId, "eq", "OnlyValue", UseCurrentUser: false)));
        StubRole(table.AppId, 2, table, filter: null); // Administrator-like role with no filter defined

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { field }, CancellationToken.None);

        result.ViewFilter.Should().NotBeNull();
        result.ViewFilter!.Logic.Should().Be("and");
        result.ViewFilter.Nodes.Should().ContainSingle(n => n.Condition != null && n.Condition.Value == "OnlyValue");
    }

    // Malformed FilterJson must not throw — the role's filter is silently skipped (defense-in-depth against bad data).
    [Fact]
    public async Task GetTableAccessAsync_MalformedFilterJson_SkipsRoleFilterWithoutThrowing()
    {
        var table = MakeTable();
        var field = MakeField(id: 1, publicId: Guid.NewGuid());

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });

        var brokenFilter = new AppRoleRecordFilter { AppRoleId = 1, AppTableId = table.Id, Conjunction = "AND", FilterJson = "{not valid json" };
        StubRole(table.AppId, 1, table, brokenFilter);

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { field }, CancellationToken.None);

        result.ViewFilter.Should().BeNull();
    }

    // A condition referencing a field PublicId that isn't in the current table's fields is dropped silently, not errored.
    [Fact]
    public async Task GetTableAccessAsync_ConditionReferencesUnknownFieldPublicId_DropsConditionAndSkipsRoleIfEmpty()
    {
        var table = MakeTable();
        var knownField = MakeField(id: 1, publicId: Guid.NewGuid());
        var unknownFieldPublicId = Guid.NewGuid(); // not part of `fields` passed to GetTableAccessAsync

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });

        var condition = new RoleRecordFilterCondition(unknownFieldPublicId, "eq", "v", UseCurrentUser: false);
        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, "AND", condition));

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { knownField }, CancellationToken.None);

        result.ViewFilter.Should().BeNull(); // the role's only condition was dropped, so the role contributes nothing
    }

    // When a field has no Quickbase-style Fid assigned, the filter falls back to the field's internal Id.
    [Fact]
    public async Task GetTableAccessAsync_FieldWithoutFid_FallsBackToInternalId()
    {
        var table = MakeTable();
        var fieldPublicId = Guid.NewGuid();
        var field = new AppField { Id = 777, PublicId = fieldPublicId, Fid = null, Name = "NoFidField" };

        _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(table.AppId, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });

        var condition = new RoleRecordFilterCondition(fieldPublicId, "eq", "v", UseCurrentUser: false);
        StubRole(table.AppId, 1, table, MakeFilter(1, table.Id, "AND", condition));

        var sut = CreateSut();
        var result = await sut.GetTableAccessAsync(table, new[] { field }, CancellationToken.None);

        result.ViewFilter!.Nodes[0].Condition!.FieldId.Should().Be(777);
    }
}
