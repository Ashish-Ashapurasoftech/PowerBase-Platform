using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Regression coverage for Summary / Lookup / Reference fields (alongside Formula) in every place a
/// pipeline searches or filters: the On New Event trigger filter, Search Records and the shared
/// in-memory evaluator. These fields have no stored value (Summary / Lookup) or store only the
/// parent's Record ID# (Reference), so a filter has to compare the value the user sees.
/// </summary>
public class PipelineComputedFieldSearchTests
{
    private const long NameFid = 6;
    private const long SummaryFid = 14;
    private const long ReferenceFid = 9;
    private static readonly AppTable Table = new() { Id = 100, AppId = 1, PublicId = Guid.NewGuid() };

    private static AppField Summary(string function, string? targetTypeCode = null) => new()
    {
        Id = SummaryFid, Fid = (int)SummaryFid, Name = "Summary", TypeCode = "Summary",
        Settings = JsonSerializer.Serialize(new { function, targetTypeCode })
    };

    private static AppField Lookup(string sourceTypeCode) => new()
    {
        Id = 12, Fid = 12, Name = "Lookup", TypeCode = "Lookup",
        Settings = JsonSerializer.Serialize(new { sourceTypeCode })
    };

    private static AppField Reference() => new() { Id = ReferenceFid, Fid = (int)ReferenceFid, Name = "Related Customer", TypeCode = "Reference" };

    private static List<AppField> TableFields(params AppField[] extra) =>
        new List<AppField> { new() { Id = NameFid, Fid = (int)NameFid, Name = "Name", TypeCode = "Text" } }.Concat(extra).ToList();

    // ───────────────────────── type category ─────────────────────────

    [Theory]
    [InlineData("Count", null, "NUMBER")]
    [InlineData("Sum", null, "NUMBER")]
    [InlineData("Avg", null, "NUMBER")]
    [InlineData("DistinctCount", null, "NUMBER")]
    [InlineData("Exists", null, "BOOLEAN")]
    [InlineData("CombinedText", null, "TEXT")]
    [InlineData("Max", "Date", "DATE")]
    [InlineData("Min", "Number", "NUMBER")]
    public void TypeCategory_Summary_FollowsItsCalculation(string function, string? target, string expected)
        => Assert.Equal(expected, PipelineFilterEvaluator.GetTypeCategory(Summary(function, target)));

    [Theory]
    [InlineData("Number", "NUMBER")]
    [InlineData("Currency", "NUMBER")]
    [InlineData("Date", "DATE")]
    [InlineData("DateTime", "DATE")]
    [InlineData("Text", "TEXT")]
    [InlineData("Boolean", "BOOLEAN")]
    [InlineData("Formula_Number", "NUMBER")]
    public void TypeCategory_Lookup_FollowsThePulledDownField(string source, string expected)
        => Assert.Equal(expected, PipelineFilterEvaluator.GetTypeCategory(Lookup(source)));

    [Fact]
    public void TypeCategory_Reference_IsText() =>
        Assert.Equal("TEXT", PipelineFilterEvaluator.GetTypeCategory(Reference()));

    // ───────────────────────── trigger filter evaluation ─────────────────────────

    private static bool Evaluate(string field, string op, string value, IReadOnlyDictionary<long, object?> values, params AppField[] fields)
    {
        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = new List<TriggerFilterRule> { new() { Field = field, Operator = op, Value = value } } };
        return PipelineFilterEvaluator.EvaluateGroup(group, values, TableFields(fields));
    }

    [Fact]
    public void Trigger_SummaryCombinedText_Is_MatchesTheJoinedText()
    {
        var values = new Dictionary<long, object?> { [SummaryFid] = "Hardik, lalo" };
        Assert.True(Evaluate("fid_14", "is", "Hardik, lalo", values, Summary("CombinedText")));
        Assert.True(Evaluate("fid_14", "is", "hardik, LALO", values, Summary("CombinedText")));
        Assert.False(Evaluate("fid_14", "is", "Hardik", values, Summary("CombinedText")));
        Assert.True(Evaluate("fid_14", "contains", "lalo", values, Summary("CombinedText")));
        Assert.True(Evaluate("fid_14", "starts-with", "Hardik", values, Summary("CombinedText")));
    }

    [Fact]
    public void Trigger_SummaryCount_ComparesNumerically()
    {
        // A long Count: as text "9" > "10" would be true.
        Assert.False(Evaluate("fid_14", "greater_than", "10", new Dictionary<long, object?> { [SummaryFid] = 9L }, Summary("Count")));
        Assert.True(Evaluate("fid_14", "greater_than", "10", new Dictionary<long, object?> { [SummaryFid] = 25L }, Summary("Count")));
        Assert.True(Evaluate("fid_14", "is", "3", new Dictionary<long, object?> { [SummaryFid] = 3L }, Summary("Count")));
    }

    [Fact]
    public void Trigger_SummaryMaxOfDate_ComparesAsDate()
    {
        var values = new Dictionary<long, object?> { [SummaryFid] = new DateTime(2026, 9, 1) };
        Assert.True(Evaluate("fid_14", "is-before", "2026-10-01", values, Summary("Max", "Date")));
        Assert.False(Evaluate("fid_14", "is-after", "2026-10-01", values, Summary("Max", "Date")));
    }

    [Fact]
    public void Trigger_LookupOfNumber_ComparesNumerically()
    {
        var values = new Dictionary<long, object?> { [12] = 25m };
        Assert.True(Evaluate("fid_12", "greater_than", "10", values, Lookup("Number")));
        Assert.False(Evaluate("fid_12", "less_than", "10", values, Lookup("Number")));
    }

    [Fact]
    public void Trigger_SummaryExists_IsTrue()
    {
        Assert.True(Evaluate("fid_14", "is_true", "", new Dictionary<long, object?> { [SummaryFid] = true }, Summary("Exists")));
        Assert.True(Evaluate("fid_14", "is_false", "", new Dictionary<long, object?> { [SummaryFid] = false }, Summary("Exists")));
    }

    [Fact]
    public void Trigger_Reference_MatchesDisplayTextOrStoredId()
    {
        // The interceptor puts the display text under the Fid and keeps the stored id under -Fid.
        var values = new Dictionary<long, object?> { [ReferenceFid] = "Hardik", [-ReferenceFid] = 42L };
        Assert.True(Evaluate("fid_9", "is", "Hardik", values, Reference()));
        Assert.True(Evaluate("fid_9", "is", "42", values, Reference()), "an id saved before the display key changed keeps matching");
        Assert.True(Evaluate("fid_9", "contains", "ardi", values, Reference()));
        Assert.False(Evaluate("fid_9", "is", "Other", values, Reference()));
        Assert.False(Evaluate("fid_9", "is-not", "Hardik", values, Reference()));
        Assert.False(Evaluate("fid_9", "is-not", "42", values, Reference()));
        Assert.True(Evaluate("fid_9", "is-not", "Other", values, Reference()));
    }

    [Fact]
    public void Trigger_Reference_WithoutLabel_ComparesStoredId()
    {
        var values = new Dictionary<long, object?> { [ReferenceFid] = 42L };
        Assert.True(Evaluate("fid_9", "is", "42", values, Reference()));
        Assert.False(Evaluate("fid_9", "is", "Hardik", values, Reference()));
    }

    [Fact]
    public void Trigger_AdvancedQuery_ReferenceAndSummary()
    {
        var fields = TableFields(Reference(), Summary("Count"));
        var values = new Dictionary<long, object?> { [ReferenceFid] = "Hardik", [-ReferenceFid] = 42L, [SummaryFid] = 12L };
        FilterGroup Tree(long fid, string op, string value) => new() { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value } }] };

        Assert.True(PipelineFilterEvaluator.EvaluateFilterGroup(Tree(ReferenceFid, "eq", "Hardik"), values, fields));
        Assert.True(PipelineFilterEvaluator.EvaluateFilterGroup(Tree(ReferenceFid, "eq", "42"), values, fields));
        Assert.False(PipelineFilterEvaluator.EvaluateFilterGroup(Tree(ReferenceFid, "ne", "42"), values, fields));
        Assert.True(PipelineFilterEvaluator.EvaluateFilterGroup(Tree(ReferenceFid, "contains", "ard"), values, fields));
        Assert.True(PipelineFilterEvaluator.EvaluateFilterGroup(Tree(SummaryFid, "gt", "9"), values, fields));
        Assert.False(PipelineFilterEvaluator.EvaluateFilterGroup(Tree(SummaryFid, "gt", "100"), values, fields));
    }

    [Theory]
    [InlineData("is-after", "2026-10-01", true)]
    [InlineData("is-after", "2026-12-01", false)]
    [InlineData("is-on-or-after", "2026-11-05", true)]
    [InlineData("is-before", "2026-12-01", true)]
    [InlineData("is-before", "2026-10-01", false)]
    [InlineData("is-on-or-before", "2026-11-05", true)]
    public void Trigger_HyphenatedDateOperators_AreEvaluated(string op, string operand, bool expected)
    {
        // The editor emits these hyphenated; they used to normalize to a name no branch matched.
        var field = new AppField { Id = 20, Fid = 20, Name = "When", TypeCode = "Date" };
        var values = new Dictionary<long, object?> { [20] = new DateTime(2026, 11, 5) };
        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = new List<TriggerFilterRule> { new() { Field = "fid_20", Operator = op, Value = operand } } };
        Assert.Equal(expected, PipelineFilterEvaluator.EvaluateGroup(group, values, new[] { field }));
    }

    [Theory]
    [InlineData("is-after", "10", true)]
    [InlineData("is-on-or-before", "25", true)]
    [InlineData("is-before", "25", false)]
    public void Trigger_HyphenatedOperators_WorkOnSummaryNumbers(string op, string operand, bool expected)
    {
        var values = new Dictionary<long, object?> { [SummaryFid] = 25L };
        Assert.Equal(expected, Evaluate("fid_14", op, operand, values, Summary("Sum")));
    }

    // ───────────────────────── in-memory evaluator (Search Records / Copy Records) ─────────────────────────

    private static List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> Pairs(params (long Id, long? RefId, object? Summary, object? Label)[] rows) =>
        rows.Select(r => ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = r.Id, ["f_9"] = r.RefId, ["f_6"] = "x" },
                          (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [SummaryFid] = r.Summary, [ReferenceFid] = r.Label })).ToList();

    private static FilterGroup One(long fid, string op, string? value, string? mode = null, long? valueFid = null) =>
        new() { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value, ValueMode = mode, ValueFieldId = valueFid } }] };

    private static List<long> Ids(IEnumerable<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> pairs) =>
        pairs.Select(p => Convert.ToInt64(p.Row["Id"])).ToList();

    [Fact]
    public void InMemory_SummaryCount_GreaterThan_IsNumeric()
    {
        var fields = TableFields(Summary("Count"));
        var pairs = Pairs((1, null, 9L, null), (2, null, 25L, null), (3, null, 100L, null));
        Assert.Equal(new long[] { 2, 3 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "gt", "10"), fields)));
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "lte", "9"), fields)));
        Assert.Equal(new long[] { 2 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "eq", "25"), fields)));
    }

    [Fact]
    public void InMemory_SummaryCombinedText_TextOperators()
    {
        var fields = TableFields(Summary("CombinedText"));
        var pairs = Pairs((1, null, "Hardik, lalo", null), (2, null, "Other", null), (3, null, null, null));
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "eq", "Hardik, lalo"), fields)));
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "contains", "lalo"), fields)));
        Assert.Equal(new long[] { 2, 3 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "notContains", "lalo"), fields)));
        Assert.Equal(new long[] { 3 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "isEmpty", null), fields)));
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "wildcard", "Hardik*"), fields)));
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "includes", "lalo"), fields)));
    }

    [Fact]
    public void InMemory_FieldToFieldComparison_UsesTheOtherFieldsValue()
    {
        var fields = TableFields(Summary("Count"), new AppField { Id = 12, Fid = 12, Name = "Limit", TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"Number\"}" });
        var pairs = new List<(IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<long, object?>)>
        {
            (new Dictionary<string, object?> { ["Id"] = 1L }, new Dictionary<long, object?> { [SummaryFid] = 5L, [12] = 3m }),
            (new Dictionary<string, object?> { ["Id"] = 2L }, new Dictionary<long, object?> { [SummaryFid] = 2L, [12] = 3m }),
        };
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(SummaryFid, "gt", null, "field", 12), fields)));
    }

    [Fact]
    public void InMemory_Reference_MatchesLabelAndStoredId()
    {
        var fields = TableFields(Reference());
        var pairs = Pairs((1, 42, null, "Hardik Patel"), (2, 43, null, "Lalo Shah"), (3, null, null, null));
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(ReferenceFid, "contains", "hardik"), fields)));
        Assert.Equal(new long[] { 2 }, Ids(PipelineComputedFilter.Apply(pairs, One(ReferenceFid, "eq", "Lalo Shah"), fields)));
        Assert.Equal(new long[] { 2 }, Ids(PipelineComputedFilter.Apply(pairs, One(ReferenceFid, "eq", "43"), fields)));
        Assert.Equal(new long[] { 1, 3 }, Ids(PipelineComputedFilter.Apply(pairs, One(ReferenceFid, "ne", "43"), fields)));
        Assert.Equal(new long[] { 3 }, Ids(PipelineComputedFilter.Apply(pairs, One(ReferenceFid, "isEmpty", null), fields)));
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(ReferenceFid, "startsWith", "Hardik"), fields)));
    }

    [Fact]
    public void InMemory_Reference_WithoutLabel_FallsBackToStoredId()
    {
        // Display key is Record ID#: nothing is projected, the stored id is what the user sees.
        var fields = TableFields(Reference());
        var pairs = new List<(IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<long, object?>)>
        {
            (new Dictionary<string, object?> { ["Id"] = 1L, ["f_9"] = 42L }, new Dictionary<long, object?>()),
            (new Dictionary<string, object?> { ["Id"] = 2L, ["f_9"] = 7L }, new Dictionary<long, object?>()),
        };
        Assert.Equal(new long[] { 1 }, Ids(PipelineComputedFilter.Apply(pairs, One(ReferenceFid, "eq", "42"), fields)));
    }

    [Fact]
    public void InMemory_OrGroup_AndNestedGroup()
    {
        var fields = TableFields(Summary("Count"));
        var pairs = Pairs((1, null, 1L, null), (2, null, 50L, null), (3, null, 500L, null));
        var tree = new FilterGroup
        {
            Logic = "or",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = SummaryFid, Operator = "lt", Value = "5" } },
                new FilterNode { Group = new FilterGroup { Logic = "and", Nodes =
                [
                    new FilterNode { Condition = new FilterCondition { FieldId = SummaryFid, Operator = "gt", Value = "100" } },
                    new FilterNode { Condition = new FilterCondition { FieldId = SummaryFid, Operator = "lt", Value = "1000" } },
                ] } },
            ]
        };
        Assert.Equal(new long[] { 1, 3 }, Ids(PipelineComputedFilter.Apply(pairs, tree, fields)));
    }

    [Fact]
    public void LabelStyleReferenceFids_OnlyTextConditionsLeaveSql()
    {
        var fields = TableFields(Reference());
        Assert.Empty(PipelineComputedFilter.LabelStyleReferenceFids(fields, One(ReferenceFid, "eq", "42")));
        Assert.Empty(PipelineComputedFilter.LabelStyleReferenceFids(fields, One(ReferenceFid, "in", "[\"1\",\"2\"]")));
        Assert.Empty(PipelineComputedFilter.LabelStyleReferenceFids(fields, One(ReferenceFid, "isEmpty", null)));
        Assert.Contains(ReferenceFid, PipelineComputedFilter.LabelStyleReferenceFids(fields, One(ReferenceFid, "eq", "Hardik")));
        Assert.Contains(ReferenceFid, PipelineComputedFilter.LabelStyleReferenceFids(fields, One(ReferenceFid, "contains", "42")));
        Assert.Contains(ReferenceFid, PipelineComputedFilter.LabelStyleReferenceFids(fields, One(ReferenceFid, "in", "[\"Hardik\"]")));
        Assert.Empty(PipelineComputedFilter.LabelStyleReferenceFids(fields, One(NameFid, "contains", "x")));
        Assert.Empty(PipelineComputedFilter.LabelStyleReferenceFids(fields, null));
    }

    // ───────────────────────── trigger events ─────────────────────────

    private sealed class InterceptorHarness
    {
        public IPipelineRepository PipelineRepo { get; } = Substitute.For<IPipelineRepository>();
        public IRecordRepository RecordRepo { get; } = Substitute.For<IRecordRepository>();
        public IFormulaProjector Projector { get; } = Substitute.For<IFormulaProjector>();
        public IRelationalProjector Relational { get; } = Substitute.For<IRelationalProjector>();
        public List<PipelineOutboxItem> Outbox { get; } = new();
        public PipelineTriggerInterceptor Interceptor { get; }

        public InterceptorHarness(string field, string op, string operand)
        {
            var uow = Substitute.For<ITenantUnitOfWork>();
            uow.Transaction.Returns(Substitute.For<IDbTransaction>());
            PipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { new() { Id = 101, IsActive = true } });
            PipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
            {
                new() { Type = "trigger", Subtype = "new-event", ConfigJson = JsonSerializer.Serialize(new
                {
                    TablePublicId = Table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnModified = true,
                    TriggerOnAnyField = true,
                    Filters = new[] { new { Field = field, Operator = op, Value = operand } }
                }) }
            });
            PipelineRepo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(Outbox.Add), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
            Interceptor = new PipelineTriggerInterceptor(PipelineRepo, RecordRepo, Substitute.For<IQueryContext>(), uow, null!, null!,
                Substitute.For<ILogger<PipelineTriggerInterceptor>>(), null, null, Projector, Relational);
        }

        /// <summary>The re-read row and what the relational / formula projectors compute for it.</summary>
        public void Computes(Guid record, IReadOnlyDictionary<long, object?> computed)
        {
            RecordRepo.GetRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, long> { [record] = 50 });
            RecordRepo.GetBulkUpsertRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(ci => (IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>)new Dictionary<long, IReadOnlyDictionary<string, object?>>
                {
                    [50] = new Dictionary<string, object?> { ["Id"] = 50L, ["PublicId"] = record }
                });
            Relational.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(new List<IReadOnlyDictionary<long, object?>> { computed }));
            Projector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                    Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
                .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(2) ?? new List<IReadOnlyDictionary<long, object?>> { computed });
        }

        public JsonElement Payload() => JsonDocument.Parse(Outbox[0].TriggerPayloadJson).RootElement;
    }

    [Fact]
    public async Task Event_SummaryCombinedTextFilter_FiresWhenTheJoinedTextMatches()
    {
        var fields = TableFields(Summary("CombinedText"));
        var h = new InterceptorHarness("fid_14", "is", "Hardik, lalo");
        var id = Guid.NewGuid();
        h.Computes(id, new Dictionary<long, object?> { [SummaryFid] = "Hardik, lalo" });

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "row" }, "record-updated",
            default, new Dictionary<long, object?> { [NameFid] = "old" }, new long[] { NameFid });

        Assert.Single(h.Outbox);
        Assert.Equal("Hardik, lalo", h.Payload().GetProperty("NewValues").GetProperty("fid_14").GetString());
    }

    [Fact]
    public async Task Event_SummaryCombinedTextFilter_DoesNotFireOnADifferentText()
    {
        var fields = TableFields(Summary("CombinedText"));
        var h = new InterceptorHarness("fid_14", "is", "Hardik, lalo");
        var id = Guid.NewGuid();
        h.Computes(id, new Dictionary<long, object?> { [SummaryFid] = "Someone else" });

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "row" }, "record-added");

        Assert.Empty(h.Outbox);
    }

    [Fact]
    public async Task Event_SummaryCountFilter_ComparesNumerically()
    {
        var fields = TableFields(Summary("Count"));
        var h = new InterceptorHarness("fid_14", "greater_than", "10");
        var id = Guid.NewGuid();
        h.Computes(id, new Dictionary<long, object?> { [SummaryFid] = 9L });

        await h.Interceptor.InterceptAsync(Table, fields, id, new Dictionary<long, object?> { [NameFid] = "row" }, "record-added");

        Assert.Empty(h.Outbox);
    }

    [Fact]
    public async Task Event_ReferenceFilterByDisplayText_FiresAndKeepsTheStoredIdInThePayload()
    {
        var fields = TableFields(Reference());
        var h = new InterceptorHarness("fid_9", "contains", "Hardik");
        var id = Guid.NewGuid();
        h.Computes(id, new Dictionary<long, object?> { [ReferenceFid] = "Hardik Patel" });

        await h.Interceptor.InterceptAsync(Table, fields, id,
            new Dictionary<long, object?> { [NameFid] = "row", [ReferenceFid] = 42L }, "record-added");

        Assert.Single(h.Outbox);
        // The payload keeps the stored Record ID# (a table with no computed fields never had labels in it).
        Assert.Equal(42, h.Payload().GetProperty("NewValues").GetProperty("fid_9").GetInt64());
    }

    [Fact]
    public async Task Event_ReferenceFilterByDisplayText_DoesNotFireForAnotherParent()
    {
        var fields = TableFields(Reference());
        var h = new InterceptorHarness("fid_9", "contains", "Hardik");
        var id = Guid.NewGuid();
        h.Computes(id, new Dictionary<long, object?> { [ReferenceFid] = "Lalo Shah" });

        await h.Interceptor.InterceptAsync(Table, fields, id,
            new Dictionary<long, object?> { [NameFid] = "row", [ReferenceFid] = 43L }, "record-added");

        Assert.Empty(h.Outbox);
    }

    [Fact]
    public async Task Event_ReferenceFilterByStoredId_StillFires()
    {
        var fields = TableFields(Reference());
        var h = new InterceptorHarness("fid_9", "is", "42");
        var id = Guid.NewGuid();
        h.Computes(id, new Dictionary<long, object?> { [ReferenceFid] = "Hardik Patel" });

        await h.Interceptor.InterceptAsync(Table, fields, id,
            new Dictionary<long, object?> { [NameFid] = "row", [ReferenceFid] = 42L }, "record-added");

        Assert.Single(h.Outbox);
    }

    [Fact]
    public async Task Event_ReferenceNotFilteredOn_IsNotProjected()
    {
        var fields = TableFields(Reference());
        var h = new InterceptorHarness("fid_6", "is", "row");
        var id = Guid.NewGuid();

        await h.Interceptor.InterceptAsync(Table, fields, id,
            new Dictionary<long, object?> { [NameFid] = "row", [ReferenceFid] = 42L }, "record-added");

        Assert.Single(h.Outbox);
        h.Projector.DidNotReceiveWithAnyArgs().Project(default!, default!, default, default);
    }

    // ───────────────────────── Search Records step ─────────────────────────

    private sealed class EngineHarness
    {
        public IRecordRepository RecordRepo { get; } = Substitute.For<IRecordRepository>();
        public IPipelineRecordSearchService SearchService { get; } = Substitute.For<IPipelineRecordSearchService>();
        public IFormulaProjector Projector { get; } = Substitute.For<IFormulaProjector>();
        public IRelationalProjector Relational { get; } = Substitute.For<IRelationalProjector>();
        public PipelineEngine Engine { get; }
        public FilterGroup? CapturedFilter { get; private set; }

        public EngineHarness(IReadOnlyList<AppField> fields)
        {
            var tableRepo = Substitute.For<IAppTableRepository>();
            var fieldRepo = Substitute.For<IAppFieldRepository>();
            var uow = Substitute.For<ITenantUnitOfWork>();
            var idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
            tableRepo.GetByPublicIdAsync(Table.PublicId, Arg.Any<CancellationToken>()).Returns(Table);
            fieldRepo.ListByTableAsync(Table.Id, Arg.Any<CancellationToken>()).Returns(fields.ToList());
            uow.Transaction.Returns(Substitute.For<IDbTransaction>());
            idempotency.GetByExecutionKeyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<byte[]>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);

            var serviceProvider = Substitute.For<IServiceProvider>();
            serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(SearchService);
            serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
            serviceProvider.GetService(typeof(IFormulaProjector)).Returns(Projector);
            serviceProvider.GetService(typeof(IRelationalProjector)).Returns(Relational);

            Engine = new PipelineEngine(
                Substitute.For<IPipelineRepository>(), RecordRepo, Substitute.For<IRecordWriteService>(), tableRepo, fieldRepo,
                Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
                Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
                Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), uow, Substitute.For<IPipelineAuditFormatter>(),
                Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
                Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), idempotency);
            this.tableRepo = tableRepo; this.fieldRepo = fieldRepo; this.uow = uow; this.idempotency = idempotency;
        }

        private readonly IAppTableRepository tableRepo; private readonly IAppFieldRepository fieldRepo;
        private readonly ITenantUnitOfWork uow; private readonly IPipelineStepIdempotencyRepository idempotency;

        /// <summary>The projectors return the given relational/computed values for the row ids they are asked about.</summary>
        public void Computes(IReadOnlyDictionary<long, IReadOnlyDictionary<long, object?>> byRowId)
        {
            IReadOnlyList<IReadOnlyDictionary<long, object?>> For(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
                rows.Select(r => byRowId[Convert.ToInt64(r["Id"])]).ToList();
            Relational.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(For(ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(2))));
            Projector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                    Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
                .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(2) ?? For(ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1)));
        }

        /// <summary>Candidate rows as the SQL layer returns them for the (computed-free) physical filter.</summary>
        public void SqlReturns(List<IReadOnlyDictionary<string, object?>> candidates)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> Narrow(FilterGroup? tree) { CapturedFilter = tree; return candidates; }
            SearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(Narrow(ci.ArgAt<FilterGroup?>(3))));
            RecordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
                Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(Narrow(ci.ArgAt<FilterGroup?>(4))));
            RecordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<int>(p => p > 1), Arg.Any<int>(),
                Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(new List<IReadOnlyDictionary<string, object?>>());
        }

        public async Task<JsonElement> RunSearchAsync(string field, string op, string value)
        {
            var rules = JsonSerializer.Serialize(new[] { new { Field = field, Operator = op, Value = value } });
            var step = new PipelineStep { Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records",
                ConfigJson = $"{{\"TableId\":\"{Table.PublicId}\",\"FilterGroups\":[{{\"LogicalOp\":\"AND\",\"Rules\":{rules}}}]}}" };
            var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var json = await (Task<string>)method.Invoke(Engine, new object[]
            {
                step, "{}", new Dictionary<string, object>(), new List<PipelineStep> { step }, new Dictionary<string, object>(),
                1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
                RecordRepo, tableRepo, fieldRepo, Substitute.For<IRecordWriteService>(), Substitute.For<IPipelineTriggerInterceptor>(), uow, idempotency,
                Substitute.For<IFileStorageService>(), SearchService, CancellationToken.None
            })!;
            return JsonDocument.Parse(json).RootElement;
        }
    }

    private static IReadOnlyDictionary<string, object?> Row(long id, Guid publicId, long? refId = null) =>
        new Dictionary<string, object?> { ["Id"] = id, ["PublicId"] = publicId, ["f_6"] = $"r{id}", ["f_9"] = refId };

    private static bool HasCondition(FilterGroup? group, long fid) =>
        group != null && group.Nodes.Any(n => n.Condition?.FieldId == fid || HasCondition(n.Group, fid));

    [Fact]
    public async Task SearchRecords_SummaryCountGreaterThan_FiltersInMemoryNumerically()
    {
        var h = new EngineHarness(TableFields(Summary("Count")));
        var hit = Guid.NewGuid();
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, Guid.NewGuid()), Row(2, hit), Row(3, Guid.NewGuid()) });
        h.Computes(new Dictionary<long, IReadOnlyDictionary<long, object?>>
        {
            [1] = new Dictionary<long, object?> { [SummaryFid] = 9L },
            [2] = new Dictionary<long, object?> { [SummaryFid] = 25L },
            [3] = new Dictionary<long, object?> { [SummaryFid] = 2L },
        });

        var root = await h.RunSearchAsync("fid_14", "greater_than", "10");

        var records = root.GetProperty("records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal(hit.ToString(), records[0].GetProperty("RecordPublicId").GetString());
        Assert.False(HasCondition(h.CapturedFilter, SummaryFid), "a Summary condition must never be sent to SQL");
    }

    [Fact]
    public async Task SearchRecords_SummaryCombinedTextIs_MatchesTheJoinedText()
    {
        var h = new EngineHarness(TableFields(Summary("CombinedText")));
        var hit = Guid.NewGuid();
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, hit), Row(2, Guid.NewGuid()) });
        h.Computes(new Dictionary<long, IReadOnlyDictionary<long, object?>>
        {
            [1] = new Dictionary<long, object?> { [SummaryFid] = "Hardik, lalo" },
            [2] = new Dictionary<long, object?> { [SummaryFid] = "Other" },
        });

        var root = await h.RunSearchAsync("fid_14", "is", "Hardik, lalo");

        var records = root.GetProperty("records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal(hit.ToString(), records[0].GetProperty("RecordPublicId").GetString());
    }

    [Fact]
    public async Task SearchRecords_LookupOfNumber_FiltersInMemory()
    {
        var h = new EngineHarness(TableFields(Lookup("Number")));
        var hit = Guid.NewGuid();
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, hit), Row(2, Guid.NewGuid()) });
        h.Computes(new Dictionary<long, IReadOnlyDictionary<long, object?>>
        {
            [1] = new Dictionary<long, object?> { [12] = 25m },
            [2] = new Dictionary<long, object?> { [12] = 5m },
        });

        var root = await h.RunSearchAsync("fid_12", "greater_than", "10");

        Assert.Equal(1, root.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task SearchRecords_ReferenceByDisplayText_MatchesInMemoryAndKeepsStoredIdInOutput()
    {
        var h = new EngineHarness(TableFields(Reference()));
        var hit = Guid.NewGuid();
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, hit, 42), Row(2, Guid.NewGuid(), 43) });
        h.Computes(new Dictionary<long, IReadOnlyDictionary<long, object?>>
        {
            [1] = new Dictionary<long, object?> { [ReferenceFid] = "Hardik Patel" },
            [2] = new Dictionary<long, object?> { [ReferenceFid] = "Lalo Shah" },
        });

        var root = await h.RunSearchAsync("fid_9", "contains", "Hardik");

        var records = root.GetProperty("records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal(hit.ToString(), records[0].GetProperty("RecordPublicId").GetString());
        Assert.False(HasCondition(h.CapturedFilter, ReferenceFid), "a display-text condition on a Reference must not reach SQL");
        Assert.Equal(42, records[0].GetProperty("fid_9").GetInt64());
    }

    [Fact]
    public async Task SearchRecords_ReferenceById_StaysInSql()
    {
        var h = new EngineHarness(TableFields(Reference()));
        h.SqlReturns(new List<IReadOnlyDictionary<string, object?>> { Row(1, Guid.NewGuid(), 42) });

        var root = await h.RunSearchAsync("fid_9", "is", "42");

        Assert.Equal(1, root.GetProperty("records").GetArrayLength());
        Assert.True(HasCondition(h.CapturedFilter, ReferenceFid), "an id comparison on a Reference is still matched by SQL");
        h.Projector.DidNotReceiveWithAnyArgs().Project(default!, default!, default, default);
    }
}
