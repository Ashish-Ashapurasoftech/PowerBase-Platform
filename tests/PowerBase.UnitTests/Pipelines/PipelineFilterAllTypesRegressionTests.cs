using System.Reflection;
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
/// Regression: filtering on a person's detail ("Record Owner › Email") was added next to the existing filters. A rule on any plain
/// field — every field type a table can have, its system fields, formulas, lookups, summaries and relationships — with any operator
/// its type allows must validate, evaluate (trigger filters) and map to SQL (Search Records) exactly as before — save for the types whose
/// filters were wrong and were fixed since: a time of day compares times (TIME), a Date / Numeric Range compares the whole range.
/// </summary>
public class PipelineFilterAllTypesRegressionTests
{
    private static readonly (string TypeCode, string? Settings, string Label, int Fid, bool System)[] Types =
    {
        ("Number", null, "Record ID#", 3, true),
        ("DateTime", null, "Date Created", 1, true),
        ("DateTime", null, "Date Modified", 2, true),
        ("User", null, "Record Owner", 4, true),
        ("User", null, "Last Modified By", 5, true),
        ("Text", null, "Text", 10, false),
        ("TextMultiLine", null, "Multi-line", 11, false),
        ("RichText", null, "Rich text", 12, false),
        ("SingleSelect", "{\"options\":[\"Open\",\"Closed\"]}", "Single choice", 13, false),
        ("MultiSelect", "{\"options\":[\"Red\",\"Blue\"]}", "Multiple choice", 14, false),
        ("Number", null, "Number", 15, false),
        ("Currency", null, "Currency", 16, false),
        ("Percent", null, "Percent", 17, false),
        ("Rating", null, "Rating", 18, false),
        ("Date", null, "Date", 19, false),
        ("DateTime", null, "Date time", 20, false),
        ("Time", null, "Time", 21, false),
        ("Duration", null, "Duration", 22, false),
        ("DateRange", null, "Date range", 23, false),
        ("NumericRange", null, "Numeric range", 24, false),
        ("Boolean", null, "Check box", 25, false),
        ("Email", null, "Email", 26, false),
        ("Phone", null, "Phone", 27, false),
        ("Url", null, "Url", 28, false),
        ("File", null, "File", 29, false),
        ("Address", null, "Address", 30, false),
        ("User", null, "User field", 31, false),
        ("MultiUser", null, "User list", 32, false),
        ("Formula_Text", null, "F text", 33, false),
        ("Formula_Number", null, "F number", 34, false),
        ("Formula_Date", null, "F date", 35, false),
        ("Formula_DateTime", null, "F datetime", 36, false),
        ("Formula_Duration", null, "F duration", 37, false),
        ("Formula_Bool", null, "F bool", 38, false),
        ("Formula_User", null, "F user", 39, false),
        ("Formula_ListUser", null, "F user list", 40, false),
        ("Formula_MultiSelect", null, "F multiselect", 41, false),
        ("Formula_Time", null, "F time", 42, false),
        ("Formula_Phone", null, "F phone", 43, false),
        ("Formula_Email", null, "F email", 44, false),
        ("Formula_RichText", null, "F rich text", 45, false),
        ("Formula_Url", null, "F url", 46, false),
        ("Formula", "{\"resultType\":\"Number\"}", "Formula number", 47, false),
        ("Formula", "{\"resultType\":\"Date\"}", "Formula date", 48, false),
        ("Formula", "{\"resultType\":\"Bool\"}", "Formula bool", 49, false),
        ("Formula", "{\"resultType\":\"Text\"}", "Formula text", 50, false),
        ("Reference", "{\"parentTableId\":9}", "Customer", 51, false),
        ("Lookup", "{\"sourceTypeCode\":\"Text\"}", "Lookup text", 52, false),
        ("Lookup", "{\"sourceTypeCode\":\"Number\"}", "Lookup number", 53, false),
        ("Lookup", "{\"sourceTypeCode\":\"Date\"}", "Lookup date", 54, false),
        ("Lookup", "{\"sourceTypeCode\":\"User\"}", "Lookup user", 55, false),
        ("Summary", "{\"function\":\"Count\"}", "Summary count", 56, false),
        ("Summary", "{\"function\":\"Sum\"}", "Summary sum", 57, false),
        ("Summary", "{\"function\":\"Exists\"}", "Summary exists", 58, false),
        ("Summary", "{\"function\":\"CombinedText\"}", "Summary text", 59, false),
        ("ReportLink", null, "Report link", 60, false),
        ("ActionButton", null, "Button", 61, false),
    };

    private static readonly List<AppField> Fields = Types.Select((t, i) => new AppField
    {
        Id = 1000 + i, Fid = t.Fid, Name = t.System ? $"S_{t.Fid}" : $"f{t.Fid}", Label = t.Label, TypeCode = t.TypeCode,
        Settings = t.Settings, IsSystem = t.System
    }).ToList();

    private static readonly Dictionary<long, PipelineUserDetails> People = new()
    {
        [40021] = new(40021, "Dipak", "Patel", "dipak@example.com", "dipak.p", "Dipak Patel"),
    };

    private static string Sample(string category) => category switch
    {
        "NUMBER" => "12",
        "DATE" => "2026-10-15",
        "TIME" => "10:30:00",
        "BOOLEAN" => "true",
        _ => "abc"
    };

    /// <summary>The report filter's operators added later (wildcard, includes, is during, the current user): they have their own
    /// behaviour, covered by PipelineFilterReportOperatorsTests. Every operator before them must behave exactly as it did.</summary>
    internal static readonly HashSet<string> ReportOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "wildcard", "not-wildcard", "not_wildcard", "includes", "not-includes", "not_includes", "during", "not-during", "not_during",
        "is-current-user", "is_current_user", "is-not-current-user", "is_not_current_user"
    };

    public static IEnumerable<object[]> FieldsAndOperators() =>
        Fields.SelectMany(f => PipelineFilterEvaluator.AllowedOperatorsByTypeCategory[PipelineFilterEvaluator.GetTypeCategory(f)]
            .Where(op => !ReportOperators.Contains(op))
            .Select(op => new object[] { f.Fid!.Value, op }));

    private static TriggerFilterGroup Group(string field, string op, string value) =>
        new() { LogicalOp = "AND", Rules = new() { new TriggerFilterRule { Field = field, Operator = op, Value = value } } };

    [Theory]
    [MemberData(nameof(FieldsAndOperators))]
    public void EveryTypeAndOperator_ValidatesAndEvaluatesAsBefore(int fid, string op)
    {
        var field = Fields.Single(f => f.Fid == fid);
        var category = PipelineFilterEvaluator.GetTypeCategory(field);
        var value = Sample(category);
        var group = Group($"fid_{fid}", op, value);

        // Not a person-detail rule: it takes the existing path.
        Assert.False(PipelineUserFieldFilter.TryResolve(group.Rules![0].Field, Fields, out _, out _));
        Assert.Empty(PipelineUserFieldFilter.DetailRules(new[] { group }, Fields));

        var errors = new Dictionary<string, List<string>>();
        PipelineFilterEvaluator.ValidateGroup(group, Fields, errors, "FilterGroups[0]");
        Assert.Empty(errors);

        foreach (var stored in new object?[] { value, null, "other" })
        {
            var values = new Dictionary<long, object?> { [fid] = stored };
            var before = PipelineFilterEvaluator.EvaluateGroup(group, values, Fields);
            // Passing the people (as a trigger with a person-detail rule elsewhere does) changes nothing for this rule.
            Assert.Equal(before, PipelineFilterEvaluator.EvaluateGroup(group, values, Fields, null, People));
            if (field.TypeCode != "Reference")
                Assert.Equal(PipelineFilterEvaluator.EvaluateConditionOperator(stored?.ToString() ?? string.Empty, op, value, category), before);
        }
    }

    [Fact]
    public void EveryType_SavedByNameOrLabel_StillResolves()
    {
        foreach (var field in Fields)
        {
            var category = PipelineFilterEvaluator.GetTypeCategory(field);
            var values = new Dictionary<long, object?> { [field.Fid!.Value] = Sample(category) };
            var byFid = PipelineFilterEvaluator.EvaluateGroup(Group($"fid_{field.Fid}", "is", Sample(category)), values, Fields);
            Assert.Equal(byFid, PipelineFilterEvaluator.EvaluateGroup(Group(field.Name, "is", Sample(category)), values, Fields));
        }
    }

    [Fact]
    public void OnlyStoredUserFields_OfferPersonDetails()
    {
        foreach (var field in Fields)
        {
            var isStoredUser = field.TypeCode is "User" or "MultiUser";
            foreach (var detail in new[] { "id", "first_name", "last_name", "email", "screen_name" })
                Assert.Equal(isStoredUser, PipelineUserFieldFilter.TryResolve($"fid_{field.Fid}.{detail}", Fields, out _, out _));
        }
    }

    [Fact]
    public void ADetailOfANonUserField_IsStillRejectedAsAnUnknownField()
    {
        var errors = new Dictionary<string, List<string>>();
        PipelineFilterEvaluator.ValidateGroup(Group("fid_10.email", "is", "x"), Fields, errors, "FilterGroups[0]");
        Assert.Contains("does not exist", Assert.Single(errors.Values.SelectMany(v => v)));
    }

    // ── Search Records: every type maps to the same SQL condition as before ─────────

    private static PipelineEngine Engine()
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        return new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    [Theory]
    [MemberData(nameof(FieldsAndOperators))]
    public void EveryTypeAndOperator_MapsToTheSameSqlCondition(int fid, string op)
    {
        var field = Fields.Single(f => f.Fid == fid);
        var value = Sample(PipelineFilterEvaluator.GetTypeCategory(field));
        var map = typeof(PipelineEngine).GetMethod("MapTriggerFilterGroupToDbFilterGroup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var mapOperator = typeof(PipelineEngine).GetMethod("MapUiOperatorToDbOperator", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (FilterGroup?)map.Invoke(Engine(), new object?[] { Group($"fid_{fid}", op, value), Fields, "{}", "path", new List<PipelineStep>(), null });

        Assert.NotNull(result);
        var conditions = Flatten(result!).ToList();
        Assert.NotEmpty(conditions);
        Assert.All(conditions, c => Assert.Equal(fid, c.FieldId));
        var expected = (string)mapOperator.Invoke(null, new object?[] { op })!;
        // A Date / Numeric Range is compared as a whole range, on its start and end columns (PipelineFilterRangeTimeFileTests covers
        // what each operator means); it used to compare its start column alone, as text against a range value.
        if (PowerBase.Domain.Constants.PhysicalNaming.IsRangeTypeCode(field.TypeCode) && expected is "eq" or "ne" or "gt" or "gte" or "lt" or "lte" or "isEmpty" or "isNotEmpty")
        {
            Assert.All(conditions, c => Assert.Contains(c.SubField, new[] { "start", "end" }));
            Assert.All(conditions, c => Assert.Contains(c.Operator, new[] { "gt", "gte", "lt", "lte", "isEmpty", "isNotEmpty" }));
            return;
        }
        // A date field may use the date_ form, or a relative-date window; every other type keeps the mapped operator.
        if (PipelineFilterEvaluator.GetTypeCategory(field) != "DATE")
            Assert.Equal(expected, Assert.Single(conditions).Operator);
    }

    private static IEnumerable<FilterCondition> Flatten(FilterGroup group) =>
        group.Nodes.SelectMany(n => n.Condition != null ? new[] { n.Condition } : Flatten(n.Group!));
}
