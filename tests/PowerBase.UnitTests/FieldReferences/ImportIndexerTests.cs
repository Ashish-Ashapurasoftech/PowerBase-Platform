using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Imports;
using PowerBase.Application.Pages;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.FieldReferences;

/// <summary>Saved imports in the Usage index: what an import writes, matches on, reads, filters on and calculates from.</summary>
public class ImportIndexerTests
{
    private const long AppId = 1;
    private const long SourceTableId = 7;
    private const long DestTableId = 8;

    private readonly IFieldReferenceRepository _references = Substitute.For<IFieldReferenceRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();

    public ImportIndexerTests()
    {
        _fields.ListByTableAsync(SourceTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 1001, Fid = 6, AppTableId = SourceTableId, Name = "Qty", Label = "Qty", TypeCode = "Number" },
            new() { Id = 1002, Fid = 7, AppTableId = SourceTableId, Name = "Status", Label = "Status", TypeCode = "Text" },
        });
        _fields.ListByTableAsync(DestTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 2001, Fid = 6, AppTableId = DestTableId, Name = "Amount", Label = "Amount", TypeCode = "Number" },
            new() { Id = 2002, Fid = 7, AppTableId = DestTableId, Name = "Code", Label = "Code", TypeCode = "Text" },
        });
        _tables.GetByIdAsync(DestTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = DestTableId, PublicId = Guid.NewGuid(), AppId = AppId });
        _references.ListReportSourcesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<Report>());
    }

    private FieldReferenceIndexer NewIndexer() =>
        new(_references, _fields, _tables, Substitute.For<IReportRepository>(), Substitute.For<IFormRepository>(),
            Substitute.For<IFormRuleRepository>(), new FormulaEngine(), NullLogger<FieldReferenceIndexer>.Instance);

    private static ImportDefinition Def(
        string type = ImportTypes.Copy, int? mergeKey = null, string? conditions = null, bool file = false,
        params ImportFieldMapping[] mappings) => new()
    {
        Id = 5, PublicId = Guid.NewGuid(), AppId = AppId, DestinationTableId = DestTableId,
        SourceKind = file ? "file" : "table", SourceTableId = file ? null : SourceTableId,
        ImportType = type, MergeKeyFid = mergeKey, ConditionsJson = conditions,
        FieldMappingJson = JsonSerializer.Serialize(mappings),
    };

    private static ImportFieldMapping Map(int dest, int? source = null, string? formula = null) => new()
    {
        DestFid = dest, SourceFid = source, Formula = formula,
        Source = formula is not null ? ImportMappingSource.Formula : ImportMappingSource.Dynamic,
    };

    private async Task<IReadOnlyCollection<FieldReferenceRow>> IndexAsync(ImportDefinition def)
    {
        _references.GetImportDefinitionAsync(def.PublicId, Arg.Any<CancellationToken>()).Returns(def);
        IReadOnlyCollection<FieldReferenceRow>? captured = null;
        await _references.ReplaceForSourceAsync(
            FieldReferenceSourceTypes.Import, Arg.Any<long>(), Arg.Do<IReadOnlyCollection<FieldReferenceRow>>(r => captured = r), Arg.Any<CancellationToken>());
        await NewIndexer().ReindexImportAsync(def.PublicId);
        return captured ?? throw new InvalidOperationException("The import was not indexed.");
    }

    [Fact]
    public async Task A_mapping_writes_to_a_destination_field_and_reads_a_source_field()
    {
        var rows = await IndexAsync(Def(mappings: Map(6, 6)));

        rows.Should().Contain(r => r.TargetFieldId == 2001 && r.SourceTableId == DestTableId && r.Usage == FieldReferenceUsages.WritesTo);
        rows.Should().Contain(r => r.TargetFieldId == 1001 && r.SourceTableId == SourceTableId && r.Usage == FieldReferenceUsages.ReadsFrom);
        rows.Should().OnlyContain(r => r.SourceType == FieldReferenceSourceTypes.Import && r.SourceId == 5);
    }

    [Fact]
    public async Task A_merge_import_matches_on_its_key_field()
    {
        var rows = await IndexAsync(Def(ImportTypes.Merge, mergeKey: 7, mappings: Map(6, 6)));

        rows.Should().Contain(r => r.TargetFieldId == 2002 && r.Usage == FieldReferenceUsages.MatchKey);
    }

    [Fact]
    public async Task A_copy_import_has_no_match_key()
    {
        var rows = await IndexAsync(Def(mergeKey: 7, mappings: Map(6, 6)));

        rows.Should().NotContain(r => r.Usage == FieldReferenceUsages.MatchKey);
    }

    [Fact]
    public async Task A_formula_mapping_uses_the_source_fields_it_reads()
    {
        var rows = await IndexAsync(Def(mappings: Map(6, formula: "[Qty] * 2")));

        rows.Should().Contain(r => r.TargetFieldId == 1001 && r.Usage == FieldReferenceUsages.Formula);
        rows.Should().Contain(r => r.TargetFieldId == 2001 && r.Usage == FieldReferenceUsages.WritesTo);
    }

    [Fact]
    public async Task A_filter_on_the_source_uses_the_field_it_tests()
    {
        var conditions = RoleRecordFilterJson.SerializeGroup(new FilterGroup
        {
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "eq", Value = "open" } }],
        });

        var rows = await IndexAsync(Def(conditions: conditions, mappings: Map(6, 6)));

        rows.Should().Contain(r => r.TargetFieldId == 1002 && r.SourceTableId == SourceTableId && r.Usage == FieldReferenceUsages.Filter);
    }

    [Fact]
    public async Task A_mapping_that_does_not_import_uses_nothing()
    {
        var skipped = Map(6, 6);
        skipped.DoNotImport = true;

        var rows = await IndexAsync(Def(mappings: skipped));

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_import_only_counts_what_it_writes_because_a_files_columns_are_not_fields()
    {
        // fid 6 here is a column of the file, not the source table's field 6.
        var rows = await IndexAsync(Def(file: true, mappings: Map(6, 6)));

        rows.Should().ContainSingle().Which.Should().Match<FieldReferenceRow>(r => r.TargetFieldId == 2001 && r.Usage == FieldReferenceUsages.WritesTo);
    }

    [Fact]
    public async Task A_deleted_import_loses_its_rows()
    {
        var id = Guid.NewGuid();
        _references.GetImportDefinitionAsync(id, Arg.Any<CancellationToken>()).Returns((ImportDefinition?)null);

        await NewIndexer().ReindexImportAsync(id);

        await _references.Received(1).DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Import, id, Arg.Any<CancellationToken>());
    }
}
