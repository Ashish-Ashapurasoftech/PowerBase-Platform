using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Formulas;

public class FormulaProjectorTests
{
    private static FormulaProjector NewProjector()
    {
        var qc = Substitute.For<IQueryContext>();
        qc.UserId.Returns(0L);
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns(string.Empty);
        return new FormulaProjector(
            new FormulaEngine(), qc, runtime,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(),
            Substitute.For<IAppRepository>());
    }

    private static AppField Field(int fid, string name, string typeCode, string? settings = null) => new()
    {
        Id = fid,
        Fid = fid,
        Name = name,
        TypeCode = typeCode,
        Settings = settings,
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows) =>
        rows.Cast<IReadOnlyDictionary<string, object?>>().ToList();

    [Fact]
    public void Projects_numeric_formula_over_field_values()
    {
        var fields = new List<AppField>
        {
            Field(1, "Qty", "Number"),
            Field(2, "Total", "Formula", "{\"resultType\":\"Number\",\"expression\":\"[Qty] * 2\"}"),
        };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 10m });

        var result = NewProjector().Project(fields, rows);

        result.Should().HaveCount(1);
        result[0][2].Should().Be(20m);
    }

    [Fact]
    public void Text_formula_concatenates_fields()
    {
        var fields = new List<AppField>
        {
            Field(1, "First", "Text"),
            Field(2, "Last", "Text"),
            Field(3, "Full", "Formula", "{\"resultType\":\"Text\",\"expression\":\"[First] & [Last]\"}"),
        };
        var rows = Rows(new Dictionary<string, object?>
        {
            [PhysicalNaming.ColumnName(1)] = "Ada",
            [PhysicalNaming.ColumnName(2)] = "Lovelace",
        });

        NewProjector().Project(fields, rows)[0][3].Should().Be("AdaLovelace");
    }

    [Fact]
    public void UserToName_and_UserToEmail_resolve_a_user_field_from_the_directory_in_one_query()
    {
        var qc = Substitute.For<IQueryContext>();
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns(string.Empty);
        var users = Substitute.For<IUserRepository>();
        users.GetIdentitiesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, UserIdentity> { [5] = new("Hasim", "hasim@x.com"), [6] = new("Ada", "ada@x.com") });
        var sut = new FormulaProjector(
            new FormulaEngine(), qc, runtime,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(),
            Substitute.For<IAppRepository>(), userRepo: users);

        var fields = new List<AppField>
        {
            Field(1, "Owner", "User"),
            Field(2, "Who", "Formula", "{\"resultType\":\"Text\",\"expression\":\"UserToName([Owner]) & \\\" <\\\" & UserToEmail([Owner]) & \\\">\\\"\"}"),
        };
        var rows = Rows(
            new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 5L },
            new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 6L },
            new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 5L });

        var result = sut.Project(fields, rows);

        result[0][2].Should().Be("Hasim <hasim@x.com>");
        result[1][2].Should().Be("Ada <ada@x.com>");
        result[2][2].Should().Be("Hasim <hasim@x.com>");
        users.Received(1).GetIdentitiesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>());
    }

    // ── Structured stored values: how the record grid stores them vs what a formula must see ──────

    [Fact]
    public void Phone_field_reads_as_the_number_not_its_stored_json()
    {
        var fields = new List<AppField>
        {
            Field(1, "Mobile", "Phone"),
            Field(2, "Dial", "Formula", "{\"resultType\":\"Text\",\"expression\":\"[Mobile]\"}"),
        };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = "{\"number\":\"+919825012345\",\"ext\":\"12\"}" });

        NewProjector().Project(fields, rows)[0][2].Should().Be("+919825012345");
    }

    [Fact]
    public void MultiSelect_field_reads_as_its_choices_not_the_stored_json_array()
    {
        var fields = new List<AppField>
        {
            Field(1, "Tags", "MultiSelect"),
            Field(2, "Shown", "Formula", "{\"resultType\":\"Text\",\"expression\":\"[Tags]\"}"),
        };
        var rows = Rows(
            new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = "[\"Red\",\"Blue\"]" },
            new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = "Red, Blue" });   // older comma-separated rows

        var result = NewProjector().Project(fields, rows);

        result[0][2].Should().Be("Red, Blue");
        result[1][2].Should().Be("Red, Blue");
    }

    [Fact]
    public void MultiUser_field_stored_as_a_json_id_array_reads_as_those_users()
    {
        var fields = new List<AppField>
        {
            Field(1, "Team", "MultiUser"),
            Field(2, "Members", "Formula_ListUser", "{\"expression\":\"[Team]\"}"),
            Field(3, "HowMany", "Formula", "{\"resultType\":\"Number\",\"expression\":\"Count([Team])\"}"),
        };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = "[11,22]" });

        var result = NewProjector().Project(fields, rows);

        result[0][2].Should().BeAssignableTo<IEnumerable<string>>().Which.Should().Equal("11", "22");
        result[0][3].Should().Be(2m);
    }

    [Fact]
    public void Returns_empty_maps_when_no_formula_fields()
    {
        var fields = new List<AppField> { Field(1, "Qty", "Number") };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 5m });

        NewProjector().Project(fields, rows)[0].Should().BeEmpty();
    }

    [Fact]
    public void Invalid_expression_yields_null_not_exception()
    {
        var fields = new List<AppField>
        {
            Field(1, "Total", "Formula", "{\"resultType\":\"Number\",\"expression\":\"[Missing] + 1\"}"),
        };
        var rows = Rows(new Dictionary<string, object?>());

        NewProjector().Project(fields, rows)[0][1].Should().BeNull();
    }

    [Fact]
    public void Formula_MultiSelect_projects_a_text_list()
    {
        var fields = new List<AppField>
        {
            Field(1, "Tags", "Text"),
            Field(2, "TagList", "Formula_MultiSelect", "{\"expression\":\"Split([Tags], \\\",\\\")\"}"),
        };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = "red,green,blue" });

        var result = NewProjector().Project(fields, rows);

        result[0][2].Should().BeAssignableTo<IEnumerable<string>>().Which.Should().Equal("red", "green", "blue");
    }

    [Fact]
    public void Formula_ListUser_projects_a_list_of_user_ids()
    {
        var fields = new List<AppField>
        {
            Field(1, "Team", "Text"),
            Field(2, "Members", "Formula_ListUser", "{\"expression\":\"ToUserList([Team])\"}"),
        };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = "11,22" });

        var result = NewProjector().Project(fields, rows);

        result[0][2].Should().BeAssignableTo<IEnumerable<string>>().Which.Should().Equal("11", "22");
    }

    [Fact]
    public void Formula_MultiSelect_with_a_scalar_expression_projects_null_not_a_wrong_type()
    {
        var fields = new List<AppField>
        {
            Field(1, "Name", "Text"),
            Field(2, "Bad", "Formula_MultiSelect", "{\"expression\":\"[Name]\"}"),
        };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = "x" });

        var result = NewProjector().Project(fields, rows);

        result[0][2].Should().NotBe("x");
    }
}
