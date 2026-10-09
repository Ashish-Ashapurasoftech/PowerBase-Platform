using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Imports.Tests;

/// <summary>Finding imports broken by changes to the tables they use, and clearing them when the cause is fixed.</summary>
public class ImportDefinitionCheckerTests
{
    private const long SourceId = 10, DestId = 11;
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IImportDefinitionRepository _definitions = Substitute.For<IImportDefinitionRepository>();
    private readonly ImportDefinitionChecker _sut;
    private List<AppField> _source;
    private List<AppField> _dest;

    public ImportDefinitionCheckerTests()
    {
        _source = [ImportHarness.RecordId(SourceId), ImportHarness.Field(SourceId, 6, "Name", "Text"), ImportHarness.Field(SourceId, 8, "Qty", "Text")];
        _dest = [ImportHarness.RecordId(DestId), ImportHarness.Field(DestId, 6, "Name", "Text", unique: true), ImportHarness.Field(DestId, 7, "Qty", "Number")];
        _tables.GetByIdAsync(SourceId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = SourceId, PublicId = ImportHarness.SourceTableId });
        _tables.GetByIdAsync(DestId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = DestId, PublicId = ImportHarness.DestTableId });
        _fields.ListByTableAsync(SourceId, Arg.Any<CancellationToken>()).Returns(_ => _source);
        _fields.ListByTableAsync(DestId, Arg.Any<CancellationToken>()).Returns(_ => _dest);
        _sut = new ImportDefinitionChecker(_tables, _fields, new FormulaEngine(), _definitions);
    }

    private static ImportDefinition Def(Action<ImportDefinition>? tweak = null, params ImportFieldMapping[] mappings)
    {
        var d = new ImportDefinition
        {
            Id = 1, PublicId = Guid.NewGuid(), SourceTableId = SourceId, DestinationTableId = DestId, ImportType = "copy",
            FieldMappingJson = ImportJson.Serialize(mappings.Length > 0 ? mappings.ToList()
                : [new ImportFieldMapping { DestFid = 6, SourceFid = 6 }, new ImportFieldMapping { DestFid = 7, SourceFid = 8 }])
        };
        tweak?.Invoke(d);
        return d;
    }

    private async Task<ImportDefinition> Check(ImportDefinition def)
    {
        await _sut.RefreshAsync(def, default);
        return def;
    }

    [Fact]
    public async Task A_sound_import_is_not_flagged_and_nothing_is_written()
    {
        var def = await Check(Def());

        def.NeedsAttention.Should().BeFalse();
        await _definitions.DidNotReceiveWithAnyArgs().SetAttentionAsync(default, default, default, default);
    }

    [Fact]
    public async Task A_deleted_destination_field_flags_the_import_with_a_reason_and_stores_it()
    {
        _dest.RemoveAll(f => f.Fid == 7);

        var def = await Check(Def());

        def.NeedsAttention.Should().BeTrue();
        def.AttentionReason.Should().Contain("deleted");
        await _definitions.Received(1).SetAttentionAsync(1, true, def.AttentionReason, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_field_marked_deleted_counts_as_gone()
    {
        _source.Single(f => f.Fid == 8).IsDeleted = true;

        (await Check(Def())).AttentionReason.Should().Contain("source field");
    }

    [Fact]
    public async Task A_deleted_source_field_flags_the_import()
    {
        _source.RemoveAll(f => f.Fid == 6);

        var def = await Check(Def());

        def.NeedsAttention.Should().BeTrue();
        def.AttentionReason.Should().Contain("'Name'").And.Contain("source field");
    }

    [Fact]
    public async Task A_type_change_that_makes_a_mapping_unsafe_flags_the_import()
    {
        var source = _source.Single(f => f.Fid == 8);
        var dest = _dest.Single(f => f.Fid == 7);
        source.TypeCode = "Checkbox";
        dest.TypeCode = "Date";
        ImportTypeCompatibility.CheckMapping(source, dest).Should().NotBeNull("the test needs a pair the import refuses");

        (await Check(Def())).NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public async Task A_do_not_import_mapping_to_a_deleted_field_is_ignored()
    {
        _dest.RemoveAll(f => f.Fid == 7);
        var def = Def(null, new ImportFieldMapping { DestFid = 6, SourceFid = 6 }, new ImportFieldMapping { DestFid = 7, SourceFid = 8, DoNotImport = true });

        (await Check(def)).NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public async Task A_merge_key_that_is_deleted_or_no_longer_unique_flags_the_import()
    {
        var merge = (Action<ImportDefinition>)(d => { d.ImportType = "merge"; d.MergeKeyFid = 6; });
        _dest.Single(f => f.Fid == 6).IsUnique = false;
        (await Check(Def(merge))).AttentionReason.Should().Contain("not unique");

        _dest.RemoveAll(f => f.Fid == 6);
        (await Check(Def(merge, new ImportFieldMapping { DestFid = 7, SourceFid = 8 }))).AttentionReason.Should().Contain("matches existing records");
    }

    [Fact]
    public async Task Matching_on_the_record_id_stays_sound()
    {
        var def = Def(d => { d.ImportType = "merge"; d.MergeKeyFid = 3; },
            new ImportFieldMapping { DestFid = 3, SourceFid = 3 }, new ImportFieldMapping { DestFid = 7, SourceFid = 8 });

        (await Check(def)).NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public async Task A_formula_that_refers_to_a_deleted_field_flags_the_import()
    {
        var def = Def(null, new ImportFieldMapping { DestFid = 6, Source = ImportMappingSource.Formula, Formula = "Upper([Name])" });
        (await Check(def)).NeedsAttention.Should().BeFalse();

        _source.RemoveAll(f => f.Fid == 6);
        var after = await Check(def);

        after.NeedsAttention.Should().BeTrue();
        after.AttentionReason.Should().Contain("formula");
    }

    [Fact]
    public async Task A_field_that_became_unique_cannot_keep_a_fixed_value()
    {
        var def = Def(null, new ImportFieldMapping { DestFid = 7, Source = ImportMappingSource.Static, StaticValue = "5" });
        (await Check(def)).NeedsAttention.Should().BeFalse();

        _dest.Single(f => f.Fid == 7).IsUnique = true;

        (await Check(def)).AttentionReason.Should().Contain("unique");
    }

    [Fact]
    public async Task A_deleted_condition_field_flags_the_import_but_the_record_id_does_not()
    {
        var conditions = new FilterGroup
        {
            Nodes =
            [
                new() { Condition = new FilterCondition { FieldId = 3, Operator = "gt", Value = "0" } },
                new() { Group = new FilterGroup { Nodes = [new() { Condition = new FilterCondition { FieldId = 8, Operator = "eq", Value = "x" } }] } }
            ]
        };
        var def = Def(d => d.ConditionsJson = ImportJson.Serialize(conditions));
        (await Check(def)).NeedsAttention.Should().BeFalse();

        _source.RemoveAll(f => f.Fid == 8);
        _dest.Single(f => f.Fid == 7).IsRequired = false;
        // field 8 is also mapped, so repoint the mapping to isolate the condition
        def.FieldMappingJson = ImportJson.Serialize(new List<ImportFieldMapping> { new() { DestFid = 6, SourceFid = 6 } });

        (await Check(def)).AttentionReason.Should().Contain("filters on");
    }

    [Fact]
    public async Task A_deleted_table_flags_the_import()
    {
        _tables.GetByIdAsync(SourceId, Arg.Any<CancellationToken>()).Returns<AppTable>(_ => throw new NotFoundException("Table", SourceId));

        (await Check(Def())).AttentionReason.Should().Contain("copies from");

        _tables.GetByIdAsync(DestId, Arg.Any<CancellationToken>()).Returns<AppTable>(_ => throw new NotFoundException("Table", DestId));
        _tables.GetByIdAsync(SourceId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = SourceId });
        (await Check(Def())).AttentionReason.Should().Contain("copies into");
    }

    [Fact]
    public async Task A_flagged_import_is_cleared_once_the_cause_is_fixed()
    {
        var removed = _dest.Single(f => f.Fid == 7);
        _dest.Remove(removed);
        var def = await Check(Def());
        def.NeedsAttention.Should().BeTrue();

        _dest.Add(removed);
        await _sut.RefreshAsync(def, default);

        def.NeedsAttention.Should().BeFalse();
        def.AttentionReason.Should().BeNull();
        await _definitions.Received(1).SetAttentionAsync(1, false, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_checking_an_already_flagged_import_with_the_same_reason_does_not_write_again()
    {
        _dest.RemoveAll(f => f.Fid == 7);
        var def = await Check(Def());

        await _sut.RefreshAsync(def, default);

        await _definitions.Received(1).SetAttentionAsync(Arg.Any<long>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Several_imports_on_the_same_tables_load_each_tables_fields_once()
    {
        await _sut.RefreshAsync([Def(), Def(), Def()], default);

        await _fields.Received(1).ListByTableAsync(SourceId, Arg.Any<CancellationToken>());
        await _fields.Received(1).ListByTableAsync(DestId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_reason_fits_its_column()
    {
        _source.Single(f => f.Fid == 6).Label = new string('x', 700);
        _source.Single(f => f.Fid == 6).Name = new string('x', 700);
        _dest.Single(f => f.Fid == 6).Label = new string('y', 700);
        _dest.Single(f => f.Fid == 6).TypeCode = "Date";

        var after = await Check(Def());

        after.NeedsAttention.Should().BeTrue();
        after.AttentionReason!.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task A_formula_nested_far_deeper_than_anyone_writes_is_flagged_without_reaching_the_parser()
    {
        var def = Def(null, new ImportFieldMapping { DestFid = 6, Source = ImportMappingSource.Formula, Formula = new string('(', 600) + "1" + new string(')', 600) });

        (await Check(def)).AttentionReason.Should().Contain("nested too deeply");
    }

    // ---- where the check is used ----

    [Fact]
    public async Task Listing_checks_the_imports_before_reading_the_list()
    {
        var checker = Substitute.For<IImportDefinitionChecker>();
        var access = Substitute.For<IAppAccessService>();
        _tables.GetByPublicIdAsync(ImportHarness.DestTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = DestId });
        var stored = new List<ImportDefinition> { Def() };
        _definitions.ListEntitiesByDestinationAsync(DestId, Arg.Any<CancellationToken>()).Returns(stored);
        var order = new List<string>();
        checker.When(c => c.RefreshAsync(Arg.Any<IEnumerable<ImportDefinition>>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("check"));
        _definitions.When(d => d.ListByDestinationAsync(DestId, Arg.Any<CancellationToken>())).Do(_ => order.Add("list"));

        await new ListImportDefinitionsHandler(_tables, access, _definitions, checker).HandleAsync(ImportHarness.DestTableId, default);

        order.Should().Equal("check", "list");
    }

    [Fact]
    public async Task The_app_list_is_checked_then_read_for_the_whole_app()
    {
        var appId = Guid.NewGuid();
        var checker = Substitute.For<IImportDefinitionChecker>();
        var access = Substitute.For<IAppAccessService>();
        var apps = Substitute.For<IAppRepository>();
        apps.GetIdByPublicIdAsync(appId, Arg.Any<CancellationToken>()).Returns(7L);
        var order = new List<string>();
        _definitions.ListEntitiesByAppAsync(7, Arg.Any<CancellationToken>()).Returns(new List<ImportDefinition> { Def() });
        checker.When(c => c.RefreshAsync(Arg.Any<IEnumerable<ImportDefinition>>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("check"));
        _definitions.When(d => d.ListByAppAsync(7, Arg.Any<CancellationToken>())).Do(_ => order.Add("list"));

        await new ListAppImportDefinitionsHandler(apps, access, _definitions, checker).HandleAsync(appId, default);

        order.Should().Equal("check", "list");
        await access.Received(1).RequireMembershipByAppPublicIdAsync(appId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Someone_outside_the_app_gets_no_list_of_its_imports()
    {
        var appId = Guid.NewGuid();
        var access = Substitute.For<IAppAccessService>();
        access.RequireMembershipByAppPublicIdAsync(appId, Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new PowerBase.Domain.Exceptions.UnauthorizedActionException("see this app"));

        var act = () => new ListAppImportDefinitionsHandler(Substitute.For<IAppRepository>(), access, _definitions, Substitute.For<IImportDefinitionChecker>()).HandleAsync(appId, default);

        await act.Should().ThrowAsync<PowerBase.Domain.Exceptions.UnauthorizedActionException>();
        await _definitions.DidNotReceiveWithAnyArgs().ListByAppAsync(default, default);
    }
}
