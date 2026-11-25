using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class FieldValueTypesTests
{
    private static AppField Field(string typeCode, string? settings = null, int fid = 5, long tableId = 1) =>
        new() { Id = fid, Fid = fid, AppTableId = tableId, Name = "F", TypeCode = typeCode, Settings = settings };

    [Theory]
    [InlineData("Text", null, "Text")]
    [InlineData("Currency", null, "Currency")]
    [InlineData("Date", null, "Date")]
    [InlineData("Formula_Number", null, "Number")]
    [InlineData("Formula_Bool", null, "Boolean")]
    [InlineData("Formula_ListUser", null, "MultiUser")]
    [InlineData("Formula", """{"resultType":"Date"}""", "Date")]
    [InlineData("Formula", null, "Text")]
    [InlineData("ReportLink", null, null)]
    public void Of_StoredAndFormulaFields(string typeCode, string? settings, string? expected) =>
        FieldValueTypes.Of(Field(typeCode, settings)).Should().Be(expected);

    [Fact]
    public void ForNewReference_RecordIdKey_IsNumber()
    {
        FieldValueTypes.ForNewReference(null).Should().Be("Number");
        FieldValueTypes.ForNewReference(Field("Number", fid: 3)).Should().Be("Number");
    }

    [Theory]
    [InlineData("Text", null, "Text")]
    [InlineData("Currency", null, "Currency")]
    [InlineData("Date", null, "Date")]
    [InlineData("Formula_Text", null, "Text")]                                      // what the formula key produces
    [InlineData("Lookup", """{"resultTypeCode":"Percent"}""", "Percent")]           // a lookup key: what it shows
    public void ForNewReference_KeyField_IsTheKeysType(string typeCode, string? settings, string expected) =>
        FieldValueTypes.ForNewReference(Field(typeCode, settings, fid: 8)).Should().Be(expected);

    [Theory]
    [InlineData("Number", "Number")]
    [InlineData("Currency", "Currency")]
    [InlineData("Text", "Text")]
    [InlineData("MultiSelect", "MultiSelect")]
    [InlineData("DateTime", "DateTime")]
    [InlineData("Formula_Text", "Text")]
    [InlineData("Formula_DateTime", "DateTime")]
    public void ForConvertedReference_IsTheFieldsOwnType(string typeCode, string expected) =>
        FieldValueTypes.ForConvertedReference(Field(typeCode)).Should().Be(expected);

    [Fact]
    public void Of_Reference_IsUnchanged_SoLookupsOfItKeepTheirType() =>
        // A lookup of a Reference shows the parent's Record ID#, not the Reference's own saved type.
        FieldValueTypes.Of(Field("Reference", """{"resultTypeCode":"Text"}""")).Should().Be("Reference");

    [Theory]
    [InlineData("""{"function":"Count"}""", "Number")]
    [InlineData("""{"function":"DistinctCount","targetTypeCode":"Text"}""", "Number")]
    [InlineData("""{"function":"Exists"}""", "Boolean")]
    [InlineData("""{"function":"CombinedText","targetTypeCode":"Number"}""", "Text")]
    [InlineData("""{"function":"Sum","targetTypeCode":"Currency"}""", "Currency")]
    [InlineData("""{"function":"Min","targetTypeCode":"Formula_Date"}""", "Date")]
    [InlineData("""{"function":"Max","targetTypeCode":"Summary"}""", null)]            // needs the target field
    [InlineData("""{"function":"Min","resultTypeCode":"Percent"}""", "Percent")]      // saved wins
    public void Of_Summary(string settings, string? expected) =>
        FieldValueTypes.Of(Field("Summary", settings)).Should().Be(expected);

    [Theory]
    [InlineData("""{"sourceTypeCode":"Number"}""", "Number")]
    [InlineData("""{"sourceTypeCode":"Formula_Date"}""", "Date")]
    [InlineData("""{"sourceTypeCode":"Address","sourceSubField":"city"}""", "Text")]
    [InlineData("""{"sourceTypeCode":"Summary"}""", null)]                            // needs the source field
    [InlineData("""{"sourceTypeCode":"Summary","resultTypeCode":"Currency"}""", "Currency")]
    public void Of_Lookup(string settings, string? expected) =>
        FieldValueTypes.Of(Field("Lookup", settings)).Should().Be(expected);

    [Fact]
    public void OfTarget_LookupOfASummary_IsWhatTheSummaryProduces()
    {
        var lookup = Field("Lookup", """{"sourceTypeCode":"Summary"}""", fid: 31);
        var sources = new Dictionary<long, AppField> { [31] = Field("Summary", """{"function":"Min","targetTypeCode":"Currency"}""", fid: 8) };

        FieldValueTypes.OfTarget(lookup, sources).Should().Be("Currency");
    }

    [Theory]
    [InlineData("Max", "Date", null, "Date")]
    [InlineData("Avg", "Duration", null, "Duration")]
    [InlineData("Min", "Address", "city", "Text")]
    [InlineData("Count", null, null, "Number")]
    public void ForSummary(string function, string? targetValueType, string? subField, string expected) =>
        FieldValueTypes.ForSummary(function, targetValueType, subField).Should().Be(expected);

    [Fact]
    public async Task RefreshLookupsOf_SavesTheNewTypeOnEachLookupOfTheSummary_DownTheChain()
    {
        var relRepo = Substitute.For<IRelationshipRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        // Departments (1) › Employees (2) › Payslips (3). "Min of Salary" (Departments, fid 8) is pulled down by
        // Employees' lookup (fid 31), which Payslips pulls down in turn (fid 41). An unrelated lookup (fid 32) stays.
        relRepo.ListByParentTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<Relationship> { new() { Id = 1, ParentTableId = 1, ChildTableId = 2 } });
        relRepo.ListByParentTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<Relationship> { new() { Id = 2, ParentTableId = 2, ChildTableId = 3 } });
        relRepo.ListByParentTableAsync(3, Arg.Any<CancellationToken>()).Returns(new List<Relationship>());
        fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field("Lookup", """{"sourceTableId":1,"sourceFid":8,"sourceTypeCode":"Summary","resultTypeCode":"Currency"}""", fid: 31, tableId: 2),
            Field("Lookup", """{"sourceTableId":1,"sourceFid":9,"sourceTypeCode":"Text","resultTypeCode":"Text"}""", fid: 32, tableId: 2),
        });
        fieldRepo.ListByTableAsync(3, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field("Lookup", """{"sourceTableId":2,"sourceFid":31,"sourceTypeCode":"Summary","resultTypeCode":"Currency"}""", fid: 41, tableId: 3),
        });

        var updated = await FieldValueTypes.RefreshLookupsOfAsync(Field("Summary", null, fid: 8, tableId: 1), "Number", relRepo, fieldRepo, CancellationToken.None);

        updated.Should().Be(2);
        await fieldRepo.Received(1).UpdateSettingsAsync(31, Arg.Is<string>(s => s.Contains("\"resultTypeCode\":\"Number\"")), Arg.Any<CancellationToken>());
        await fieldRepo.Received(1).UpdateSettingsAsync(41, Arg.Is<string>(s => s.Contains("\"resultTypeCode\":\"Number\"")), Arg.Any<CancellationToken>());
        await fieldRepo.DidNotReceive().UpdateSettingsAsync(32, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
