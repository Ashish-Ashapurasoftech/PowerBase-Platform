using FluentAssertions;
using PowerBase.Application.Common;
using PowerBase.Domain.Constants;
using Xunit;

namespace PowerBase.UnitTests.Roles;

/// <summary>Per-table "View" is enforced only for a module's detail call made from that module's detail page (or with no
/// page header at all); lists and data pages are never blocked by it.</summary>
public class PageContextTests
{
    private const string App = "1bd09ae2-b69b-f111-8d46-010101010000";
    private const string Table = "a6f22315-b7e8-f111-8d59-70cf4932e2fd";
    private const string Item = "acf22315-b7e8-f111-8d59-70cf4932e2fd";

    [Fact]
    public void Report_definition_is_enforced_from_the_report_builder_only()
    {
        var api = $"/reports/{Item}";
        PageContext.EnforcesTableView(PermissionCodes.ReportsRead, api, $"/app/{App}/tables/{Table}/settings/reports/{Item}/edit").Should().BeTrue();
        PageContext.EnforcesTableView(PermissionCodes.ReportsRead, api, $"/app/{App}/tables/{Table}/reports/{Item}").Should().BeFalse(); // report view
        PageContext.EnforcesTableView(PermissionCodes.ReportsRead, api, null).Should().BeTrue(); // direct API call
    }

    [Fact]
    public void Lists_are_never_enforced()
    {
        var page = $"/app/{App}/tables/{Table}/settings/reports";
        PageContext.EnforcesTableView(PermissionCodes.ReportsRead, $"/tables/{Table}/reports", page).Should().BeFalse();
        PageContext.EnforcesTableView(PermissionCodes.ReportsRead, $"/tables/{Table}/reports", null).Should().BeFalse();
        PageContext.EnforcesTableView(PermissionCodes.FieldsRead, $"/tables/{Table}/fields", null).Should().BeFalse();
    }

    [Fact]
    public void Advanced_settings_only_from_the_advanced_page_or_directly()
    {
        var api = $"/tables/{Table}/advanced-settings";
        PageContext.EnforcesTableView(PermissionCodes.TablesRead, api, $"/app/{App}/tables/{Table}/settings/advanced").Should().BeTrue();
        PageContext.EnforcesTableView(PermissionCodes.TablesRead, api, $"/app/{App}/tables/{Table}/settings/fields").Should().BeFalse();
        PageContext.EnforcesTableView(PermissionCodes.TablesRead, api, null).Should().BeTrue();
    }

    [Fact]
    public void Field_detail_and_form_designer()
    {
        PageContext.EnforcesTableView(PermissionCodes.FieldsRead, $"/tables/{Table}/fields/{Item}",
            $"/app/{App}/tables/{Table}/settings/fields/{Item}").Should().BeTrue();
        PageContext.EnforcesTableView(PermissionCodes.FormsRead, $"/forms/{Item}/layout",
            $"/app/{App}/tables/{Table}/settings/forms/{Item}").Should().BeTrue();
        PageContext.EnforcesTableView(PermissionCodes.FormsRead, $"/forms/{Item}/layout",
            $"/app/{App}/tables/{Table}/records/new").Should().BeFalse(); // record form
    }

    [Fact]
    public void Non_view_codes_and_writes_are_not_decided_here()
    {
        PageContext.EnforcesTableView(PermissionCodes.ReportsUpdate, $"/reports/{Item}", null).Should().BeFalse();
        PageContext.EnforcesTableView(PermissionCodes.ReportsRead, null, null).Should().BeFalse(); // not a GET
    }
}
