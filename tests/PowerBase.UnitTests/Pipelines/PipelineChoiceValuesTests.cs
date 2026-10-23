using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Single-select / Multi-select Text values a pipeline step maps: choices picked in the step's Select tab ("a, b"),
/// Quickbase-style "a;b", another step's multi-select value (a JSON array) or a mix — written the way the record
/// form saves the field (a Multi-select as a JSON array of its choices; a Single-select as its text).
/// </summary>
public class PipelineChoiceValuesTests
{
    private const string MultiSettings = "{\"options\":[\"multiselect1\",\"mutiselect 2\",\"mutiselect1\",\"mutiselect2\"]}";
    private const string CommaChoiceSettings = "{\"options\":[\"Smith, John\",\"Doe\",\"a;b\"]}";

    // ── PipelineChoiceValues ──────────────────────────────────────────────────────────

    [Fact]
    public void ChoicesOf_ReadsTheFieldOptionsInOrder()
    {
        Assert.Equal(new[] { "multiselect1", "mutiselect 2", "mutiselect1", "mutiselect2" }, PipelineChoiceValues.ChoicesOf(MultiSettings));
        Assert.Equal(new[] { "x" }, PipelineChoiceValues.ChoicesOf("{\"Options\":[\"x\",\"\",5]}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"a\"]")]
    [InlineData("{\"maxLength\":10}")]
    [InlineData("{\"options\":\"a,b\"}")]
    public void ChoicesOf_HasNoChoicesForMissingOrInvalidSettings(string? settings)
    {
        Assert.Empty(PipelineChoiceValues.ChoicesOf(settings));
    }

    [Theory]
    [InlineData("multiselect1, mutiselect 2", "[\"multiselect1\",\"mutiselect 2\"]")]
    [InlineData("multiselect1;mutiselect2", "[\"multiselect1\",\"mutiselect2\"]")]
    [InlineData(" multiselect1 ,\n mutiselect2 ;; ", "[\"multiselect1\",\"mutiselect2\"]")]
    [InlineData("mutiselect1", "[\"mutiselect1\"]")]
    [InlineData("a, a, b", "[\"a\",\"b\"]")]
    [InlineData("new choice", "[\"new choice\"]")]
    [InlineData("[Draft], b", "[\"[Draft]\",\"b\"]")]
    [InlineData("ગુજરાતી, \"quoted\"", "[\"ગુજરાતી\",\"\\\"quoted\\\"\"]")]
    public void ToStoredMultiSelect_StoresTheItemsAsAJsonArray(string mapped, string stored)
    {
        Assert.Equal(stored, PipelineChoiceValues.ToStoredMultiSelect(mapped, MultiSettings));
    }

    [Theory]
    [InlineData("[\"B1\",\"B2\"]")]
    [InlineData("[]")]
    [InlineData("  [\"ગુજરાતી\"]  ")]
    public void ToStoredMultiSelect_KeepsAJsonArrayExactlyAsItWas(string mapped)
    {
        Assert.Equal(mapped.Trim(), PipelineChoiceValues.ToStoredMultiSelect(mapped));
    }

    [Theory]
    [InlineData("[\"x\",\"y\"], multiselect1", "[\"x\",\"y\",\"multiselect1\"]")]
    [InlineData("multiselect1, [\"x\"]", "[\"multiselect1\",\"x\"]")]
    [InlineData("[\"x\"][\"y\"]", "[\"x\",\"y\"]")]
    [InlineData("[\"x\",\"multiselect1\"], multiselect1", "[\"x\",\"multiselect1\"]")]
    public void ToStoredMultiSelect_MergesAReferencedMultiSelectValueWithPickedChoices(string mapped, string stored)
    {
        Assert.Equal(stored, PipelineChoiceValues.ToStoredMultiSelect(mapped, MultiSettings));
    }

    [Theory]
    [InlineData("Smith, John, Doe", "[\"Smith, John\",\"Doe\"]")]
    [InlineData("Doe; Smith, John", "[\"Doe\",\"Smith, John\"]")]
    [InlineData("a;b, Doe", "[\"a;b\",\"Doe\"]")]
    [InlineData("Smith, Johnny", "[\"Smith\",\"Johnny\"]")]
    public void ToStoredMultiSelect_KeepsAChoiceThatHoldsADelimiterWhole(string mapped, string stored)
    {
        Assert.Equal(stored, PipelineChoiceValues.ToStoredMultiSelect(mapped, CommaChoiceSettings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void ToStoredMultiSelect_IsNullWhenNothingIsPicked(string? mapped)
    {
        Assert.Null(PipelineChoiceValues.ToStoredMultiSelect(mapped, MultiSettings));
    }

    [Fact]
    public void StoredValue_ReadsBackAsThePickedChoices()
    {
        var stored = PipelineChoiceValues.ToStoredMultiSelect("multiselect1, mutiselect 2", MultiSettings)!;
        Assert.Equal(new[] { "multiselect1", "mutiselect 2" }, JsonSerializer.Deserialize<string[]>(stored));
        Assert.Equal("multiselect1, mutiselect 2", PowerBase.Application.Common.Formatting.DisplayValueFormatter.FormatMultiSelect(stored));
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("yes", true)]
    [InlineData(" YES ", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("No", false)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    public void TryParseCheckbox_ReadsYesNoTrueFalseAndOneZero(string value, bool expected)
    {
        Assert.True(PipelineChoiceValues.TryParseCheckbox(value, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("Yes, No")]
    [InlineData("y")]
    public void TryParseCheckbox_RejectsAnythingElse(string? value)
    {
        Assert.False(PipelineChoiceValues.TryParseCheckbox(value, out _));
    }

    // ── The engine's value parsers ────────────────────────────────────────────────────

    private static readonly PipelineEngine UninitializedEngine = (PipelineEngine)RuntimeHelpers.GetUninitializedObject(typeof(PipelineEngine));

    private static object? ParseRecordMapping(string value, AppField field) =>
        typeof(PipelineEngine).GetMethod("ParseRecordMappingValueWithFormat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(UninitializedEngine, new object?[] { value, field, value, null });

    private static object? ParseBulkUpsert(string? value, string typeCode, string? settings) =>
        typeof(PipelineEngine).GetMethod("ParseBulkUpsertValueType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(UninitializedEngine, new object?[] { value, typeCode, settings });

    [Fact]
    public void RecordMapping_MultiSelectPicks_AreWrittenAsAJsonArray()
    {
        var field = new AppField { Id = 107, Fid = 7, Name = "TMultiselect", TypeCode = "MultiSelect", Settings = MultiSettings };
        Assert.Equal("[\"multiselect1\",\"mutiselect2\"]", ParseRecordMapping("multiselect1, mutiselect2", field));
        Assert.Equal("[\"B1\",\"B2\"]", ParseRecordMapping("[\"B1\",\"B2\"]", field));
    }

    [Fact]
    public void RecordMapping_SingleSelectChoice_IsWrittenAsItsText()
    {
        var field = new AppField { Id = 106, Fid = 6, Name = "Tmultichoice", TypeCode = "SingleSelect", Settings = "{\"options\":[\"choice1\",\"choice1 choice3\"]}" };
        Assert.Equal("choice1 choice3", ParseRecordMapping("choice1 choice3", field));
        Assert.Equal("a, b", ParseRecordMapping(" a, b ", field));
    }

    [Theory]
    [InlineData("Text", "a, b", "a, b")]
    [InlineData("Email", "a@b.com", "a@b.com")]
    [InlineData("MultiUser", "[\"40021\"]", "[\"40021\"]")]
    public void RecordMapping_OtherTypes_AreUnchanged(string typeCode, string value, string expected)
    {
        var field = new AppField { Id = 108, Fid = 8, Name = "Other", TypeCode = typeCode };
        Assert.Equal(expected, ParseRecordMapping(value, field));
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("No", false)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void RecordMapping_CheckboxSelectTabPicks_AreWrittenAsBooleans(string value, bool expected)
    {
        var field = new AppField { Id = 110, Fid = 10, Name = "Isactive", TypeCode = "Boolean" };
        Assert.Equal(expected, ParseRecordMapping(value, field));
    }

    [Fact]
    public void RecordMapping_CheckboxWithAnyOtherText_StillFailsWithAClearMessage()
    {
        var field = new AppField { Id = 110, Fid = 10, Name = "Isactive", TypeCode = "Boolean" };
        var error = Assert.Throws<TargetInvocationException>(() => ParseRecordMapping("maybe", field));
        var mapping = Assert.IsType<PipelineMappingException>(error.InnerException);
        Assert.Contains("Isactive", mapping.Message);
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("no", false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    public void BulkUpsert_CheckboxSelectTabPicks_AreWrittenAsBooleans(string value, bool expected)
    {
        Assert.Equal(expected, ParseBulkUpsert(value, "Boolean", null));
        Assert.Equal(expected, ParseBulkUpsert(value, "Checkbox", null));
    }

    [Fact]
    public void BulkUpsert_CheckboxWithAnyOtherText_StaysNull()
    {
        Assert.Null(ParseBulkUpsert("maybe", "Boolean", null));
        Assert.Null(ParseBulkUpsert("", "Boolean", null));
    }

    [Fact]
    public void BulkUpsert_MultiSelectPicks_AreWrittenAsAJsonArray()
    {
        Assert.Equal("[\"multiselect1\",\"mutiselect2\"]", ParseBulkUpsert("multiselect1;mutiselect2", "MultiSelect", MultiSettings));
        Assert.Equal("[\"Smith, John\",\"Doe\"]", ParseBulkUpsert("Smith, John, Doe", "MultiSelect", CommaChoiceSettings));
        Assert.Equal("[\"B1\"]", ParseBulkUpsert("[\"B1\"]", "MultiSelect", null));
    }

    [Fact]
    public void BulkUpsert_BlankMultiSelect_StaysNull_AndOtherTypesAreUnchanged()
    {
        Assert.Null(ParseBulkUpsert("", "MultiSelect", MultiSettings));
        Assert.Null(ParseBulkUpsert(null, "MultiSelect", MultiSettings));
        Assert.Equal("choice2", ParseBulkUpsert("choice2", "SingleSelect", null));
        Assert.Equal("a, b", ParseBulkUpsert("a, b", "Text", null));
        Assert.Equal(12m, ParseBulkUpsert("12", "Numeric", null));
    }

    // ── End to end through a Create Record step ───────────────────────────────────────

    [Theory]
    [InlineData("MultiSelect", "multiselect1, mutiselect 2", "[\"multiselect1\",\"mutiselect 2\"]")]
    [InlineData("MultiSelect", "{{steps.s.fid_6}}, mutiselect1", "[\"x\",\"y\",\"mutiselect1\"]")]
    [InlineData("MultiSelect", "{{steps.s.fid_6}}", "[\"x\",\"y\"]")]
    [InlineData("SingleSelect", "choice2", "choice2")]
    public async Task CreateRecord_WritesSelectTabPicks_TheWayTheFormSavesThem(string typeCode, string mapping, string expected)
    {
        var written = await WriteThroughCreateRecordAsync(typeCode, MultiSettings, mapping, "[\"x\",\"y\"]");
        Assert.Equal(expected, written[6]?.ToString());
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("No", false)]
    [InlineData("{{steps.s.fid_6}}", true)]
    public async Task CreateRecord_WritesACheckboxSelectTabPick_AsABoolean(string mapping, bool expected)
    {
        var written = await WriteThroughCreateRecordAsync("Boolean", "", mapping, true);
        Assert.Equal(expected, written[6]);
    }

    [Fact]
    public async Task CreateRecord_SkipsABlankChoiceMapping()
    {
        var written = await WriteThroughCreateRecordAsync("MultiSelect", MultiSettings, "  ", null);
        Assert.False(written.ContainsKey(6));
    }

    private static async Task<IReadOnlyDictionary<long, object?>> WriteThroughCreateRecordAsync(string typeCode, string settings, string mapping, object? source)
    {
        var tableId = Guid.NewGuid();
        IReadOnlyDictionary<long, object?>? written = null;
        var field = new AppField { Id = 106, Fid = 6, Name = "Value", TypeCode = typeCode, Settings = settings };

        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new PipelineExecutionOptions()));
        var pipelines = Substitute.For<IPipelineRepository>();
        pipelines.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true });
        services.AddSingleton(pipelines);
        services.AddSingleton<PipelineLoopWorkerPool>();
        services.AddScoped<IPipelineEngine, PipelineEngine>();
        services.AddScoped<IQueryContext>(_ =>
        {
            var query = Substitute.For<IQueryContext>();
            query.TenantId.Returns(42L);
            query.UserId.Returns(7L);
            return query;
        });
        services.AddScoped<ITenantUnitOfWork>(_ => Substitute.For<ITenantUnitOfWork>());
        var records = Substitute.For<IRecordRepository>();
        records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(),
                Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>?>())
            .Returns(call => { written = new Dictionary<long, object?>(call.ArgAt<IReadOnlyDictionary<long, object?>>(2)); return Guid.NewGuid(); });
        services.AddSingleton(records);
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 1, PublicId = tableId, Name = "Choices" });
        services.AddSingleton(tables);
        var fields = Substitute.For<IAppFieldRepository>();
        fields.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<AppField> { field });
        services.AddSingleton(fields);
        services.AddSingleton(Substitute.For<IRecordWriteService>());
        services.AddSingleton(Substitute.For<IRelationshipRepository>());
        services.AddSingleton(Substitute.For<IEmailService>());
        services.AddSingleton(Substitute.For<IHttpClientFactory>());
        services.AddSingleton(Substitute.For<IFileStorageService>());
        services.AddSingleton(Substitute.For<ILogger<PipelineEngine>>());
        services.AddSingleton(Substitute.For<IPipelineTriggerInterceptor>());
        services.AddSingleton(Substitute.For<IPipelineAuditFormatter>());
        services.AddSingleton(Substitute.For<IAdminRepository>());
        services.AddSingleton(Substitute.For<ITenantRepository>());
        services.AddSingleton(Substitute.For<IPipelineStepIdempotencyRepository>());
        services.AddSingleton(Substitute.For<IPipelineRecordSearchService>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IPipelineEngine>();

        var create = new PipelineStep
        {
            Id = 3, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "c", Type = "action", Subtype = "create-record",
            ConfigJson = JsonSerializer.Serialize(new { TableId = tableId, FieldMappings = new[] { new { Field = "fid_6", Value = mapping } } })
        };
        var steps = new Dictionary<string, object> { ["s"] = new Dictionary<string, object?> { ["fid_6"] = source } };
        var context = new Dictionary<string, object> { ["steps"] = steps, ["_MessageId"] = Guid.NewGuid(), ["_CreatedBy"] = 7L };
        await (Task)typeof(PipelineEngine).GetMethod("ExecuteSiblingStepsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, new object?[] { 1L, new List<PipelineStep> { create }, null, null, context, steps,
                new List<PipelineEngine.RawStepAuditSnapshot>(), "root", CancellationToken.None })!;

        return written ?? new Dictionary<long, object?>();
    }
}
