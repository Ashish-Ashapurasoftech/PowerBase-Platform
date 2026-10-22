using System.Reflection;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Reported from the app: a Condition on a Rich Text did not work. The record form's editor saves HTML ("&lt;p&gt;hello&lt;/p&gt;",
/// "&lt;p&gt;&lt;br&gt;&lt;/p&gt;" when emptied), so "is hello", "starts with" and "is empty" compared the markup. Every filter now reads the
/// text a Rich Text shows: a trigger, a Condition step, Search Records (in memory) and the Advanced Query.
/// </summary>
public class PipelineRichTextFilterTests
{
    private static readonly List<AppField> Fields =
    [
        new() { Id = 1, Fid = 6, Name = "Rich", Label = "Rich Text", TypeCode = "RichText" },
        new() { Id = 2, Fid = 7, Name = "FRich", Label = "Formula rich", TypeCode = "Formula_RichText" },
        new() { Id = 3, Fid = 8, Name = "LRich", Label = "Lookup rich", TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"RichText\"}" },
        new() { Id = 4, Fid = 9, Name = "Plain", Label = "Plain", TypeCode = "Text" },
    ];

    [Theory]
    [InlineData("<p>hello</p>", "hello")]
    [InlineData("<p>Fish &amp; Chips</p>", "Fish & Chips")]
    [InlineData("<p>one</p><p>two</p>", "one two")]
    [InlineData("<p><br></p>", "")]
    [InlineData("plain text", "plain text")]
    [InlineData(null, "")]
    public void RichTextPlain_IsTheTextShown(string? html, string expected) =>
        Assert.Equal(expected, PipelineFilterEvaluator.RichTextPlain(html));

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void OnlyRichTextFields_AreReadAsText(int fid) =>
        Assert.True(PipelineFilterEvaluator.IsRichTextField(Fields.Single(f => f.Fid == fid)));

    [Fact]
    public void APlainTextField_KeepsItsMarkup() =>
        Assert.False(PipelineFilterEvaluator.IsRichTextField(Fields.Single(f => f.Fid == 9)));

    private static bool Trigger(int fid, string op, string value, object? stored) =>
        PipelineFilterEvaluator.EvaluateGroup(
            new TriggerFilterGroup { LogicalOp = "AND", Rules = [new TriggerFilterRule { Field = $"fid_{fid}", Operator = op, Value = value }] },
            new Dictionary<long, object?> { [fid] = stored }, Fields);

    [Theory]
    [InlineData("is", "hello", "<p>Hello</p>", true)]
    [InlineData("starts-with", "hel", "<p><strong>hello</strong> world</p>", true)]
    [InlineData("ends-with", "world", "<p>hello <em>world</em></p>", true)]
    [InlineData("is-empty", "", "<p><br></p>", true)]
    [InlineData("is-not-empty", "", "<p><br></p>", false)]
    [InlineData("contains", "strong", "<p><strong>x</strong></p>", false)]   // the tag is not text
    [InlineData("is-not", "hello", "<p>hello</p>", false)]
    public void Trigger_ReadsTheTextShown(string op, string value, string stored, bool matches)
    {
        Assert.Equal(matches, Trigger(6, op, value, stored));
        Assert.Equal(matches, Trigger(7, op, value, stored));
        Assert.Equal(matches, Trigger(8, op, value, stored));
    }

    [Fact]
    public void Trigger_APlainTextFieldIsUnchanged() =>
        Assert.False(Trigger(9, "is", "hello", "<p>hello</p>"));

    [Theory]
    [InlineData("is", "hello", "<p>Hello</p>", true)]
    [InlineData("starts-with", "hel", "<p><b>hello</b></p>", true)]
    [InlineData("is-empty", "", "<p><br></p>", true)]
    public void SearchRecords_InMemory_ReadsTheTextShown(string op, string value, string stored, bool matches)
    {
        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = [new TriggerFilterRule { Field = "fid_6", Operator = op, Value = value }] };
        var tree = (FilterGroup)PipelineFilterSqlParityTests.Map.Invoke(PipelineFilterSqlParityTests.Engine(), [group, Fields, "{}", "path", new List<PipelineStep>(), null])!;
        var row = ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = 1L, ["f_6"] = stored },
            (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>());
        Assert.Equal(matches, PipelineComputedFilter.Apply([row], tree, Fields).Count == 1);
    }

    [Fact]
    public void AdvancedQuery_InATrigger_ReadsTheTextShown() =>
        Assert.True(PipelineFilterEvaluator.EvaluateFilterGroup(
            PipelineAdvancedQuery.ForTrigger(CopyRecordsDefinition.ParseQuery("{6.EX.'hello'}", Fields)!),
            new Dictionary<long, object?> { [6] = "<p>hello</p>" }, Fields));

    [Theory]
    [InlineData("equals", "hello", "<p>Hello</p>", true)]
    [InlineData("starts-with", "hel", "<p><b>hello</b></p>", true)]
    [InlineData("is_blank", "", "<p><br></p>", true)]
    [InlineData("contains", "strong", "<p><strong>x</strong></p>", false)]
    public async Task ConditionStep_ReadsTheTextShown(string op, string value, string stored, bool matches)
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid() };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(Fields);
        var steps = new List<PipelineStep> { new() { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "new-event", ConfigJson = $"{{\"tableId\":\"{table.PublicId}\"}}" } };
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateConditionRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<PowerBase.Application.Records.IRecordWriteService>(),
            tableRepo, fieldRepo, Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Microsoft.Extensions.Options.Options.Create(new PowerBase.Application.Common.Configurations.PipelineExecutionOptions()),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(), Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<IServiceProvider>(), Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());

        var matched = await (Task<bool>)evaluate.Invoke(engine, ["{{trigger.fid_6}}", stored, op, value, "TEXT", "{}", steps, fieldRepo, tableRepo, CancellationToken.None])!;
        Assert.Equal(matches, matched);
    }
}
