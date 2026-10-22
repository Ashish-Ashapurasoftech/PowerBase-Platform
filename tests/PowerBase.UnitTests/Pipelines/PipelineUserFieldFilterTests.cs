using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Filters on a detail of the person a User / User List field holds — "Record Owner › Email is x" — as Quickbase's filter field list
/// offers them (Id, First name, Last name, Email, Screen name). A rule names it as <c>fid_N.detail</c>.
/// </summary>
public class PipelineUserFieldFilterTests
{
    private static readonly List<AppField> Fields = new()
    {
        new() { Id = 1, Fid = 6, Name = "Name", TypeCode = "Text" },
        new() { Id = 2, Fid = 4, Name = "CreatedBy", Label = "Record Owner", TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy" },
        new() { Id = 3, Fid = 5, Name = "ModifiedBy", Label = "Last Modified By", TypeCode = "User", IsSystem = true, PhysicalColumnName = "ModifiedBy" },
        new() { Id = 4, Fid = 9, Name = "Team", Label = "List Of Users", TypeCode = "MultiUser" },
        new() { Id = 5, Fid = 10, Name = "Owner Formula", TypeCode = "Formula_User" },
    };

    private static readonly Dictionary<long, PipelineUserDetails> People = new()
    {
        [40021] = new(40021, "Dipak", "Patel", "dipak@example.com", "dipak.p", "Dipak Patel"),
        [40022] = new(40022, "Ronak", "Dhamsaniya", "ronak@example.com", "ronak.d", "Ronak Dhamsaniya"),
        [40023] = new(40023, "Asha", "", "", "asha", "Asha"),
    };

    // ── resolving a rule field ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("fid_4.email", 4, "email")]
    [InlineData("FID_4.Screen_Name", 4, "screen_name")]
    [InlineData("fid_5.first_name", 5, "first_name")]
    [InlineData("fid_9.id", 9, "id")]
    [InlineData("Record Owner.last_name", 4, "last_name")]
    [InlineData("CreatedBy.email", 4, "email")]
    [InlineData("fid_4.name", 4, "name")]
    public void TryResolve_FindsTheUserFieldAndTheDetail(string fieldRef, int fid, string detail)
    {
        Assert.True(PipelineUserFieldFilter.TryResolve(fieldRef, Fields, out var field, out var resolvedDetail));
        Assert.Equal(fid, field.Fid);
        Assert.Equal(detail, resolvedDetail);
    }

    [Theory]
    [InlineData("fid_4")]
    [InlineData("fid_6.email")]
    [InlineData("fid_10.email")]
    [InlineData("fid_4.phone")]
    [InlineData("fid_99.email")]
    [InlineData("fid_4.")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_IgnoresAnythingElse(string? fieldRef)
    {
        Assert.False(PipelineUserFieldFilter.TryResolve(fieldRef, Fields, out _, out _));
    }

    [Theory]
    [InlineData("40021", new long[] { 40021 })]
    [InlineData("40021,40022", new long[] { 40021, 40022 })]
    [InlineData("[40021, 40022, 40021]", new long[] { 40021, 40022 })]
    [InlineData("[\"40021\",\"40022\"]", new long[] { 40021, 40022 })]
    [InlineData("", new long[0])]
    [InlineData("Dipak", new long[0])]
    public void ParseUserIds_ReadsEveryStoredShape(string raw, long[] expected)
    {
        Assert.Equal(expected, PipelineUserFieldFilter.ParseUserIds(raw));
    }

    // ── one person (User field) ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("email", "is", "DIPAK@example.com", true)]
    [InlineData("email", "is-not", "dipak@example.com", false)]
    [InlineData("first_name", "starts-with", "Dip", true)]
    [InlineData("last_name", "ends-with", "tel", true)]
    [InlineData("screen_name", "contains", "k.p", true)]
    [InlineData("screen_name", "not-contains", "k.p", false)]
    [InlineData("id", "is", "40021", true)]
    [InlineData("email", "is-empty", "", false)]
    [InlineData("email", "is-not-empty", "", true)]
    public void Matches_OnePerson(string detail, string op, string value, bool expected)
    {
        Assert.Equal(expected, PipelineUserFieldFilter.Matches(new long[] { 40021 }, People, detail, op, value));
    }

    [Theory]
    [InlineData("is", "x@example.com", false)]
    [InlineData("is-not", "x@example.com", true)]
    [InlineData("is-empty", "", true)]
    [InlineData("is-not-empty", "", false)]
    public void Matches_NoUser_ReadsAsEmpty(string op, string value, bool expected)
    {
        Assert.Equal(expected, PipelineUserFieldFilter.Matches(Array.Empty<long>(), People, "email", op, value));
    }

    [Fact]
    public void Matches_PersonNotLoaded_KnowsOnlyTheId()
    {
        Assert.True(PipelineUserFieldFilter.Matches(new long[] { 40021 }, null, "id", "is", "40021"));
        Assert.False(PipelineUserFieldFilter.Matches(new long[] { 40021 }, null, "email", "is", "dipak@example.com"));
    }

    // ── several people (User List) ────────────────────────────────────────────────────

    [Theory]
    [InlineData("is", "ronak@example.com", true)]           // any of them
    [InlineData("is", "nobody@example.com", false)]
    [InlineData("is-not", "ronak@example.com", false)]      // none of them
    [InlineData("is-not", "nobody@example.com", true)]
    [InlineData("contains", "example", true)]
    [InlineData("not-contains", "dipak", false)]
    [InlineData("is-empty", "", false)]                     // Dipak and Ronak have one, though Asha has none
    [InlineData("is-not-empty", "", true)]
    public void Matches_UserList_AnyForPositive_NoneForNegated(string op, string value, bool expected)
    {
        Assert.Equal(expected, PipelineUserFieldFilter.Matches(new long[] { 40021, 40022, 40023 }, People, "email", op, value));
    }

    [Fact]
    public void Matches_UserList_IsEmpty_OnlyWhenNoOneHasTheDetail()
    {
        Assert.False(PipelineUserFieldFilter.Matches(new long[] { 40021, 40022 }, People, "email", "is-empty", ""));
        Assert.True(PipelineUserFieldFilter.Matches(new long[] { 40023 }, People, "email", "is-empty", ""));
    }

    // ── Search Records conditions ─────────────────────────────────────────────────────

    private static readonly long[] Candidates = { 40021, 40022, 40023 };

    private static string Describe(FilterNode? node) => node == null ? "ALL"
        : node.Condition != null ? $"{node.Condition.Operator}({node.Condition.FieldId}{(node.Condition.Value == null ? "" : "," + node.Condition.Value)})"
        : $"{node.Group!.Logic}[{string.Join(" ", node.Group.Nodes.Select(Describe))}]";

    [Theory]
    [InlineData(4, "email", "is", "dipak@example.com", "in(4,[\"40021\"])")]
    [InlineData(4, "email", "contains", "example", "and[isNotEmpty(4) notIn(4,[\"40023\"])]")]
    [InlineData(4, "email", "is", "nobody@example.com", "eq(4,-1)")]
    [InlineData(4, "email", "is-not", "dipak@example.com", "or[and[isNotEmpty(4) notIn(4,[\"40021\"])] isEmpty(4)]")]
    [InlineData(4, "email", "is-not", "nobody@example.com", "ALL")]
    [InlineData(4, "email", "is-empty", "", "or[in(4,[\"40023\"]) isEmpty(4)]")]
    [InlineData(4, "email", "is-not-empty", "", "and[isNotEmpty(4) notIn(4,[\"40023\"])]")]
    [InlineData(4, "id", "is", "40022", "in(4,[\"40022\"])")]
    [InlineData(9, "screen_name", "is", "ronak.d", "includes(9,40022)")]
    [InlineData(9, "screen_name", "is-not", "ronak.d", "or[notIncludes(9,40022) isEmpty(9)]")]
    [InlineData(9, "first_name", "starts-with", "", "or[includes(9,40021) includes(9,40022) includes(9,40023) isEmpty(9)]")]
    public void BuildDbNode_TurnsTheRuleIntoConditionsOnTheUserIds(int fid, string detail, string op, string value, string expected)
    {
        var field = Fields.Single(f => f.Fid == fid);
        Assert.Equal(expected, Describe(PipelineUserFieldFilter.BuildDbNode(field, detail, op, value, Candidates, People)));
    }

    [Fact]
    public void BuildDbNode_NegatedRuleThatAnEmptyFieldFails_KeepsOnlyRecordsWithAUser()
    {
        var field = Fields.Single(f => f.Fid == 4);
        // "is not ''": a record without a user has an empty email, so it fails.
        Assert.Equal("and[isNotEmpty(4) notIn(4,[\"40023\"])]", Describe(PipelineUserFieldFilter.BuildDbNode(field, "email", "is-not", "", Candidates, People)));
    }

    [Fact]
    public void DetailRules_FindsRulesInNestedGroups()
    {
        var nested = new TriggerFilterRule { Field = "fid_9.email", Operator = "is", Value = "x" };
        var groups = new List<TriggerFilterGroup>
        {
            new() { Rules = new() { new() { Field = "fid_6", Operator = "is", Value = "a" }, new() { Field = "fid_4.email", Operator = "is", Value = "b" } } },
            new() { Rules = new() { new() { Type = "nested", Groups = new() { new() { Rules = new() { nested } } } } } }
        };

        var found = PipelineUserFieldFilter.DetailRules(groups, Fields).ToList();

        Assert.Equal(new[] { "fid_4.email", "fid_9.email" }, found.Select(r => r.Field));
        Assert.Same(nested, found[1]);
    }

    [Fact]
    public void Details_FromAUser_UseTheScreenNameThenTheNameThenTheEmail()
    {
        Assert.Equal("dipak.p", PipelineUserDetails.From(new User { Id = 1, Name = "dipak.p", FirstName = "Dipak", LastName = "Patel", Email = "d@x.com" }).ScreenName);
        Assert.Equal("Dipak Patel", PipelineUserDetails.From(new User { Id = 1, Name = " ", FirstName = "Dipak", LastName = "Patel", Email = "d@x.com" }).ScreenName);
        Assert.Equal("d@x.com", PipelineUserDetails.From(new User { Id = 1, Name = "", Email = "d@x.com" }).ScreenName);
    }

    // ── trigger filters (On New Event) ────────────────────────────────────────────────

    private static TriggerFilterGroup Group(params (string Field, string Op, string Value)[] rules) =>
        new() { LogicalOp = "AND", Rules = rules.Select(r => new TriggerFilterRule { Field = r.Field, Operator = r.Op, Value = r.Value }).ToList() };

    [Fact]
    public void EvaluateGroup_FiltersOnARecordOwnersDetail()
    {
        var values = new Dictionary<long, object?> { [4] = 40021L, [6] = "Lead" };

        Assert.True(PipelineFilterEvaluator.EvaluateGroup(Group(("fid_4.email", "is", "dipak@example.com"), ("fid_6", "is", "Lead")), values, Fields, null, People));
        Assert.False(PipelineFilterEvaluator.EvaluateGroup(Group(("fid_4.email", "is", "ronak@example.com")), values, Fields, null, People));
        Assert.True(PipelineFilterEvaluator.EvaluateGroup(Group(("fid_4.screen_name", "starts-with", "dipak")), values, Fields, null, People));
    }

    [Fact]
    public void EvaluateGroup_FiltersOnAUserListDetail()
    {
        var values = new Dictionary<long, object?> { [9] = "[40021,40022]" };

        Assert.True(PipelineFilterEvaluator.EvaluateGroup(Group(("fid_9.first_name", "is", "Ronak")), values, Fields, null, People));
        Assert.False(PipelineFilterEvaluator.EvaluateGroup(Group(("fid_9.first_name", "is-not", "Ronak")), values, Fields, null, People));
    }

    [Fact]
    public void EvaluateGroup_PlainUserFieldRules_AreUnchanged()
    {
        var values = new Dictionary<long, object?> { [4] = 40021L };

        Assert.True(PipelineFilterEvaluator.EvaluateGroup(Group(("fid_4", "is", "40021")), values, Fields));
        Assert.False(PipelineFilterEvaluator.EvaluateGroup(Group(("fid_4", "is", "40022")), values, Fields));
    }

    [Fact]
    public void ValidateGroup_AcceptsUserDetailRules_AndChecksTheirOperator()
    {
        var errors = new Dictionary<string, List<string>>();
        PipelineFilterEvaluator.ValidateGroup(Group(("fid_4.email", "is", "x"), ("fid_9.screen_name", "not-contains", "y")), Fields, errors, "FilterGroups[0]");
        Assert.Empty(errors);

        PipelineFilterEvaluator.ValidateGroup(Group(("fid_5.email", "greater_than", "x")), Fields, errors, "FilterGroups[1]");
        var error = Assert.Single(errors.Values.SelectMany(v => v));
        Assert.Contains("Last Modified By", error);
        Assert.Contains("greater_than", error);

        errors.Clear();
        PipelineFilterEvaluator.ValidateGroup(Group(("fid_4.phone", "is", "x")), Fields, errors, "FilterGroups[2]");
        Assert.Contains("does not exist", Assert.Single(errors.Values.SelectMany(v => v)));
    }

    // ── end to end: an On New Event trigger filtering on Record Owner's email ────────

    [Theory]
    [InlineData("dipak@example.com", 1)]
    [InlineData("ronak@example.com", 0)]
    public async Task OnNewEvent_WithARecordOwnerEmailFilter_FiresOnlyForThatOwner(string email, int expectedRuns)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var context = Substitute.For<IQueryContext>();
        context.IsPipelineExecution.Returns(true);
        context.PipelineDepth.Returns(1);
        context.PipelineChainJson.Returns("[101]");
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(40021, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 40021, Name = "dipak.p", FirstName = "Dipak", LastName = "Patel", Email = "dipak@example.com" });
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        repo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { new() { Id = 101, IsActive = true } });
        repo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new() { Type = "trigger", Subtype = "new-event", ConfigJson = JsonSerializer.Serialize(new
            {
                TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, IsSimpleFilter = true,
                FilterGroups = new[] { new { LogicalOp = "AND", Rules = new[] { new { Field = "fid_4.email", Operator = "is", Value = email } } } }
            }) }
        });
        var items = new List<PipelineOutboxItem>();
        repo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(items.Add), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        var interceptor = new PipelineTriggerInterceptor(repo, Substitute.For<IRecordRepository>(), context, uow, null!, null!,
            Substitute.For<ILogger<PipelineTriggerInterceptor>>(), userRepo: users);

        await interceptor.InterceptAsync(table, Fields, Guid.NewGuid(), new Dictionary<long, object?> { [4] = 40021L, [6] = "Lead" }, "record-added", CancellationToken.None);

        Assert.Equal(expectedRuns, items.Count);
    }
}
