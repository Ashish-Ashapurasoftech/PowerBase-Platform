using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Records;

/// <summary>"is the current user" on a Formula User field: the formula may yield the user's id ([Record Owner]),
/// or the user's name / email as text (ToUser([Name])), so the rule matches any of the current user's identities.</summary>
public class FormulaUserCurrentUserRuleTests
{
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppRolePermissionRepository _permRepo = Substitute.For<IAppRolePermissionRepository>();
    private readonly AppTable _table = new() { Id = 5, AppId = 10, PublicId = Guid.NewGuid() };
    private readonly AppField _formulaUser = new() { Id = 7, Fid = 7, PublicId = Guid.NewGuid(), Name = "Formula User", TypeCode = "Formula_User" };

    public FormulaUserCurrentUserRuleTests()
    {
        _queryContext.UserId.Returns(100L);
        _queryContext.UserEmail.Returns("hasim@x.com");
        _queryContext.UserName.Returns("Hasim");
        _queryContext.IsSuperAdmin.Returns(false);
        _appUserRepo.GetUserAppRoleIdsAsync(_table.AppId, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new long[] { 1 });
        _appUserRepo.GetByAppAndUserAsync(_table.AppId, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new AppUser { UserPublicId = Guid.NewGuid() });
        _permRepo.GetTablePermissionAsync(1, _table.Id, Arg.Any<CancellationToken>()).Returns(AppRoleTablePermission.Default(1, _table.Id));
        _permRepo.GetFieldAccessMapAsync(1, _table.Id, Arg.Any<CancellationToken>()).Returns(new Dictionary<long, string>());
    }

    private async Task<FilterGroup?> ViewFilterAsync(string op)
    {
        var rule = new FilterGroup { Logic = "and", Nodes = [new() { Condition = new() { FieldId = 7, Operator = op } }] };
        _permRepo.GetRecordFilterAsync(1, _table.Id, Arg.Any<CancellationToken>())
            .Returns(new AppRoleRecordFilter { AppRoleId = 1, AppTableId = _table.Id, FilterJson = RoleRecordFilterJson.SerializeGroup(rule) });
        var sut = new RolePermissionEnforcer(_queryContext, _appUserRepo, _permRepo, Substitute.For<IRecordRepository>(), Substitute.For<IUserRepository>());
        return (await sut.GetTableAccessAsync(_table, [_formulaUser], CancellationToken.None)).ViewFilter;
    }

    [Fact]
    public async Task IsCurrentUser_OnFormulaUser_MatchesIdEmailOrName()
    {
        var filter = await ViewFilterAsync("isCurrentUser");

        var group = filter!.Nodes.Should().ContainSingle().Which.Group!;
        group.Logic.Should().Be("or");
        group.Nodes.Select(n => (n.Condition!.Operator, n.Condition.Value)).Should().BeEquivalentTo(new[]
        {
            ("eq", "100"), ("eq", "hasim@x.com"), ("eq", "Hasim"),
        });
    }

    [Fact]
    public async Task NameShared_WithAnotherUserInTheApp_IsNotTrusted_OnlyIdAndEmailMatch()
    {
        AppUserDetail U(string name, string email) => new(Guid.NewGuid(), Guid.NewGuid(), name, email, Guid.NewGuid(), "Participant", "Active", true, DateTime.UtcNow, false, false, null);
        _appUserRepo.ListByAppIdAsync(_table.AppId, Arg.Any<CancellationToken>())
            .Returns(new[] { U("Hasim", "hasim@x.com"), U("Hasim", "other@x.com") });

        var filter = await ViewFilterAsync("isCurrentUser");

        var values = filter!.Nodes.Single().Group!.Nodes.Select(n => n.Condition!.Value).ToList();
        values.Should().BeEquivalentTo(new[] { "100", "hasim@x.com" });
    }

    [Fact]
    public async Task NameUniqueInTheApp_IsMatched()
    {
        AppUserDetail U(string name, string email) => new(Guid.NewGuid(), Guid.NewGuid(), name, email, Guid.NewGuid(), "Participant", "Active", true, DateTime.UtcNow, false, false, null);
        _appUserRepo.ListByAppIdAsync(_table.AppId, Arg.Any<CancellationToken>())
            .Returns(new[] { U("Hasim", "hasim@x.com"), U("Mahesh", "m@x.com") });

        var filter = await ViewFilterAsync("isCurrentUser");

        filter!.Nodes.Single().Group!.Nodes.Select(n => n.Condition!.Value).Should().Contain("Hasim");
    }

    [Theory]
    [InlineData("4", "100", true)]        // [Record Owner]: the user's id
    [InlineData("hasim", "100", true)]    // ToUser([Name]) holding the user's name (case-insensitive)
    [InlineData("hasim@x.com", "100", true)]
    [InlineData("someone else", "100", false)]
    public async Task FormulaValue_MatchesCurrentUser_WhenItIsTheirIdEmailOrName(string computed, string _, bool expected)
    {
        _queryContext.UserId.Returns(computed == "4" ? 4L : 100L);
        var filter = await ViewFilterAsync("isCurrentUser");
        var row = new Dictionary<string, object?>();
        var pairs = new[] { ((IReadOnlyDictionary<string, object?>)row, (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [7] = computed }) };

        FormulaFilterSorter.ApplyFormulaFilters(pairs, filter!, [_formulaUser]).Should().HaveCount(expected ? 1 : 0);
    }
}
