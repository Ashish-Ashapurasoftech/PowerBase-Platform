using FluentAssertions;
using PowerBase.API.QuickbaseApi.Xml;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbXmlPhase45Tests
{
    private static AppRoleDetail Role(string name, params string[] perms) =>
        new(1, Guid.NewGuid(), 1, name, false, false, perms, "All", null, Array.Empty<Guid>());

    [Fact]
    public void AccessLevel_MapsRolesToQuickbaseAccess()
    {
        QbXmlUserActions.AccessLevel(Role("Administrator")).Should().Be((1, "Administrator"));
        QbXmlUserActions.AccessLevel(Role("Manager", "roles:manage")).Should().Be((1, "Administrator"));
        QbXmlUserActions.AccessLevel(Role("Lead", "users:invite")).Should().Be((2, "Basic Access with Sharing"));
        QbXmlUserActions.AccessLevel(Role("Viewer")).Should().Be((3, "Basic Access"));
        QbXmlUserActions.AccessLevel(null).Should().Be((3, "Basic Access"));
    }

    [Fact]
    public void Csv_QuotedCommasEscapesAndLineEnds()
    {
        var rows = QbCsv.Parse("First Name,Last Name\r\n\"Doe, Jr\",\"Say \"\"hi\"\"\"\nA,B\n");
        rows.Should().HaveCount(3);
        rows[1].Should().Equal("Doe, Jr", "Say \"hi\"");
        rows[2].Should().Equal("A", "B");
    }

    [Fact]
    public void Csv_EmptyLinesAreSkipped()
    {
        QbCsv.Parse("a,b\n\n\nc,d").Should().HaveCount(2);
    }

    [Fact]
    public void ImportStatusText_LikeQuickbaseSample()
    {
        var s = new ImportRunStatusResponse(Guid.NewGuid(), "success", 100, 3, 3, 0, 0, 0, null, null, null, false);
        QbXmlImportActions.ImportStatusText(s).Should().Be("3 new records were created.");
        var s2 = s with { Inserted = 1, Updated = 2 };
        QbXmlImportActions.ImportStatusText(s2).Should().Be("1 new record was created. 2 records were updated.");
    }

    [Fact]
    public void RedirectPage_EncodesUrl()
    {
        var html = QbXmlImportActions.RedirectPage("http://x/app/1/tables/2/records/new?prefill_6=a&prefill_7=<b>");
        html.Should().Contain("http-equiv=\"refresh\"");
        html.Should().Contain("prefill_6=a&amp;prefill_7=&lt;b&gt;");
    }
}
