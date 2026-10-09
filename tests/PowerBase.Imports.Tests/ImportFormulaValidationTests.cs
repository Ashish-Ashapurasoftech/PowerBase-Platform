using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Imports.Tests;

/// <summary>The live formula check on the import screen: a formula is compiled against the source (a table's fields, or a file's columns)
/// and the import's virtual columns, the way the import will compile it.</summary>
public class ImportFormulaValidationTests
{
    private static readonly Guid AppId = Guid.NewGuid();
    private static readonly Guid TableId = ImportHarness.SourceTableId;
    private const int V1 = ImportVirtual.FidBase;
    private const int V2 = ImportVirtual.FidBase + 1;

    private sealed class Rig
    {
        public IAppAccessService Access { get; } = Substitute.For<IAppAccessService>();
        public IAppRepository Apps { get; } = Substitute.For<IAppRepository>();
        public IAppTableRepository Tables { get; } = Substitute.For<IAppTableRepository>();
        public IAppFieldRepository Fields { get; } = Substitute.For<IAppFieldRepository>();
        public IRolePermissionEnforcer Enforcer { get; } = Substitute.For<IRolePermissionEnforcer>();
        public ValidateImportFormulaHandler Handler { get; }

        public Rig(long tableAppId = 1)
        {
            Apps.GetIdByPublicIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(1L);
            var table = new AppTable { Id = 10, AppId = tableAppId, PublicId = TableId, Name = "Src" };
            Tables.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(table);
            var fields = new List<AppField>
            {
                ImportHarness.RecordId(10), ImportHarness.Field(10, 6, "Name", "Text"), ImportHarness.Field(10, 8, "Qty", "Number"), ImportHarness.Field(10, 9, "Note", "Text")
            };
            Fields.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(fields);
            Enforcer.GetTableAccessAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).Returns(new TableAccessContext { Unrestricted = true });
            Handler = new ValidateImportFormulaHandler(Apps, Tables, Fields, Access, Enforcer, new FormulaEngine());
        }

        public Task<PowerBase.Application.Formulas.Queries.ValidateFormulaResult> Check(ImportFormulaCheck check) => Handler.HandleAsync(AppId, check, default);
    }

    private static ImportFormulaCheck Table(string expression, string? expected = "Any", List<ImportVirtualColumn>? virtuals = null, int? own = null) =>
        new(expression, expected, TableId, null, virtuals, own);

    private static ImportFormulaCheck File(string expression, List<ImportVirtualColumn>? virtuals = null, int? own = null) => new(expression, "Any", null,
        [new ImportFormulaColumn(1001, "First Name"), new ImportFormulaColumn(1002, "Last Name"), new ImportFormulaColumn(1003, "Index")], virtuals, own);

    private static ImportVirtualColumn Virtual(int fid, string name, string formula) => new() { Fid = fid, Name = name, Kind = ImportVirtual.Formula, Formula = formula };

    // ---- a table source ----

    [Fact]
    public async Task A_formula_over_the_source_tables_fields_is_valid()
    {
        var result = await new Rig().Check(Table("[Name] & \"-\" & [Note]"));

        result.Valid.Should().BeTrue();
        result.ResultType.Should().Be("Text");
    }

    [Fact]
    public async Task A_formula_using_a_field_the_table_does_not_have_says_so()
    {
        var result = await new Rig().Check(Table("[Nothing] & \"x\""));

        result.Valid.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_expected_result_type_is_checked_like_any_formula()
    {
        var result = await new Rig().Check(Table("[Name] & \"x\"", expected: "Number"));

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public async Task Someone_who_may_not_read_the_source_table_cannot_check_against_it()
    {
        var rig = new Rig();
        rig.Access.RequirePermissionByTablePublicIdAsync(TableId, PermissionCodes.RecordsRead, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("read the table"));

        var act = () => rig.Check(Table("[Name]"));

        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task A_table_of_another_app_is_not_found()
    {
        var act = () => new Rig(tableAppId: 2).Check(Table("[Name]"));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Fields_a_role_hides_are_not_available_to_the_formula()
    {
        var rig = new Rig();
        var table = new AppTable { Id = 10, AppId = 1, PublicId = TableId, Name = "Src" };
        rig.Enforcer.GetTableAccessAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new TableAccessContext
        {
            Unrestricted = false, VisibleFields = [ImportHarness.Field(10, 6, "Name", "Text")]
        });

        (await rig.Check(Table("[Name]"))).Valid.Should().BeTrue();
        (await rig.Check(Table("[Note]"))).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task Someone_outside_the_app_cannot_use_the_check()
    {
        var rig = new Rig();
        rig.Access.RequireMembershipByAppPublicIdAsync(AppId, Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new UnauthorizedActionException("see this app"));

        var act = () => rig.Check(Table("[Name]"));

        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }

    // ---- a file source ----

    [Fact]
    public async Task A_formula_over_a_files_columns_is_valid()
    {
        var result = await new Rig().Check(File("[First Name] & \" \" & [Last Name]"));

        result.Valid.Should().BeTrue();
        result.ResultType.Should().Be("Text");
    }

    [Fact]
    public async Task A_formula_using_a_column_the_file_does_not_have_says_so()
    {
        (await new Rig().Check(File("[Middle Name]"))).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task A_files_values_are_text_so_arithmetic_on_them_is_refused_like_the_import_would()
    {
        (await new Rig().Check(File("[Index] * 2"))).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_read_from_the_server_for_a_file()
    {
        var rig = new Rig();

        await rig.Check(File("[First Name]"));

        await rig.Fields.DidNotReceiveWithAnyArgs().ListByTableAsync(default, default);
        await rig.Access.DidNotReceiveWithAnyArgs().RequirePermissionByTablePublicIdAsync(default, default!, default);
    }

    [Fact]
    public async Task Column_ids_that_are_not_a_files_are_ignored()
    {
        var check = new ImportFormulaCheck("[Sneaky]", "Any", null, [new ImportFormulaColumn(3, "Sneaky"), new ImportFormulaColumn(ImportVirtual.FidBase + 4, "Sneaky")], null, null);

        (await new Rig().Check(check)).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task A_file_with_absurdly_many_columns_is_refused()
    {
        var columns = Enumerable.Range(1, ValidateImportFormulaHandler.MaxFileColumns + 1).Select(i => new ImportFormulaColumn(1000 + i, "C" + i)).ToList();

        var act = () => new Rig().Check(new ImportFormulaCheck("[C1]", "Any", null, columns, null, null));

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ---- virtual columns ----

    [Fact]
    public async Task A_formula_can_use_a_virtual_column_for_a_file_and_for_a_table()
    {
        var virtuals = new List<ImportVirtualColumn> { Virtual(V1, "Full", "[First Name] & [Last Name]") };

        (await new Rig().Check(File("[Full] & [Index]", virtuals))).Valid.Should().BeTrue();
        (await new Rig().Check(Table("[Name] & [Tag]", virtuals: [new ImportVirtualColumn { Fid = V1, Name = "Tag", Kind = ImportVirtual.Fixed, StaticValue = "t" }]))).Valid.Should().BeTrue();
    }

    [Fact]
    public async Task A_virtual_column_can_use_one_that_uses_another()
    {
        var virtuals = new List<ImportVirtualColumn>
        {
            Virtual(V2, "Code", "[Full] & [Index]"), Virtual(V1, "Full", "[First Name] & [Last Name]"),
        };

        (await new Rig().Check(File("[Code] & \"!\"", virtuals))).Valid.Should().BeTrue();
    }

    [Fact]
    public async Task A_virtual_column_cannot_use_itself()
    {
        var virtuals = new List<ImportVirtualColumn> { Virtual(V1, "Full", "[Full]") };

        (await new Rig().Check(File("[Full] & \"x\"", virtuals, own: V1))).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task A_virtual_column_cannot_use_one_that_uses_it()
    {
        var virtuals = new List<ImportVirtualColumn> { Virtual(V1, "A", "[B]"), Virtual(V2, "B", "[A]") };

        (await new Rig().Check(File("[B]", virtuals, own: V1))).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task A_broken_virtual_column_is_not_offered_but_does_not_break_the_check()
    {
        var virtuals = new List<ImportVirtualColumn> { Virtual(V1, "Broken", "[Nothing]") };

        (await new Rig().Check(File("[First Name]", virtuals))).Valid.Should().BeTrue();
        (await new Rig().Check(File("[Broken]", virtuals))).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task A_virtual_columns_formula_must_return_something_a_virtual_column_can_hold()
    {
        var rig = new Rig();

        (await rig.Check(File("Today()", own: V1))).Valid.Should().BeTrue();
        (await rig.Check(File("[First Name] & \"x\"", own: V1))).ResultType.Should().Be("Text");
    }

    // ---- limits ----

    [Fact]
    public async Task A_formula_that_is_too_long_or_too_deep_is_refused_with_a_message()
    {
        var rig = new Rig();

        var tooLong = await rig.Check(File(new string('1', ImportPlanBuilder.MaxFormulaLength + 1)));
        var tooDeep = await rig.Check(File(new string('(', ImportFormulaGuard.MaxNesting + 5) + "1" + new string(')', ImportFormulaGuard.MaxNesting + 5)));

        tooLong.Valid.Should().BeFalse();
        tooLong.Diagnostics.Single().Message.Should().Contain("too long");
        tooDeep.Valid.Should().BeFalse();
        tooDeep.Diagnostics.Single().Message.Should().Contain("nested too deeply");
    }

    [Fact]
    public async Task An_empty_formula_is_not_a_crash()
    {
        var result = await new Rig().Check(File(""));

        result.Diagnostics.Should().NotBeNull();
    }
}
