using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbCommonTests
{
    [Theory]
    [InlineData("Text", "text", "", "text")]
    [InlineData("TextMultiLine", "text-multi-line", "", "text")]
    [InlineData("RichText", "rich-text", "", "text")]
    [InlineData("SingleSelect", "text-multiple-choice", "", "text")]
    [InlineData("MultiSelect", "multitext", "", "text")]
    [InlineData("Number", "numeric", "", "float")]
    [InlineData("Currency", "currency", "", "float")]
    [InlineData("Percent", "percent", "", "float")]
    [InlineData("Rating", "rating", "", "float")]
    [InlineData("Date", "date", "", "int64")]
    [InlineData("DateTime", "timestamp", "", "int64")]
    [InlineData("Time", "timeofday", "", "int64")]
    [InlineData("Duration", "duration", "", "int64")]
    [InlineData("Boolean", "checkbox", "", "bool")]
    [InlineData("Email", "email", "", "text")]
    [InlineData("Phone", "phone", "", "text")]
    [InlineData("Url", "url", "", "text")]
    [InlineData("File", "file", "", "text")]
    [InlineData("Address", "address", "", "text")]
    [InlineData("User", "user", "", "text")]
    [InlineData("MultiUser", "multiuser", "", "text")]
    [InlineData("RecordId", "recordid", "", "int64")]
    [InlineData("Formula_Number", "numeric", "formula", "float")]
    [InlineData("Formula_Bool", "checkbox", "formula", "bool")]
    [InlineData("Formula_DateTime", "timestamp", "formula", "int64")]
    [InlineData("ReportLink", "dblink", "", "text")]
    [InlineData("SomethingUnknown", "text", "", "text")]
    public void Map_TypeCode_ReturnsQuickbaseType(string typeCode, string fieldType, string mode, string baseType)
    {
        var result = QbFieldTypeMap.Map(typeCode);
        result.Should().Be(new QbFieldType(fieldType, mode, baseType));
    }

    [Fact]
    public void Map_LookupWithResultType_UsesResultTypeAndLookupMode()
    {
        QbFieldTypeMap.Map("Lookup", "Currency").Should().Be(new QbFieldType("currency", "lookup", "float"));
    }

    [Fact]
    public void Map_SummaryWithoutResultType_DefaultsToNumeric()
    {
        QbFieldTypeMap.Map("Summary").Should().Be(new QbFieldType("numeric", "summary", "float"));
    }

    [Theory]
    [InlineData("demo", "demo", true)]
    [InlineData("demo.quickbase.com", "demo", true)]
    [InlineData("DEMO.Quickbase.com", "demo", true)]
    [InlineData(" demo ", "demo", true)]
    [InlineData("other.quickbase.com", "demo", false)]
    [InlineData("demo2.quickbase.com", "demo", false)]
    [InlineData("demo", "", false)]
    public void RealmMatches_Header_ComparedWithTenantSlug(string realm, string slug, bool expected)
    {
        QbRealmHostnameAttribute.RealmMatches(realm, slug).Should().Be(expected);
    }

    [Fact]
    public void ParseGuid_Valid_ReturnsGuid()
    {
        var id = Guid.NewGuid();
        QbIds.ParseGuid(id.ToString(), "tableId").Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bck7gp3q2")]
    public void ParseGuid_MissingOrInvalid_Throws400(string? value)
    {
        var act = () => QbIds.ParseGuid(value, "tableId");
        act.Should().Throw<QbApiException>().Which.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData("6", 6)]
    [InlineData(" 12 ", 12)]
    public void ParseFid_Valid_ReturnsInt(string value, int expected)
    {
        QbIds.ParseFid(value, "mergeFieldId").Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void ParseFid_Invalid_Throws400(string value)
    {
        var act = () => QbIds.ParseFid(value, "mergeFieldId");
        act.Should().Throw<QbApiException>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void FromException_NotFound_Returns404QuickbaseBody()
    {
        var result = QbErrorBody.FromException(new NotFoundException("Report", 5));
        result.StatusCode.Should().Be(404);
        result.Value.Should().Be(new QbErrorBody("Item not found", "Report '5' was not found."));
    }

    [Fact]
    public void FromException_UnauthorizedAction_Returns403AccessDenied()
    {
        var result = QbErrorBody.FromException(new UnauthorizedActionException("view"));
        result.StatusCode.Should().Be(403);
        ((QbErrorBody)result.Value!).Message.Should().Be("Access denied");
    }

    [Fact]
    public void FromException_QbApiException_KeepsStatusAndText()
    {
        var result = QbErrorBody.FromException(QbApiException.BadRequest("Required header 'x' not specified"));
        result.StatusCode.Should().Be(400);
        result.Value.Should().Be(new QbErrorBody("Bad Request", "Required header 'x' not specified"));
    }

    [Fact]
    public void FromException_Unexpected_Returns500WithoutInternalDetails()
    {
        var result = QbErrorBody.FromException(new InvalidCastException("secret internals"));
        result.StatusCode.Should().Be(500);
        result.Value.Should().Be(new QbErrorBody("Internal server error", ""));
    }
}
