using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The type a filter compares a field as — and so the operators it allows — for every field type, every Formula variant, a Lookup of
/// every type and every Summary calculation. The filter editor works it out on its own (effectiveFilterTypeCode / the category helpers
/// in pipeline-filter-helpers.ts); its spec (pipeline-filter-category-contract.spec.ts) holds this same table, so an operator the editor
/// offers is one the API accepts when the PowerFlow is saved, and compares the way the editor says.
/// </summary>
public class PipelineFilterCategoryContractTests
{
    public static readonly (string TypeCode, string? Settings, string Category)[] Contract =
    [
        ("Text", null, "TEXT"), ("TextMultiLine", null, "TEXT"), ("RichText", null, "TEXT"), ("SingleSelect", null, "TEXT"),
        ("MultiSelect", null, "TEXT"), ("Email", null, "TEXT"), ("Phone", null, "TEXT"), ("Url", null, "TEXT"), ("File", null, "TEXT"),
        ("Address", null, "TEXT"), ("User", null, "TEXT"), ("MultiUser", null, "TEXT"), ("Reference", null, "TEXT"),
        ("ReportLink", null, "TEXT"), ("ActionButton", null, "TEXT"), ("ActionButton_File", null, "TEXT"), ("ActionButton_Data", null, "TEXT"),
        ("Number", null, "NUMBER"), ("Currency", null, "NUMBER"), ("Percent", null, "NUMBER"), ("Rating", null, "NUMBER"),
        ("Duration", null, "NUMBER"), ("NumericRange", null, "NUMBER"),
        ("Date", null, "DATE"), ("DateTime", null, "DATE"), ("DateRange", null, "DATE"),
        ("Time", null, "TIME"),
        ("Boolean", null, "BOOLEAN"),

        ("Formula_Text", null, "TEXT"), ("Formula_RichText", null, "TEXT"), ("Formula_Email", null, "TEXT"), ("Formula_Phone", null, "TEXT"),
        ("Formula_Url", null, "TEXT"), ("Formula_User", null, "TEXT"), ("Formula_ListUser", null, "TEXT"), ("Formula_MultiSelect", null, "TEXT"),
        ("Formula_Number", null, "NUMBER"), ("Formula_Duration", null, "NUMBER"),
        ("Formula_Date", null, "DATE"), ("Formula_DateTime", null, "DATE"),
        ("Formula_Time", null, "TIME"),
        ("Formula_Bool", null, "BOOLEAN"),
        ("Formula", "{\"resultType\":\"Number\"}", "NUMBER"), ("Formula", "{\"resultType\":\"Duration\"}", "NUMBER"),
        ("Formula", "{\"resultType\":\"Date\"}", "DATE"), ("Formula", "{\"resultType\":\"DateTime\"}", "DATE"),
        ("Formula", "{\"resultType\":\"Time\"}", "TIME"), ("Formula", "{\"resultType\":\"Bool\"}", "BOOLEAN"),
        ("Formula", "{\"resultType\":\"Text\"}", "TEXT"), ("Formula", "{\"resultType\":\"User\"}", "TEXT"), ("Formula", null, "TEXT"),

        ("Lookup", "{\"sourceTypeCode\":\"Text\"}", "TEXT"), ("Lookup", "{\"sourceTypeCode\":\"MultiSelect\"}", "TEXT"),
        ("Lookup", "{\"sourceTypeCode\":\"Number\"}", "NUMBER"), ("Lookup", "{\"sourceTypeCode\":\"Currency\"}", "NUMBER"),
        ("Lookup", "{\"sourceTypeCode\":\"Percent\"}", "NUMBER"), ("Lookup", "{\"sourceTypeCode\":\"Rating\"}", "NUMBER"),
        ("Lookup", "{\"sourceTypeCode\":\"Duration\"}", "NUMBER"),
        ("Lookup", "{\"sourceTypeCode\":\"Date\"}", "DATE"), ("Lookup", "{\"sourceTypeCode\":\"DateTime\"}", "DATE"),
        ("Lookup", "{\"sourceTypeCode\":\"Time\"}", "TIME"), ("Lookup", "{\"sourceTypeCode\":\"Boolean\"}", "BOOLEAN"),
        ("Lookup", "{\"sourceTypeCode\":\"User\"}", "TEXT"), ("Lookup", "{\"sourceTypeCode\":\"MultiUser\"}", "TEXT"),
        ("Lookup", "{\"sourceTypeCode\":\"File\"}", "TEXT"), ("Lookup", "{\"sourceTypeCode\":\"Address\"}", "TEXT"),
        // A range, a reference, a report link: no single value a formula reads — compared as text.
        ("Lookup", "{\"sourceTypeCode\":\"DateRange\"}", "TEXT"), ("Lookup", "{\"sourceTypeCode\":\"NumericRange\"}", "TEXT"),
        ("Lookup", "{\"sourceTypeCode\":\"Reference\"}", "TEXT"), ("Lookup", "{\"sourceTypeCode\":\"ReportLink\"}", "TEXT"),
        ("Lookup", "{\"sourceTypeCode\":\"Formula_Number\"}", "NUMBER"), ("Lookup", "{\"sourceTypeCode\":\"Formula_Date\"}", "DATE"),
        ("Lookup", "{\"sourceTypeCode\":\"Formula_Bool\"}", "BOOLEAN"), ("Lookup", "{\"sourceTypeCode\":\"Formula_Time\"}", "TIME"),
        ("Lookup", null, "TEXT"),

        ("Summary", "{\"function\":\"Count\"}", "NUMBER"), ("Summary", "{\"function\":\"Sum\"}", "NUMBER"),
        ("Summary", "{\"function\":\"Avg\"}", "NUMBER"), ("Summary", "{\"function\":\"DistinctCount\"}", "NUMBER"),
        ("Summary", "{\"function\":\"sum\"}", "NUMBER"),
        ("Summary", "{\"function\":\"Exists\"}", "BOOLEAN"), ("Summary", "{\"function\":\"CombinedText\"}", "TEXT"),
        ("Summary", "{\"function\":\"Min\",\"targetTypeCode\":\"Number\"}", "NUMBER"),
        ("Summary", "{\"function\":\"Max\",\"targetTypeCode\":\"Date\"}", "DATE"),
        ("Summary", "{\"function\":\"Max\",\"targetTypeCode\":\"DateTime\"}", "DATE"),
        ("Summary", "{\"function\":\"Min\",\"targetTypeCode\":\"Time\"}", "TIME"),
        ("Summary", "{\"function\":\"Min\",\"targetTypeCode\":\"Text\"}", "TEXT"),
        ("Summary", "{\"function\":\"Max\",\"targetTypeCode\":\"DateRange\"}", "TEXT"),
        ("Summary", "{\"function\":\"Min\"}", "NUMBER"),
        ("Summary", "{\"function\":\"Median\"}", "NUMBER"),
        ("Summary", null, "NUMBER"),
    ];

    public static IEnumerable<object?[]> Rows() => Contract.Select(c => new object?[] { c.TypeCode, c.Settings, c.Category });

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryFieldType_ComparesAsTheContractSays(string typeCode, string? settings, string category)
    {
        var field = new AppField { Id = 1, Fid = 10, Name = "f", Label = "f", TypeCode = typeCode, Settings = settings };
        Assert.Equal(category, PipelineFilterEvaluator.GetTypeCategory(field));
    }

    [Fact]
    public void TheContract_CoversEveryFieldType()
    {
        string[] seeded =
        [
            "Text", "TextMultiLine", "RichText", "SingleSelect", "MultiSelect", "Number", "Currency", "Percent", "Rating", "Date", "DateTime",
            "Time", "Duration", "DateRange", "NumericRange", "Boolean", "Email", "Phone", "Url", "File", "Address", "User", "MultiUser",
            "Formula_Text", "Formula_Number", "Formula_Date", "Formula_DateTime", "Formula_Duration", "Formula_Bool", "Formula_User",
            "Formula_ListUser", "Formula_MultiSelect", "Formula_Time", "Formula_Phone", "Formula_Email", "Formula_RichText", "Formula_Url",
            "Formula", "Reference", "Lookup", "Summary", "ReportLink"
        ];
        Assert.Empty(seeded.Except(Contract.Select(c => c.TypeCode)));
    }
}
