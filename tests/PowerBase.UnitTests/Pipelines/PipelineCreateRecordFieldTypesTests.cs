using System.Data;
using System.Globalization;
using System.Reflection;
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
/// Every field type a Create Record step can write, carried from another step's record through a token
/// ({{steps.s.fid_6}}) into a field of the same type: the value the step writes must be the value it read.
/// Each value is given in both shapes a pipeline meets — as a plain app reads it (typed: decimal, DateTime,
/// bool, TimeSpan, a range object) and as an encrypted app reads it (the decrypted text).
/// </summary>
public class PipelineCreateRecordFieldTypesTests
{
    private static readonly string BigRichText =
        "<p>RR</p><p><strong>bold</strong> <em><u>text</u></em> ગુજરાતી</p><p><img src=\"data:image/png;base64," +
        new string('A', 3_000_000) + "\"></p><p>after the image</p>";

    public static IEnumerable<object?[]> Cases() => new List<object?[]>
    {
        // name, field type, value the source step holds, value the Create Record step must write
        new object?[] { "text", "Text", "Ronak", "Ronak" },
        new object?[] { "text with symbols", "Text", "a & b < c > \"q\" {{ x }} 'y' ગુજરાતી $1|2", "a & b < c > \"q\" {{ x }} 'y' ગુજરાતી $1|2" },
        new object?[] { "text over 1 MB", "Text", new string('x', 2_000_000), new string('x', 2_000_000) },
        new object?[] { "multi-line text", "TextMultiLine", "line 1\nline 2\n\nline 4", "line 1\nline 2\n\nline 4" },
        new object?[] { "rich text with an embedded image", "RichText", BigRichText, BigRichText },
        new object?[] { "single choice", "SingleSelect", "A1", "A1" },
        new object?[] { "multiple choice", "MultiSelect", "[\"B1\",\"B2\"]", "[\"B1\",\"B2\"]" },
        new object?[] { "number", "Number", 12.5m, 12.5m },
        new object?[] { "number (encrypted text)", "Number", "12.5000", 12.5m },
        new object?[] { "large number", "Number", 99999999999999.9999m, 99999999999999.9999m },
        new object?[] { "negative number", "Number", -3.25m, -3.25m },
        new object?[] { "zero", "Number", 0m, 0m },
        new object?[] { "currency", "Currency", 1234567.89m, 1234567.89m },
        new object?[] { "percent", "Percent", 0.25m, 0.25m },
        new object?[] { "rating", "Rating", 4, 4m },
        new object?[] { "duration", "Duration", 90, 90m },
        new object?[] { "duration (encrypted text)", "Duration", "90", 90m },
        new object?[] { "date", "Date", new DateTime(2026, 10, 15), new DateTime(2026, 10, 15) },
        new object?[] { "date (encrypted text)", "Date", "2026-10-15T00:00:00.0000000", new DateTime(2026, 10, 15) },
        new object?[] { "date and time", "DateTime", new DateTime(2026, 10, 15, 7, 13, 5), new DateTime(2026, 10, 15, 7, 13, 5) },
        new object?[] { "date and time (encrypted text)", "DateTime", "2026-10-15T07:13:05.0000000", new DateTime(2026, 10, 15, 7, 13, 5) },
        new object?[] { "date and time in UTC (Date Created)", "DateTime", new DateTime(2026, 10, 15, 7, 13, 5, DateTimeKind.Utc), new DateTime(2026, 10, 15, 7, 13, 5) },
        new object?[] { "time of day", "Time", new TimeSpan(7, 13, 0), new TimeSpan(7, 13, 0) },
        new object?[] { "time of day (encrypted text)", "Time", "07:13:00", new TimeSpan(7, 13, 0) },
        new object?[] { "numeric range", "NumericRange", Range(10m, 20m), new RangeExpectation("10", "20") },
        new object?[] { "numeric range (encrypted text)", "NumericRange", "{\"start\":10,\"end\":20}", new RangeExpectation("10", "20") },
        new object?[] { "numeric range with only a start", "NumericRange", Range(10m, null), new RangeExpectation("10", null) },
        new object?[] { "date range", "DateRange", Range("2026-10-01", "2026-10-05"), new RangeExpectation("2026-10-01", "2026-10-05") },
        new object?[] { "date range (encrypted text)", "DateRange", "{\"start\":\"2026-10-01\",\"end\":\"2026-10-05\"}", new RangeExpectation("2026-10-01", "2026-10-05") },
        new object?[] { "checkbox on", "Boolean", true, true },
        new object?[] { "checkbox off", "Boolean", false, false },
        new object?[] { "checkbox on (encrypted text)", "Boolean", "True", true },
        new object?[] { "checkbox off (encrypted text)", "Boolean", "False", false },
        new object?[] { "email", "Email", "ronak@example.com", "ronak@example.com" },
        new object?[] { "phone", "Phone", "{\"number\":\"+91 98765 43210\",\"ext\":\"12\"}", "{\"number\":\"+91 98765 43210\",\"ext\":\"12\"}" },
        new object?[] { "url", "Url", "https://example.com/a?b=1&c=2#x", "https://example.com/a?b=1&c=2#x" },
        new object?[] { "file", "File", "[{\"fileName\":\"a.pdf\",\"url\":\"https://files/a.pdf\",\"size\":10}]", "[{\"fileName\":\"a.pdf\",\"url\":\"https://files/a.pdf\",\"size\":10}]" },
        new object?[] { "address", "Address", "{\"street\":\"1 Main\",\"city\":\"Rajkot\",\"zip\":\"360001\"}", "{\"street\":\"1 Main\",\"city\":\"Rajkot\",\"zip\":\"360001\"}" },
        new object?[] { "user", "User", 40021L, "40021" },
        new object?[] { "user (encrypted text)", "User", "40021", "40021" },
        new object?[] { "multiple users", "MultiUser", "[\"40021\",\"40022\"]", "[\"40021\",\"40022\"]" },
        new object?[] { "reference", "Reference", 5L, "5" },
    };

    public sealed record RangeExpectation(string? Start, string? End);

    private static Dictionary<string, object?> Range(object? start, object? end) => new() { ["start"] = start, ["end"] = end };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CreateRecord_WritesTheSourceValue_ForEveryFieldType(string name, string typeCode, object? source, object? expected)
    {
        var written = await WriteThroughCreateRecordAsync(typeCode, source);

        Assert.True(written.TryGetValue(6, out var value), $"{name}: the field was not written at all.");
        AssertSame(name, typeCode, expected, value);
    }

    public static IEnumerable<object?[]> StoredRangeRows() => new List<object?[]>
    {
        // name, field type, the stored row's start column, its end column, the range the Create Record step must write
        new object?[] { "numeric range, plain app (two columns)", "NumericRange", 10.0000m, 20.0000m, new RangeExpectation("10", "20") },
        new object?[] { "date range, plain app (two columns)", "DateRange", new DateTime(2026, 10, 1), new DateTime(2026, 10, 5),
            new RangeExpectation("2026-10-01T00:00:00", "2026-10-05T00:00:00") },
        new object?[] { "numeric range, encrypted app (JSON in the start column)", "NumericRange", "{\"start\":10,\"end\":20}", null, new RangeExpectation("10", "20") },
    };

    [Theory]
    [MemberData(nameof(StoredRangeRows))]
    public async Task CreateRecord_WritesBothEndsOfARangeThatSearchRecordsRead(string name, string typeCode, object? start, object? end, RangeExpectation expected)
    {
        // Search Records (and Look Up Record) read the range's two columns; the next step must get both of them.
        var field = new AppField { Id = 106, Fid = 6, Name = "Value", TypeCode = typeCode };
        var row = new Dictionary<string, object?> { ["Id"] = 1L, ["PublicId"] = Guid.NewGuid(), ["f_6"] = start, ["f_6_e"] = end };
        var normalized = (Dictionary<string, object?>)typeof(PipelineEngine)
            .GetMethod("NormalizeSearchRecord", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { row, new List<AppField> { field } })!;

        var written = await WriteThroughCreateRecordAsync(typeCode, normalized["fid_6"]);

        AssertSame(name, typeCode, expected, written[6]);
    }

    [Fact]
    public void CreatedRecordOutput_CarriesBothEndsOfARange()
    {
        var field = new AppField { Id = 106, Fid = 6, Name = "Value", TypeCode = "NumericRange" };
        var persisted = new Dictionary<string, object?> { ["Id"] = 1L, ["f_6"] = 10m, ["f_6_e"] = 20m };

        var output = PipelineEngine.BuildCreatedRecordOutput(Guid.NewGuid(), 1, new List<AppField> { field }, new Dictionary<long, object?>(), persisted);

        var range = Assert.IsType<Dictionary<string, object?>>(output["fid_6"]);
        Assert.Equal(10m, range["start"]);
        Assert.Equal(20m, range["end"]);
    }

    private static void AssertSame(string name, string typeCode, object? expected, object? actual)
    {
        switch (expected)
        {
            case RangeExpectation range:
                var (start, end) = RangeParts(actual);
                Assert.True(range.Start == start && range.End == end, $"{name}: expected {range.Start}..{range.End}, written {start}..{end} ({actual}).");
                break;
            case decimal number:
                Assert.True(actual is decimal d && d == number, $"{name}: expected {number}, written {actual} ({actual?.GetType().Name}).");
                break;
            case TimeSpan time:
                Assert.True(actual is DateTime t && t.TimeOfDay == time, $"{name}: expected {time}, written {actual}.");
                break;
            case DateTime date:
                Assert.True(actual is DateTime dt && dt == date, $"{name}: expected {date:O}, written {(actual is DateTime w ? w.ToString("O") : actual)}.");
                break;
            case bool flag:
                Assert.True(actual is bool b && b == flag, $"{name}: expected {flag}, written {actual}.");
                break;
            default:
                var text = actual?.ToString();
                Assert.True(text == (string?)expected,
                    $"{name} ({typeCode}): written value differs — {text?.Length ?? 0:N0} chars written, {((string?)expected)?.Length ?? 0:N0} expected; ends '{(text is { Length: > 10 } ? text[^10..] : text)}'.");
                break;
        }
    }

    /// <summary>A range as the record repository splits it for its two columns: a '{"start":..,"end":..}' text or object.</summary>
    private static (string? Start, string? End) RangeParts(object? value)
    {
        JsonElement element;
        switch (value)
        {
            case string s when s.TrimStart().StartsWith('{'): element = JsonDocument.Parse(s).RootElement; break;
            case JsonElement e: element = e; break;
            case IDictionary<string, object?> d: element = JsonSerializer.SerializeToElement(d); break;
            default: return (value?.ToString(), null);
        }
        static string? Part(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null
            ? (p.ValueKind == JsonValueKind.Number ? (p.GetDecimal() / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : p.GetString()) : null;
        return (Part(element, "start"), Part(element, "end"));
    }

    private static async Task<IReadOnlyDictionary<long, object?>> WriteThroughCreateRecordAsync(string typeCode, object? source)
    {
        var tableId = Guid.NewGuid();
        IReadOnlyDictionary<long, object?>? written = null;
        var field = new AppField { Id = 106, Fid = 6, Name = "Value", TypeCode = typeCode, Settings = typeCode == "Reference" ? "{\"parentTableId\":9}" : null };

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
                Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>(), null)
            .Returns(call => { written = new Dictionary<long, object?>(call.ArgAt<IReadOnlyDictionary<long, object?>>(2)); return Guid.NewGuid(); });
        records.ExistsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        records.GetBulkUpsertRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>>(new Dictionary<long, IReadOnlyDictionary<string, object?>>()));
        services.AddSingleton(records);
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 1, PublicId = tableId, Name = "F2" });
        tables.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 9, Name = "Customer" });
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
            ConfigJson = JsonSerializer.Serialize(new { TableId = tableId, FieldMappings = new[] { new { Field = "fid_6", Value = "{{steps.s.fid_6}}" } } })
        };
        var steps = new Dictionary<string, object> { ["s"] = new Dictionary<string, object?> { ["fid_6"] = source } };
        var context = new Dictionary<string, object> { ["steps"] = steps, ["_MessageId"] = Guid.NewGuid(), ["_CreatedBy"] = 7L };
        await (Task)typeof(PipelineEngine).GetMethod("ExecuteSiblingStepsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, new object?[] { 1L, new List<PipelineStep> { create }, null, null, context, steps,
                new List<PipelineEngine.RawStepAuditSnapshot>(), "root", CancellationToken.None })!;

        return written ?? throw new Xunit.Sdk.XunitException("The Create Record step did not write a record.");
    }
}
