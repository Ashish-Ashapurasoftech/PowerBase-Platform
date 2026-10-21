using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The report filter's operators a PowerFlow filter now offers too ("Show records where"): does not start / end with, wildcard match,
/// includes, is during, is the current user, and one part of an address. Each is checked where a filter runs: a trigger's filter and
/// the Condition step (in memory), Search Records (mapped to SQL), and the streamed in-memory search.
/// </summary>
public class PipelineFilterReportOperatorsTests
{
    private const long Dipak = 40021, Ronak = 40022;

    private static readonly List<AppField> Fields = new()
    {
        new() { Id = 101, Fid = 10, Name = "Title", TypeCode = "Text" },
        new() { Id = 102, Fid = 14, Name = "Colors", TypeCode = "MultiSelect", Settings = "{\"options\":[\"Red\",\"Blue\",\"Red, dark\"]}" },
        new() { Id = 103, Fid = 19, Name = "Due", TypeCode = "Date" },
        new() { Id = 104, Fid = 20, Name = "Stamp", TypeCode = "DateTime" },
        new() { Id = 105, Fid = 29, Name = "Attachment", TypeCode = "File" },
        new() { Id = 106, Fid = 30, Name = "Address", Label = "Site", TypeCode = "Address" },
        new() { Id = 107, Fid = 4, Name = "CreatedBy", Label = "Record Owner", TypeCode = "User", IsSystem = true },
        new() { Id = 108, Fid = 32, Name = "Team", Label = "User list", TypeCode = "MultiUser" },
        new() { Id = 109, Fid = 15, Name = "Amount", TypeCode = "Number" },
    };

    private static readonly Dictionary<long, PipelineUserDetails> People = new()
    {
        [Dipak] = new(Dipak, "Dipak", "Patel", "dipak@example.com", "dipak.p", "Dipak Patel"),
        [Ronak] = new(Ronak, "Ronak", "Dhamsaniya", "ronak@example.com", "ronak.d", "Ronak Dhamsaniya"),
    };

    private const string PuneAddress = "{\"street1\":\"12 MG Road\",\"street2\":\"\",\"city\":\"Pune\",\"state\":\"MH\",\"zip\":\"411001\",\"country\":\"India\"}";

    private static TriggerFilterGroup Group(string field, string op, string? value = null) =>
        new() { LogicalOp = "AND", Rules = new() { new TriggerFilterRule { Field = field, Operator = op, Value = value } } };

    private static bool Trigger(string field, string op, string? value, Dictionary<long, object?> values, long? currentUser = null) =>
        PipelineFilterEvaluator.EvaluateGroup(Group(field, op, value), values, Fields, null, People, currentUser);

    // ── "is during": the period window ──────────────────────────────────────────────────────────────────────────

    [Theory]
    // Wednesday 2026-10-14 10:00 UTC; weeks start on Monday, as the report filter's do.
    [InlineData("current|1|day", "2026-10-14", "2026-10-15")]
    [InlineData("current|1|week", "2026-10-12", "2026-10-19")]
    [InlineData("previous|1|week", "2026-10-05", "2026-10-12")]
    [InlineData("previous|2|week", "2026-09-28", "2026-10-12")]
    [InlineData("next|1|week", "2026-10-19", "2026-10-26")]
    [InlineData("current|1|month", "2026-10-01", "2026-11-01")]
    [InlineData("previous|2|month", "2026-08-01", "2026-10-01")]
    [InlineData("next|1|quarter", "2027-01-01", "2027-04-01")]
    [InlineData("current|1|quarter", "2026-10-01", "2027-01-01")]
    [InlineData("previous|1|year", "2025-01-01", "2026-01-01")]
    public void PeriodWindow_FollowsTheReportFilter(string period, string start, string end)
    {
        var window = RelativeFilterDate.PeriodWindow($"@relative-period|{period}|UTC", DateTimeOffset.Parse("2026-10-14T10:00:00Z"));
        Assert.Equal(DateTime.Parse(start + "T00:00:00Z").ToUniversalTime(), window.Start);
        Assert.Equal(DateTime.Parse(end + "T00:00:00Z").ToUniversalTime(), window.End);
    }

    [Fact]
    public void PeriodWindow_IsWorkedOutInTheSavedTimezone()
    {
        // 20:00 UTC on Sunday 2026-10-18 is already Monday 2026-10-19 01:30 in India: "the current week" is the new week.
        var window = RelativeFilterDate.PeriodWindow("@relative-period|current|1|week|Asia/Kolkata", DateTimeOffset.Parse("2026-10-18T20:00:00Z"));
        Assert.Equal(DateTime.Parse("2026-10-18T18:30:00Z").ToUniversalTime(), window.Start);
        Assert.Equal(DateTime.Parse("2026-10-25T18:30:00Z").ToUniversalTime(), window.End);
    }

    [Fact]
    public void PeriodWindow_SundayBelongsToTheWeekStartedOnMonday()
    {
        var window = RelativeFilterDate.PeriodWindow("@relative-period|current|1|week|UTC", DateTimeOffset.Parse("2026-10-18T12:00:00Z"));
        Assert.Equal(DateTime.Parse("2026-10-12T00:00:00Z").ToUniversalTime(), window.Start);
    }

    [Theory]
    [InlineData("@relative-period|current|1|week")]
    [InlineData("@relative-period|sideways|1|week|UTC")]
    [InlineData("@relative-period|previous|0|week|UTC")]
    [InlineData("@relative-period|previous|-2|week|UTC")]
    [InlineData("@relative-period|previous|1|fortnight|UTC")]
    public void PeriodWindow_RejectsMalformedValues(string value) =>
        Assert.Throws<ArgumentException>(() => RelativeFilterDate.PeriodWindow(value));

    [Fact]
    public void PeriodValue_IsNotARelativeDate_AndTheReverse()
    {
        Assert.True(RelativeFilterDate.IsPeriod("@relative-period|current|1|week|UTC"));
        Assert.False(RelativeFilterDate.IsRelative("@relative-period|current|1|week|UTC"));
        Assert.False(RelativeFilterDate.IsPeriod("@relative-date|today|0|UTC"));
    }

    // ── "is during" in memory (triggers, Condition step) ────────────────────────────────────────────────────────

    [Fact]
    public void During_MatchesADateInsideThePeriod_AndNotDuringTheOthers()
    {
        var today = DateTime.UtcNow.ToString("O");
        var longAgo = DateTime.UtcNow.AddDays(-40).ToString("O");
        const string thisDay = "@relative-period|current|1|day|UTC";

        Assert.True(PipelineFilterEvaluator.EvaluateConditionOperator(today, "during", thisDay, "DATE"));
        Assert.False(PipelineFilterEvaluator.EvaluateConditionOperator(today, "not-during", thisDay, "DATE"));
        Assert.False(PipelineFilterEvaluator.EvaluateConditionOperator(longAgo, "during", thisDay, "DATE"));
        Assert.True(PipelineFilterEvaluator.EvaluateConditionOperator(longAgo, "not-during", thisDay, "DATE"));
        // The Condition step does not always know the type: a period value still compares as a date.
        Assert.True(PipelineFilterEvaluator.EvaluateConditionOperator(today, "during", thisDay, "INFER"));
        Assert.True(PipelineFilterEvaluator.EvaluateConditionOperator(today, "during", thisDay));
    }

    [Theory]
    [InlineData("during")]
    [InlineData("not-during")]
    [InlineData("not_during")]
    public void During_ABlankDateIsInNoPeriod_AndAPlainDateIsNoPeriod(string op)
    {
        Assert.False(PipelineFilterEvaluator.EvaluateConditionOperator("", op, "@relative-period|current|1|week|UTC", "DATE"));
        Assert.False(PipelineFilterEvaluator.EvaluateConditionOperator(DateTime.UtcNow.ToString("O"), op, "2026-10-14", "DATE"));
    }

    [Fact]
    public void During_OnATriggerRule()
    {
        var values = new Dictionary<long, object?> { [19] = DateTime.UtcNow.ToString("O") };
        Assert.True(Trigger("fid_19", "during", "@relative-period|current|1|month|UTC", values));
        Assert.False(Trigger("fid_19", "not-during", "@relative-period|current|1|month|UTC", values));
        Assert.False(Trigger("fid_19", "during", "@relative-period|previous|1|year|UTC", values));
    }

    // ── does not start / end with, wildcard match ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Invoice 2026", "not-starts-with", "inv", false)]
    [InlineData("Invoice 2026", "not-starts-with", "bill", true)]
    [InlineData("Invoice 2026", "not-ends-with", "2026", false)]
    [InlineData("Invoice 2026", "not-ends-with", "2025", true)]
    [InlineData("Invoice 2026", "wildcard", "inv*26", true)]
    [InlineData("Invoice 2026", "wildcard", "Invoice 202?", true)]
    [InlineData("Invoice 2026", "wildcard", "*2025", false)]
    [InlineData("Invoice 2026", "not-wildcard", "inv*", false)]
    [InlineData("Invoice 2026", "not-wildcard", "bill*", true)]
    [InlineData("a*b", "wildcard", "a\\*b", true)]
    [InlineData("axb", "wildcard", "a\\*b", false)]
    public void TextOperators_OnATriggerRule(string stored, string op, string value, bool expected)
    {
        Assert.Equal(expected, Trigger("fid_10", op, value, new() { [10] = stored }));
        Assert.Equal(expected, PipelineFilterEvaluator.EvaluateConditionOperator(stored, op, value, "TEXT"));
    }

    [Fact]
    public void Wildcard_OnABlankValue_MatchesOnlyAnEverythingPattern()
    {
        Assert.True(PipelineFilterEvaluator.EvaluateConditionOperator("", "wildcard", "*", "TEXT"));
        Assert.False(PipelineFilterEvaluator.EvaluateConditionOperator("", "wildcard", "a*", "TEXT"));
        Assert.True(PipelineFilterEvaluator.EvaluateConditionOperator("", "not-wildcard", "a*", "TEXT"));
    }

    // ── includes (Multi-select, User list) ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Red;Blue", "Blue", true)]
    [InlineData("Red;Blue", "Green", false)]
    [InlineData("Red;Blue", "Green, Blue", true)]
    [InlineData("[\"Red\",\"Blue\"]", "blue", true)]
    [InlineData("Red, Blue", "Blue", true)]
    [InlineData("Red, dark;Blue", "Red, dark", true)]
    [InlineData("", "Red", false)]
    [InlineData("Red;Blue", "", false)]
    public void Includes_HoldsAnyOfTheValues(string stored, string wanted, bool expected)
    {
        Assert.Equal(expected, Trigger("fid_14", "includes", wanted, new() { [14] = stored }));
        Assert.Equal(!expected, Trigger("fid_14", "not-includes", wanted, new() { [14] = stored }));
    }

    [Fact]
    public void Includes_OnAUserList_ComparesUserIds()
    {
        var values = new Dictionary<long, object?> { [32] = $"[\"{Dipak}\",\"{Ronak}\"]" };
        Assert.True(Trigger("fid_32", "includes", Ronak.ToString(), values));
        Assert.True(Trigger("fid_32", "includes", $"1, {Dipak}", values));
        Assert.False(Trigger("fid_32", "includes", "1, 2", values));
        Assert.True(Trigger("fid_32", "not-includes", "1, 2", values));
    }

    // ── is the current user ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CurrentUser_IsWhoeverTheTriggerRunsFor()
    {
        var owned = new Dictionary<long, object?> { [4] = Dipak.ToString() };
        Assert.True(Trigger("fid_4", "is-current-user", null, owned, Dipak));
        Assert.False(Trigger("fid_4", "is-current-user", null, owned, Ronak));
        Assert.False(Trigger("fid_4", "is-not-current-user", null, owned, Dipak));
        Assert.True(Trigger("fid_4", "is-not-current-user", null, owned, Ronak));
    }

    [Fact]
    public void CurrentUser_OnAUserList_MeansTheListHoldsThem()
    {
        var team = new Dictionary<long, object?> { [32] = $"{Dipak},{Ronak}" };
        Assert.True(Trigger("fid_32", "is-current-user", null, team, Ronak));
        Assert.False(Trigger("fid_32", "is-current-user", null, team, 1));
    }

    [Fact]
    public void CurrentUser_ABlankFieldIsNotTheCurrentUser_AndAnUnknownUserIsNobody()
    {
        var blank = new Dictionary<long, object?> { [4] = null };
        Assert.False(Trigger("fid_4", "is-current-user", null, blank, Dipak));
        Assert.True(Trigger("fid_4", "is-not-current-user", null, blank, Dipak));
        var owned = new Dictionary<long, object?> { [4] = Dipak.ToString() };
        Assert.False(Trigger("fid_4", "is-current-user", null, owned));
        Assert.False(Trigger("fid_4", "is-current-user", null, owned, 0));
    }

    [Fact]
    public void CurrentUser_TakesNoValue_SoTheRuleIsNotBlank()
    {
        var rule = new TriggerFilterRule { Field = "fid_4", Operator = "is-current-user", Value = "" };
        Assert.False(PipelineFilterEvaluator.IsRuleCompletelyBlank(rule));
    }

    // ── wildcard match on a user field: the person's name or email ─────────────────────────────────────────────

    [Theory]
    [InlineData("dipak*", true)]
    [InlineData("*@example.com", true)]
    [InlineData("Dipak Patel", true)]
    [InlineData("dipak.p", true)]
    [InlineData("ronak*", false)]
    public void Wildcard_OnAUserField_MatchesThePersonsNameOrEmail(string pattern, bool expected)
    {
        var owned = new Dictionary<long, object?> { [4] = Dipak.ToString() };
        Assert.Equal(expected, Trigger("fid_4", "wildcard", pattern, owned));
        Assert.Equal(!expected, Trigger("fid_4", "not-wildcard", pattern, owned));
    }

    [Fact]
    public void Wildcard_OnAUserList_MatchesWhenAnyPersonDoes()
    {
        var team = new Dictionary<long, object?> { [32] = $"{Dipak},{Ronak}" };
        Assert.True(Trigger("fid_32", "wildcard", "ronak*", team));
        Assert.False(Trigger("fid_32", "not-wildcard", "ronak*", team));
        Assert.True(Trigger("fid_32", "not-wildcard", "asha*", team));
    }

    [Fact]
    public void Wildcard_OnAUserField_IsAPersonRule_AndLoadsItsPeople()
    {
        var group = Group("fid_4", "wildcard", "dipak*");
        Assert.True(PipelineUserFieldFilter.TryResolveRule(group.Rules![0], Fields, out var field, out var detail));
        Assert.Equal(4, field.Fid);
        Assert.Equal(PipelineUserFieldFilter.PersonDetail, detail);
        Assert.Single(PipelineUserFieldFilter.DetailRules(new[] { group }, Fields));
        // Other operators on the user field itself keep their old meaning (the stored id).
        Assert.False(PipelineUserFieldFilter.TryResolveRule(new TriggerFilterRule { Field = "fid_4", Operator = "contains", Value = "1" }, Fields, out _, out _));
        // A wildcard on a field that holds no user is plain text.
        Assert.False(PipelineUserFieldFilter.TryResolveRule(new TriggerFilterRule { Field = "fid_10", Operator = "wildcard", Value = "a*" }, Fields, out _, out _));
    }

    [Fact]
    public void Wildcard_OnAPersonsDetail()
    {
        var owned = new Dictionary<long, object?> { [4] = Dipak.ToString() };
        Assert.True(Trigger("fid_4.email", "wildcard", "dipak@*", owned));
        Assert.True(Trigger("fid_4.email", "not-wildcard", "ronak@*", owned));
        Assert.True(Trigger("fid_4.first_name", "not-starts-with", "R", owned));
        Assert.False(Trigger("fid_4.first_name", "not-ends-with", "ak", owned));
    }

    [Fact]
    public void Wildcard_OnAUserField_SearchRecordsCondition()
    {
        var team = Fields.Single(f => f.Fid == 32);
        var filter = PipelineUserFieldFilter.BuildDbFilter(team, PipelineUserFieldFilter.PersonDetail, "wildcard", "ronak*", new[] { Dipak, Ronak }, People);
        var json = JsonSerializer.Serialize(filter.Node);
        Assert.Contains("includes", json);
        Assert.Contains(Ronak.ToString(), json);
        Assert.DoesNotContain(Dipak.ToString(), json);
    }

    // ── one part of an address ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("fid_30.city", "is", "pune", true)]
    [InlineData("fid_30.city", "is-not", "Pune", false)]
    [InlineData("fid_30.city", "contains", "un", true)]
    [InlineData("fid_30.zip", "starts-with", "411", true)]
    [InlineData("fid_30.country", "wildcard", "Ind*", true)]
    [InlineData("fid_30.state", "not-wildcard", "M?", false)]
    [InlineData("fid_30.street2", "is-empty", null, true)]
    [InlineData("Site.city", "is", "Pune", true)]
    [InlineData("Address.city", "is", "Pune", true)]
    public void AddressPart_ComparesThatPartAsText(string field, string op, string? value, bool expected)
    {
        Assert.Equal(expected, Trigger(field, op, value, new() { [30] = PuneAddress }));
    }

    [Fact]
    public void AddressPart_OfABlankOrBrokenAddress_IsEmpty()
    {
        Assert.True(Trigger("fid_30.city", "is-empty", null, new() { [30] = null }));
        Assert.True(Trigger("fid_30.city", "is-empty", null, new() { [30] = "not json" }));
        Assert.Equal("Pune", PipelineAddressFieldFilter.ReadPart(JsonDocument.Parse(PuneAddress).RootElement, "city"));
    }

    [Fact]
    public void AddressPart_ResolvesOnlyAKnownPartOfAnAddressField()
    {
        Assert.True(PipelineAddressFieldFilter.TryResolve("fid_30.city", Fields, out var field, out var part));
        Assert.Equal((30, "city"), (field.Fid!.Value, part));
        Assert.False(PipelineAddressFieldFilter.TryResolve("fid_30.planet", Fields, out _, out _));
        Assert.False(PipelineAddressFieldFilter.TryResolve("fid_10.city", Fields, out _, out _));
        Assert.False(PipelineAddressFieldFilter.TryResolve("fid_30", Fields, out _, out _));
    }

    // ── saving: the validator accepts the new operators where the UI offers them ────────────────────────────────

    [Theory]
    [InlineData("fid_10", "not-starts-with", "a")]
    [InlineData("fid_10", "not-ends-with", "a")]
    [InlineData("fid_10", "wildcard", "a*")]
    [InlineData("fid_10", "not-wildcard", "a*")]
    [InlineData("fid_14", "includes", "Red")]
    [InlineData("fid_14", "not-includes", "Red")]
    [InlineData("fid_19", "during", "@relative-period|current|1|week|UTC")]
    [InlineData("fid_20", "not-during", "@relative-period|next|2|month|UTC")]
    [InlineData("fid_29", "not-starts-with", "a")]
    [InlineData("fid_30", "wildcard", "*Pune*")]
    [InlineData("fid_30.city", "wildcard", "P*")]
    [InlineData("fid_30.city", "is", "Pune")]
    [InlineData("fid_4", "is-current-user", "")]
    [InlineData("fid_4", "is-not-current-user", "")]
    [InlineData("fid_4", "wildcard", "dipak*")]
    [InlineData("fid_32", "includes", "40021")]
    [InlineData("fid_4.email", "not-wildcard", "a*")]
    public void Validator_AcceptsTheNewOperators(string field, string op, string value)
    {
        var errors = new Dictionary<string, List<string>>();
        PipelineFilterEvaluator.ValidateGroup(Group(field, op, value), Fields, errors, "FilterGroups[0]");
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("fid_10", "during")]
    [InlineData("fid_15", "wildcard")]
    [InlineData("fid_15", "during")]
    public void Validator_StillRejectsAnOperatorTheTypeHasNot(string field, string op)
    {
        var errors = new Dictionary<string, List<string>>();
        PipelineFilterEvaluator.ValidateGroup(Group(field, op, "x"), Fields, errors, "FilterGroups[0]");
        Assert.NotEmpty(errors);
    }

    // ── Search Records: mapped to SQL ───────────────────────────────────────────────────────────────────────────

    private static PipelineEngine Engine(long actingUser = Dipak)
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(actingUser);
        return new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), queryContext,
            Substitute.For<IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    private static FilterGroup Sql(string field, string op, string? value, long actingUser = Dipak)
    {
        var map = typeof(PipelineEngine).GetMethod("MapTriggerFilterGroupToDbFilterGroup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (FilterGroup?)map.Invoke(Engine(actingUser), new object?[] { Group(field, op, value), Fields, "{}", "path", new List<PipelineStep>(), null });
        Assert.NotNull(result);
        return result!;
    }

    private static IEnumerable<FilterCondition> Flatten(FilterGroup group) =>
        group.Nodes.SelectMany(n => n.Condition != null ? new[] { n.Condition } : Flatten(n.Group!));

    [Theory]
    [InlineData("not-starts-with", "a_b", "notStartsWith", "a[_]b")]
    [InlineData("not-ends-with", "50%", "notEndsWith", "50[%]")]
    [InlineData("not_ends_with", "x", "notEndsWith", "x")]
    [InlineData("wildcard", "a_*", "wildcard", "a_*")]
    [InlineData("not-wildcard", "a?", "notWildcard", "a?")]
    public void SearchRecords_TextOperators(string op, string value, string sqlOp, string sqlValue)
    {
        var tree = Sql("fid_10", op, value);
        var condition = Assert.Single(SearchFilterNodes.Conditions(tree));
        Assert.Equal((10, sqlOp, sqlValue), (condition.FieldId, condition.Operator, condition.Value));
        // A negated operator lets a blank record through, as the trigger filters do.
        Assert.Equal(sqlOp.StartsWith("not", StringComparison.Ordinal), SearchFilterNodes.IsOrBlank(Assert.Single(tree.Nodes)));
    }

    [Theory]
    [InlineData("includes", "includes")]
    [InlineData("not-includes", "notIncludes")]
    public void SearchRecords_Includes(string op, string sqlOp)
    {
        var tree = Sql("fid_14", op, "Red, Blue");
        var condition = Assert.Single(SearchFilterNodes.Conditions(tree));
        Assert.Equal((14, sqlOp, "Red, Blue"), (condition.FieldId, condition.Operator, condition.Value));
        Assert.Equal(sqlOp == "notIncludes", SearchFilterNodes.IsOrBlank(Assert.Single(tree.Nodes)));
    }

    [Fact]
    public void SearchRecords_During_IsThePeriodsWindow()
    {
        const string period = "@relative-period|previous|2|month|UTC";
        var window = RelativeFilterDate.PeriodWindow(period);

        var during = Assert.Single(Sql("fid_19", "during", period).Nodes).Group!;
        Assert.Equal("and", during.Logic);
        Assert.Equal(new[] { ("gte", window.Start.ToString("O")), ("lt", window.End.ToString("O")) },
            Flatten(during).Select(c => (c.Operator, c.Value!)));

        var notDuring = Assert.Single(Sql("fid_20", "not-during", period).Nodes).Group!;
        Assert.Equal("or", notDuring.Logic);
        Assert.Equal(new[] { ("lt", window.Start.ToString("O")), ("gte", window.End.ToString("O")) },
            Flatten(notDuring).Select(c => (c.Operator, c.Value!)));
    }

    [Fact]
    public void SearchRecords_During_WithoutAPeriod_MatchesNothing()
    {
        var none = Assert.Single(Sql("fid_19", "during", "2026-10-14").Nodes).Group!;
        Assert.Equal("and", none.Logic);
        Assert.Equal(new[] { "isEmpty", "isNotEmpty" }, Flatten(none).Select(c => c.Operator));
    }

    [Fact]
    public void SearchRecords_CurrentUser_IsTheUserTheStepRunsAs()
    {
        var owner = Assert.Single(Flatten(Sql("fid_4", "is-current-user", null, Ronak)));
        Assert.Equal((4, "eq", Ronak.ToString()), (owner.FieldId, owner.Operator, owner.Value));

        var team = Assert.Single(Flatten(Sql("fid_32", "is-current-user", null, Ronak)));
        Assert.Equal((32, "includes", Ronak.ToString()), (team.FieldId, team.Operator, team.Value));

        // "is not the current user" keeps the records nobody is assigned to, as the report filter does.
        var notOwner = Assert.Single(Sql("fid_4", "is-not-current-user", null, Ronak).Nodes).Group!;
        Assert.Equal("or", notOwner.Logic);
        Assert.Equal(new[] { ("isEmpty", (string?)null), ("ne", Ronak.ToString()) }, Flatten(notOwner).Select(c => (c.Operator, c.Value)));
        var notInTeam = Flatten(Sql("fid_32", "is-not-current-user", null, Ronak)).ToList();
        Assert.Contains(notInTeam, c => c.Operator == "notIncludes" && c.Value == Ronak.ToString());
    }

    [Theory]
    [InlineData("fid_30.city", "is", "Pune", "eq", "Pune")]
    [InlineData("fid_30.zip", "starts-with", "41_", "startsWith", "41[_]")]
    [InlineData("Site.country", "wildcard", "Ind*", "wildcard", "Ind*")]
    public void SearchRecords_AddressPart_FiltersOnThePropertyOfTheAddress(string field, string op, string value, string sqlOp, string sqlValue)
    {
        var condition = Assert.Single(Flatten(Sql(field, op, value)));
        Assert.Equal(30, condition.FieldId);
        Assert.Equal(field.Split('.')[1], condition.SubField);
        Assert.Equal((sqlOp, sqlValue), (condition.Operator, condition.Value));
    }

    [Fact]
    public void SearchRecords_AWholeAddress_HasNoPart()
    {
        var condition = Assert.Single(Flatten(Sql("fid_30", "contains", "Pune")));
        Assert.Null(condition.SubField);
    }

    // ── Condition step ──────────────────────────────────────────────────────────────────────────────────────────

    private static bool ConditionRule(string? leftToken, string left, string op, string right, string category, long actingUser = Dipak)
    {
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateConditionRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((Task<bool>)evaluate.Invoke(Engine(actingUser), new object?[]
        {
            leftToken, left, op, right, category, "{}", new List<PipelineStep>(),
            Substitute.For<IAppFieldRepository>(), Substitute.For<IAppTableRepository>(), CancellationToken.None
        })!).GetAwaiter().GetResult();
    }

    [Fact]
    public void ConditionStep_CurrentUser_IsTheUserTheStepRunsAs()
    {
        Assert.True(ConditionRule(null, Dipak.ToString(), "is-current-user", "", "TEXT", Dipak));
        Assert.False(ConditionRule(null, Dipak.ToString(), "is-current-user", "", "TEXT", Ronak));
        Assert.True(ConditionRule(null, $"{Dipak},{Ronak}", "is-current-user", "", "TEXT", Ronak));
        Assert.True(ConditionRule(null, "", "is-not-current-user", "", "TEXT", Ronak));
    }

    [Fact]
    public void ConditionStep_OtherOperators_CompareTheValuesAsBefore()
    {
        Assert.True(ConditionRule(null, "Invoice 2026", "wildcard", "inv*", "TEXT"));
        Assert.True(ConditionRule(null, "Red;Blue", "includes", "Blue", "TEXT"));
        Assert.True(ConditionRule(null, DateTime.UtcNow.ToString("O"), "during", "@relative-period|current|1|day|UTC", "DATE"));
        Assert.True(ConditionRule(null, "12", "greater-than", "5", "NUMBER"));
        Assert.False(ConditionRule(null, "abc", "equals", "abd", "TEXT"));
    }

    // ── the streamed in-memory search (encrypted / formula fields) ──────────────────────────────────────────────

    private static bool InMemory(AppField field, object? stored, FilterCondition condition)
    {
        var row = new Dictionary<string, object?> { [field.PhysicalColumnName!] = stored };
        var pairs = new[] { ((IReadOnlyDictionary<string, object?>)row, (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()) };
        return PipelineComputedFilter.Apply(pairs, new FilterGroup { Logic = "and", Nodes = new() { new FilterNode { Condition = condition } } }, new[] { field }).Count == 1;
    }

    [Theory]
    [InlineData("Red;Blue", "Blue", true)]
    [InlineData("Red;Blue", "Green, Blue", true)]
    [InlineData("Red;Blue", "Green", false)]
    public void InMemorySearch_IncludesAnyOfTheValues(string stored, string wanted, bool expected)
    {
        var field = new AppField { Id = 1, Fid = 14, Name = "Colors", TypeCode = "MultiSelect", IsEncrypted = true, PhysicalColumnName = "f_14" };
        Assert.Equal(expected, InMemory(field, stored, new FilterCondition { FieldId = 14, Operator = "includes", Value = wanted }));
        Assert.Equal(!expected, InMemory(field, stored, new FilterCondition { FieldId = 14, Operator = "notIncludes", Value = wanted }));
    }

    [Fact]
    public void InMemorySearch_AddressPart()
    {
        var field = new AppField { Id = 1, Fid = 30, Name = "Address", TypeCode = "Address", IsEncrypted = true, PhysicalColumnName = "f_30" };
        Assert.True(InMemory(field, PuneAddress, new FilterCondition { FieldId = 30, Operator = "eq", Value = "Pune", SubField = "city" }));
        Assert.False(InMemory(field, PuneAddress, new FilterCondition { FieldId = 30, Operator = "eq", Value = "Mumbai", SubField = "city" }));
        Assert.True(InMemory(field, PuneAddress, new FilterCondition { FieldId = 30, Operator = "wildcard", Value = "41*", SubField = "zip" }));
    }
}
