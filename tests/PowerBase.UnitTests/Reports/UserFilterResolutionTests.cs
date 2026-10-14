using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Reports;

/// <summary>
/// A user field stores a user id. "contains" / "wildcard match" on one take a typed name or email, which the
/// report resolves to the matching users' ids before filtering; "is the current user" on a list of users means
/// the list includes them.
/// </summary>
public class UserFilterResolutionTests
{
    private const long AppId = 9;

    private static readonly Guid AnnPublicId = Guid.NewGuid();
    private static readonly Guid AnnaPublicId = Guid.NewGuid();
    private static readonly Guid BobPublicId = Guid.NewGuid();

    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();

    private readonly Dictionary<long, AppField> _fields = new()
    {
        [1] = new() { Id = 1, Fid = 1, Name = "Owner", TypeCode = "User" },
        [2] = new() { Id = 2, Fid = 2, Name = "Team", TypeCode = "MultiUser" },
        [3] = new() { Id = 3, Fid = 3, Name = "Lead", TypeCode = "Formula_User" },
        [4] = new() { Id = 4, Fid = 4, Name = "Members", TypeCode = "Formula_ListUser" },
    };

    public UserFilterResolutionTests()
    {
        _appUserRepo.ListByAppIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<AppUserDetail>
        {
            Detail(AnnPublicId, "Ann Smith", "ann@x.com"),
            Detail(AnnaPublicId, "Anna Jones", "anna@y.com"),
            Detail(BobPublicId, "Bob Stone", "bob@x.com"),
        });
        _userRepo.GetByPublicIdAsync(AnnPublicId, Arg.Any<CancellationToken>()).Returns(new User { Id = 11 });
        _userRepo.GetByPublicIdAsync(AnnaPublicId, Arg.Any<CancellationToken>()).Returns(new User { Id = 12 });
        _userRepo.GetByPublicIdAsync(BobPublicId, Arg.Any<CancellationToken>()).Returns(new User { Id = 13 });
    }

    private static AppUserDetail Detail(Guid userPublicId, string name, string email) =>
        new(Guid.NewGuid(), userPublicId, name, email, Guid.NewGuid(), "Member", "Active", true, DateTime.UtcNow, false, false, null);

    private async Task<FilterCondition> ResolveAsync(long fieldId, string op, string? value)
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = op, Value = value } }],
        };
        var resolved = await RunReportQueryHandler.ResolveUserFieldValuesAsync(
            tree, _fields, currentUserId: 7, new Dictionary<Guid, long>(), _userRepo, default, _appUserRepo, AppId);
        return resolved!.Nodes[0].Condition!;
    }

    [Fact]
    public async Task Contains_on_a_single_user_becomes_an_in_filter_on_the_matching_users_ids()
    {
        var cond = await ResolveAsync(1, "contains", "ann");   // Ann Smith, Anna Jones — by name

        cond.Operator.Should().Be("in");
        cond.Value.Should().Be("[\"11\",\"12\"]");
    }

    [Fact]
    public async Task Contains_also_matches_the_email()
    {
        var cond = await ResolveAsync(1, "contains", "@y.com");

        cond.Operator.Should().Be("in");
        cond.Value.Should().Be("[\"12\"]");
    }

    [Fact]
    public async Task Wildcard_match_is_matched_against_the_whole_name_or_email()
    {
        var cond = await ResolveAsync(1, "wildcard", "Ann*");

        cond.Operator.Should().Be("in");
        cond.Value.Should().Be("[\"11\",\"12\"]");                                     // Ann Smith, Anna Jones
        (await ResolveAsync(1, "wildcard", "*stone")).Value.Should().Be("[\"13\"]");   // Bob Stone
        (await ResolveAsync(1, "wildcard", "*@x.com")).Value.Should().Be("[\"11\",\"13\"]");   // by email
        (await ResolveAsync(1, "wildcard", "B?b Stone")).Value.Should().Be("[\"13\"]");        // ? = one character
        (await ResolveAsync(1, "wildcard", "Ann")).Value.Should().Be("[\"-1\"]");     // whole value, not a substring
    }

    [Fact]
    public async Task The_negative_operators_become_a_not_in_filter()
    {
        (await ResolveAsync(1, "notWildcard", "Ann*")).Should().BeEquivalentTo(new { Operator = "notIn", Value = "[\"11\",\"12\"]" });
        (await ResolveAsync(1, "notContains", "bob")).Should().BeEquivalentTo(new { Operator = "notIn", Value = "[\"13\"]" });
    }

    [Fact]
    public async Task No_matching_user_matches_nothing_and_its_negation_matches_everyone()
    {
        (await ResolveAsync(1, "contains", "zzz")).Should().BeEquivalentTo(new { Operator = "in", Value = "[\"-1\"]" });
        (await ResolveAsync(1, "notContains", "zzz")).Should().BeEquivalentTo(new { Operator = "notIn", Value = "[\"-1\"]" });
    }

    [Theory]
    [InlineData(2L)]   // MultiUser
    [InlineData(4L)]   // Formula_ListUser
    public async Task On_a_list_of_users_contains_means_the_list_includes_any_matching_user(long fieldId)
    {
        (await ResolveAsync(fieldId, "contains", "ann")).Should().BeEquivalentTo(new { Operator = "includes", Value = "11,12" });
        (await ResolveAsync(fieldId, "notWildcard", "Ann*")).Should().BeEquivalentTo(new { Operator = "notIncludes", Value = "11,12" });
        (await ResolveAsync(fieldId, "contains", "zzz")).Should().BeEquivalentTo(new { Operator = "includes", Value = "-1" });
    }

    [Fact]
    public async Task A_formula_user_is_resolved_like_a_stored_user()
    {
        (await ResolveAsync(3, "contains", "bob")).Should().BeEquivalentTo(new { Operator = "in", Value = "[\"13\"]" });
    }

    [Fact]
    public async Task Without_the_app_user_lookup_the_condition_is_left_as_typed()
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "contains", Value = "ann" } }],
        };

        var resolved = await RunReportQueryHandler.ResolveUserFieldValuesAsync(tree, _fields, 7, new Dictionary<Guid, long>(), _userRepo, default);

        resolved!.Nodes[0].Condition.Should().BeEquivalentTo(new { Operator = "contains", Value = "ann" });
    }

    [Fact]
    public async Task A_picked_user_is_still_converted_from_a_public_id_to_the_stored_id()
    {
        var cond = await ResolveAsync(1, "eq", AnnPublicId.ToString());

        cond.Operator.Should().Be("eq");
        cond.Value.Should().Be("11");
    }

    [Fact]
    public async Task Is_the_current_user_means_includes_on_any_list_of_users_and_equals_on_a_single_user()
    {
        (await ResolveAsync(1, "isCurrentUser", null)).Should().BeEquivalentTo(new { Operator = "eq", Value = "7" });
        (await ResolveAsync(3, "isCurrentUser", null)).Should().BeEquivalentTo(new { Operator = "eq", Value = "7" });
        (await ResolveAsync(2, "isCurrentUser", null)).Should().BeEquivalentTo(new { Operator = "includes", Value = "7" });
        (await ResolveAsync(4, "isCurrentUser", null)).Should().BeEquivalentTo(new { Operator = "includes", Value = "7" });
    }
}
