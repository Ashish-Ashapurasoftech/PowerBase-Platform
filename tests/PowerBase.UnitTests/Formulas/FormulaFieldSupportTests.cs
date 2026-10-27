using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Formulas;

/// <summary>File and Address fields in formulas, Hour of a Time, multi-branch If and UserRoles() through the real projector.</summary>
public class FormulaFieldSupportTests
{
    private const string AddressJson = """{"street1":"1 Main St","street2":"","city":"Albany","state":"NY","zip":"12207","country":"USA"}""";
    private const string FilesJson = """[{"name":"timesheet.pdf","url":"x"},{"name":"notes.txt","url":"y"}]""";

    private static FormulaProjector NewProjector(IAppUserRepository? appUsers = null, IAppRoleRepository? appRoles = null, long userId = 0)
    {
        var qc = Substitute.For<IQueryContext>();
        qc.UserId.Returns(userId);
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns(string.Empty);
        return new FormulaProjector(
            new FormulaEngine(), qc, runtime,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(),
            Substitute.For<IAppRepository>(), appUserRepo: appUsers, appRoleRepo: appRoles);
    }

    private static AppField Field(int fid, string name, string typeCode, string? settings = null) => new()
    {
        Id = fid, Fid = fid, Name = name, Label = name, TypeCode = typeCode, Settings = settings,
    };

    private static AppField Formula(int fid, string name, string resultType, string expression) =>
        Field(fid, name, "Formula", $"{{\"resultType\":\"{resultType}\",\"expression\":{System.Text.Json.JsonSerializer.Serialize(expression)}}}");

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Row(params (int Fid, object? Value)[] values) =>
        [values.ToDictionary(v => PhysicalNaming.ColumnName(v.Fid), v => v.Value)];

    [Fact]
    public void FileField_ReadsAsItsFileNames()
    {
        var fields = new List<AppField> { Field(1, "File Attachment", "File"), Formula(2, "Name", "Text", "[File Attachment]") };

        var result = NewProjector().Project(fields, Row((1, FilesJson)));

        result[0][2].Should().Be("timesheet.pdf, notes.txt");
    }

    [Fact]
    public void FileField_TestedForPresence_LikeQuickbase()
    {
        var fields = new List<AppField>
        {
            Field(1, "File Attachment", "File"),
            Formula(2, "Has File", "Text", "If(ToText([File Attachment]) != \"\", \"yes\", \"no\")"),
        };

        NewProjector().Project(fields, Row((1, FilesJson)))[0][2].Should().Be("yes");
        NewProjector().Project(fields, Row((1, null)))[0][2].Should().Be("no");
    }

    [Fact]
    public void AddressField_ReadsWholeOrByPart()
    {
        var fields = new List<AppField>
        {
            Field(1, "Address", "Address"),
            Formula(2, "Whole", "Text", "[Address]"),
            Formula(3, "City", "Text", "[Address : City]"),
            Formula(4, "Line", "Text", "[Address : Street 1] & \", \" & [Address : Postal Code]"),
        };

        var result = NewProjector().Project(fields, Row((1, AddressJson)))[0];

        result[2].Should().Be("1 Main St, Albany, NY, 12207, USA");
        result[3].Should().Be("Albany");
        result[4].Should().Be("1 Main St, 12207");
    }

    [Fact]
    public void Hour_OfATimeField_Works()
    {
        var fields = new List<AppField> { Field(1, "Clock In", "Time"), Formula(2, "H", "Number", "Hour([Clock In])") };

        NewProjector().Project(fields, Row((1, "14:45:00")))[0][2].Should().Be(14m);
    }

    [Fact]
    public void MultiBranchIf_PicksTheFirstTrueCondition()
    {
        var fields = new List<AppField>
        {
            Field(1, "Qty", "Number"),
            Formula(2, "Size", "Text", "If([Qty] < 10, \"small\", [Qty] < 100, \"medium\", \"large\")"),
        };

        NewProjector().Project(fields, Row((1, 5m)))[0][2].Should().Be("small");
        NewProjector().Project(fields, Row((1, 50m)))[0][2].Should().Be("medium");
        NewProjector().Project(fields, Row((1, 500m)))[0][2].Should().Be("large");
    }

    [Fact]
    public void UserRoles_ReadsTheCurrentUsersRolesInTheTablesApp()
    {
        var appUsers = Substitute.For<IAppUserRepository>();
        appUsers.GetUserAppRoleIdsAsync(77, 5, Arg.Any<CancellationToken>()).Returns(new List<long> { 12 });
        var roleId = Guid.NewGuid();
        var appRoles = Substitute.For<IAppRoleRepository>();
        appRoles.ListDetailsByAppIdAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppRoleDetail>
        {
            new(11, Guid.NewGuid(), 77, "Viewer", false, false, [], "None", null, []),
            new(12, roleId, 77, "Service Worker", false, false, [], "None", null, []),
        });
        var fields = new List<AppField>
        {
            Formula(1, "Status", "Text", "If(ToText(UserRoles(\"Name\")) = \"Service Worker\", \"Completed By Tech\", \"Closed\")"),
            Formula(2, "RoleId", "Text", "ToText(UserRoles(\"ID\"))"),
        };
        var table = new AppTable { Id = 3, AppId = 77, PublicId = Guid.NewGuid() };

        var result = NewProjector(appUsers, appRoles, userId: 5).Project(fields, Row((9, null)), null, table);

        result[0][1].Should().Be("Completed By Tech");
        result[0][2].Should().Be(roleId.ToString());
    }

    [Fact]
    public void AppFieldSchema_KnowsAnAddressFieldAndAFileField()
    {
        var schema = new AppFieldSchema([Field(1, "Address", "Address"), Field(2, "File Attachment", "File")]);

        schema.TryResolve("Address", out var address).Should().BeTrue();
        address.IsAddress.Should().BeTrue();
        schema.TryResolve("File Attachment", out var file).Should().BeTrue();
        file.IsAddress.Should().BeFalse();
    }
}
