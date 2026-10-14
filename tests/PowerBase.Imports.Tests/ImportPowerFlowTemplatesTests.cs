using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>The list a PowerFlow picks an import from: choose an app, then a table (and optionally one import, or only file or table imports), and get what
/// fills that table. Filters combine with AND; the caller must be a member; what is asked is validated before any data is read.</summary>
public class ImportPowerFlowTemplatesTests
{
    private static readonly Guid AppPublicId = Guid.NewGuid();
    private static readonly Guid Students = Guid.NewGuid(), Courses = Guid.NewGuid(), Staff = Guid.NewGuid();
    private static readonly Guid ImportA = Guid.NewGuid(), ImportB = Guid.NewGuid(), ImportC = Guid.NewGuid();

    private readonly IAppRepository _apps = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IImportDefinitionRepository _definitions = Substitute.For<IImportDefinitionRepository>();
    private readonly ListPowerFlowImportTemplatesHandler _sut;

    public ImportPowerFlowTemplatesTests()
    {
        _sut = new ListPowerFlowImportTemplatesHandler(_apps, _tables, _access, _definitions);
        _apps.GetIdByPublicIdAsync(AppPublicId, Arg.Any<CancellationToken>()).Returns(7L);
        foreach (var (id, appId) in new[] { (Students, 7L), (Courses, 7L), (Staff, 8L) })
            _tables.GetByPublicIdAsync(id, Arg.Any<CancellationToken>()).Returns(new AppTable { AppId = appId, PublicId = id });

        // A (a file) fills Students; B (a table) fills Courses and, as a second table, Students; C (a file) fills Courses.
        _definitions.ListByAppAsync(7, Arg.Any<CancellationToken>()).Returns(
            [Item(ImportA, "A", Students, "Student", "file"), Item(ImportB, "B", Courses, "Course", "table"), Item(ImportC, "C", Courses, "Course", "file")]);
        _definitions.ListEntitiesByAppAsync(7, Arg.Any<CancellationToken>()).Returns([Entity(ImportA), Entity(ImportB, Students), Entity(ImportC)]);
    }

    private static ImportDefinitionListItem Item(Guid id, string name, Guid table, string tableName, string kind) =>
        new(id, name, "copy", Guid.Empty, "", 3, false, null, null, null, [], null, null, kind, 1, table, tableName);

    private static ImportDefinition Entity(Guid id, params Guid[] otherTables)
    {
        var entity = new ImportDefinition { PublicId = id };
        ImportConfigMapper.Apply(entity, new ImportDefinitionConfig
        {
            Name = "x", ImportType = ImportTypes.Copy,
            AdditionalTargets = otherTables.Select(t => new ImportTargetConfig { DestinationTableId = t, ImportType = ImportTypes.Copy }).ToList()
        });
        return entity;
    }

    private Task<IReadOnlyList<PowerFlowImportTemplate>> List(Guid? table = null, Guid? template = null, string? kind = null) =>
        _sut.HandleAsync(AppPublicId, table, template, kind, default);

    private static Guid[] Ids(IEnumerable<PowerFlowImportTemplate> list) => list.Select(t => t.TemplateId).ToArray();

    // ---- what is listed ----

    [Fact]
    public async Task With_no_filter_every_import_of_the_app_is_listed_with_the_tables_it_fills()
    {
        var all = await List();

        Ids(all).Should().Equal(ImportA, ImportB, ImportC);
        all.Single(t => t.TemplateId == ImportB).TableIds.Should().Equal(Courses, Students);
        all.Single(t => t.TemplateId == ImportB).TableId.Should().Be(Courses, "the first table is where it is saved");
        all[0].Should().Match<PowerFlowImportTemplate>(t => t.Name == "A" && t.SourceKind == "file" && t.TableName == "Student" && !t.NeedsAttention);
    }

    [Fact]
    public async Task A_table_lists_the_imports_that_fill_it_as_their_first_table_or_any_other()
    {
        Ids(await List(table: Students)).Should().Equal(ImportA, ImportB);
        Ids(await List(table: Courses)).Should().Equal(ImportB, ImportC);
    }

    [Fact]
    public async Task A_template_id_lists_that_import_only()
    {
        Ids(await List(template: ImportC)).Should().Equal(ImportC);
        (await List(template: Guid.NewGuid())).Should().BeEmpty("an id that is no import of the app tells nothing about other apps");
    }

    // ---- the source kind ----

    [Theory]
    [InlineData("file", new[] { 0, 2 })]
    [InlineData("table", new[] { 1 })]
    [InlineData("FILE", new[] { 0, 2 })]
    [InlineData("  Table  ", new[] { 1 })]
    public async Task The_source_kind_keeps_the_imports_that_read_a_file_or_a_table_whatever_its_case_or_spaces(string kind, int[] expected)
    {
        var all = new[] { ImportA, ImportB, ImportC };

        Ids(await List(kind: kind)).Should().Equal(expected.Select(i => all[i]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_source_kind_is_no_filter(string? kind) => Ids(await List(kind: kind)).Should().Equal(ImportA, ImportB, ImportC);

    [Theory]
    [InlineData("csv")]
    [InlineData("files")]
    [InlineData("file,table")]
    [InlineData("'; DROP TABLE x;--")]
    public async Task Any_other_source_kind_is_refused_before_anything_is_read(string kind)
    {
        var act = () => List(kind: kind);

        var error = await act.Should().ThrowAsync<ValidationException>();
        error.Which.Errors.Should().ContainKey("sourceKind");
        await _definitions.DidNotReceiveWithAnyArgs().ListByAppAsync(default, default);
    }

    [Fact]
    public async Task Every_filter_given_must_hold_at_once()
    {
        Ids(await List(table: Students, template: ImportB)).Should().Equal(ImportB);
        (await List(table: Students, template: ImportC)).Should().BeEmpty("C does not fill Students");
        Ids(await List(table: Courses, kind: "file")).Should().Equal(ImportC);
        Ids(await List(table: Students, kind: "table")).Should().Equal(ImportB);
        (await List(table: Students, template: ImportB, kind: "file")).Should().BeEmpty("B reads a table");
        Ids(await List(table: Courses, template: ImportC, kind: "FILE")).Should().Equal(ImportC);
    }

    // ---- what is refused ----

    [Fact]
    public async Task A_table_of_another_app_is_refused_not_returned_as_an_empty_list()
    {
        var act = () => List(table: Staff);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_empty_id_is_refused_for_each_id()
    {
        (await ((Func<Task>)(() => List(table: Guid.Empty))).Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("tableId");
        (await ((Func<Task>)(() => List(template: Guid.Empty))).Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("templateId");
        (await ((Func<Task>)(() => _sut.HandleAsync(Guid.Empty, null, null, null, default))).Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("appId");
    }

    [Fact]
    public async Task Only_a_member_of_the_app_may_ask_and_a_stranger_is_not_told_whether_the_filters_are_valid()
    {
        _access.RequireMembershipByAppPublicIdAsync(AppPublicId, Arg.Any<CancellationToken>()).Returns(Task.FromException(new UnauthorizedActionException("read the app")));

        var act = () => List(kind: "nonsense");

        await act.Should().ThrowAsync<UnauthorizedActionException>("who may ask is checked before what was asked");
        await _definitions.DidNotReceiveWithAnyArgs().ListByAppAsync(default, default);
        await _tables.DidNotReceiveWithAnyArgs().GetByPublicIdAsync(default, default);
    }

    [Fact]
    public async Task Nothing_is_rechecked_or_changed()
    {
        await List();

        await _definitions.DidNotReceiveWithAnyArgs().SetAttentionAsync(default, default, default, default);
        await _definitions.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        await _definitions.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    // ---- damaged or odd data ----

    [Fact]
    public async Task One_import_whose_saved_options_cannot_be_read_still_lists_with_its_first_table_and_does_not_take_the_list_down()
    {
        var damaged = new ImportDefinition { PublicId = ImportB, OptionsJson = "{ this is not json" };
        _definitions.ListEntitiesByAppAsync(7, Arg.Any<CancellationToken>()).Returns([Entity(ImportA), damaged, Entity(ImportC)]);

        var all = await List();

        Ids(all).Should().Equal(ImportA, ImportB, ImportC);
        all.Single(t => t.TemplateId == ImportB).TableIds.Should().Equal(Courses);
    }

    [Fact]
    public async Task An_import_with_no_saved_row_to_read_lists_with_its_first_table()
    {
        _definitions.ListEntitiesByAppAsync(7, Arg.Any<CancellationToken>()).Returns([]);

        (await List()).Should().HaveCount(3).And.OnlyContain(t => t.TableIds.Count == 1);
    }

    [Fact]
    public async Task The_same_table_named_twice_is_listed_once_and_an_empty_id_is_left_out()
    {
        _definitions.ListEntitiesByAppAsync(7, Arg.Any<CancellationToken>()).Returns([Entity(ImportA, Students, Guid.Empty, Courses, Courses), Entity(ImportB), Entity(ImportC)]);

        (await List()).Single(t => t.TemplateId == ImportA).TableIds.Should().Equal(Students, Courses);
    }

    [Fact]
    public async Task An_app_with_no_imports_is_an_empty_list_and_reads_nothing_more()
    {
        _definitions.ListByAppAsync(7, Arg.Any<CancellationToken>()).Returns([]);

        (await List()).Should().BeEmpty();
        await _definitions.DidNotReceiveWithAnyArgs().ListEntitiesByAppAsync(default, default);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_is_an_empty_list_and_reads_no_options()
    {
        (await List(kind: "table", template: ImportA)).Should().BeEmpty();
        await _definitions.DidNotReceiveWithAnyArgs().ListEntitiesByAppAsync(default, default);
    }
}
