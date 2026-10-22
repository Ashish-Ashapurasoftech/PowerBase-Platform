using System.Reflection;
using System.Runtime.CompilerServices;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Every data type a Create / Update Record or Add a Bulk Upsert Row step writes, given as the text a step maps (typed, or picked on
/// the Select tab). Only Multi-select (stored as a JSON array of its choices) and Check box (Yes / No accepted) changed; every other
/// type is written exactly as before.
/// </summary>
public class PipelineMappingValueAllTypesTests
{
    private static readonly PipelineEngine Engine = (PipelineEngine)RuntimeHelpers.GetUninitializedObject(typeof(PipelineEngine));

    private static object? Record(string typeCode, string value, string? settings = null) =>
        typeof(PipelineEngine).GetMethod("ParseRecordMappingValueWithFormat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Engine, new object?[] { value, new AppField { Id = 1, Fid = 6, Name = "Value", TypeCode = typeCode, Settings = settings }, value, null });

    private static object? Bulk(string typeCode, string value, string? settings = null) =>
        typeof(PipelineEngine).GetMethod("ParseBulkUpsertValueType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Engine, new object?[] { value, typeCode, settings });

    public static IEnumerable<object?[]> Cases() => new List<object?[]>
    {
        // type, mapped text, settings, written by Create / Update Record, written by Add a Bulk Upsert Row
        new object?[] { "Text", " Ronak ", null, "Ronak", "Ronak" },
        new object?[] { "TextMultiLine", "a\nb", null, "a\nb", "a\nb" },
        new object?[] { "RichText", "<p>x</p>", null, "<p>x</p>", "<p>x</p>" },
        new object?[] { "Email", "a@b.com", null, "a@b.com", "a@b.com" },
        new object?[] { "Phone", "+91 98765 43210", null, "+91 98765 43210", "+91 98765 43210" },
        new object?[] { "Url", "https://x.com", null, "https://x.com", "https://x.com" },
        new object?[] { "Address", "{\"city\":\"Rajkot\"}", null, "{\"city\":\"Rajkot\"}", "{\"city\":\"Rajkot\"}" },
        new object?[] { "File", "[{\"fileName\":\"a.pdf\"}]", null, "[{\"fileName\":\"a.pdf\"}]", "[{\"fileName\":\"a.pdf\"}]" },
        new object?[] { "SingleSelect", "Open", "{\"options\":[\"Open\",\"Closed\"]}", "Open", "Open" },
        new object?[] { "MultiSelect", "Red, Blue", "{\"options\":[\"Red\",\"Blue\"]}", "[\"Red\",\"Blue\"]", "[\"Red\",\"Blue\"]" },
        new object?[] { "MultiSelect", "[\"Red\"]", null, "[\"Red\"]", "[\"Red\"]" },
        new object?[] { "Number", "12.5", null, 12.5m, 12.5m },
        new object?[] { "Currency", "1000", null, 1000m, 1000m },
        new object?[] { "Percent", "0.25", null, 0.25m, 0.25m },
        new object?[] { "Rating", "4", null, 4m, 4m },
        new object?[] { "Duration", "90", null, 90m, 90m },
        new object?[] { "Boolean", "Yes", null, true, true },
        new object?[] { "Boolean", "No", null, false, false },
        new object?[] { "Boolean", "true", null, true, true },
        new object?[] { "Boolean", "0", null, false, false },
        new object?[] { "User", "40021", null, "40021", "40021" },
        new object?[] { "MultiUser", "40021,40022", null, "40021,40022", "40021,40022" },
        new object?[] { "Reference", "5", null, "5", "5" },
        new object?[] { "DateRange", "{\"start\":\"2026-10-01\",\"end\":\"2026-10-05\"}", null,
            "{\"start\":\"2026-10-01\",\"end\":\"2026-10-05\"}", "{\"start\":\"2026-10-01\",\"end\":\"2026-10-05\"}" },
        new object?[] { "NumericRange", "{\"start\":1,\"end\":2}", null, "{\"start\":1,\"end\":2}", "{\"start\":1,\"end\":2}" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryType_IsWrittenAsIntended(string typeCode, string mapped, string? settings, object? record, object? bulk)
    {
        Assert.Equal(record, Record(typeCode, mapped, settings));
        Assert.Equal(bulk, Bulk(typeCode, mapped, settings));
    }

    [Theory]
    [InlineData("Date", "2026-10-15")]
    [InlineData("DateTime", "2026-10-15T07:13:05")]
    public void Dates_AreWrittenAsDates(string typeCode, string mapped)
    {
        Assert.IsType<DateTime>(Record(typeCode, mapped));
        Assert.IsType<DateTime>(Bulk(typeCode, mapped));
    }

    [Fact]
    public void Time_IsWrittenAsTimeOfDay()
    {
        var written = Assert.IsType<DateTime>(Record("Time", "07:13"));
        Assert.Equal(new TimeSpan(7, 13, 0), written.TimeOfDay);
    }

    [Theory]
    [InlineData("Number", "abc")]
    [InlineData("Boolean", "maybe")]
    [InlineData("Date", "not a date")]
    public void AWrongValue_StillFailsWithAMappingError(string typeCode, string mapped)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Record(typeCode, mapped));
        Assert.IsType<PipelineMappingException>(error.InnerException);
    }

    [Theory]
    [InlineData("Number")]
    [InlineData("Boolean")]
    [InlineData("MultiSelect")]
    [InlineData("Date")]
    public void BulkUpsert_BlankNonTextValue_StaysNull(string typeCode)
    {
        Assert.Null(Bulk(typeCode, "  "));
    }
}
