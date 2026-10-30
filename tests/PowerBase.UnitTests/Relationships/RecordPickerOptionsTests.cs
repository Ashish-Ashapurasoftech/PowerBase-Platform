using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using Xunit;

namespace PowerBase.UnitTests.Relationships;

/// <summary>A form element's "Select using record picker" choice (default / based on report / based on field) reaching the
/// parent-options query: which columns, filter and sort the dropdown gets.</summary>
public class RecordPickerOptionsTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRelationalProjector _relationalProjector = Substitute.For<IRelationalProjector>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();

    private readonly AppTable _parent = new() { Id = 10, PublicId = Guid.NewGuid(), Name = "Departments", DefaultRecordPickerField1Id = 101 };
    private readonly AppTable _child = new() { Id = 20, PublicId = Guid.NewGuid(), Name = "Employees" };
    private readonly Relationship _rel = new() { Id = 1, PublicId = Guid.NewGuid(), ParentTableId = 10, ChildTableId = 20, ReferenceFieldId = 205, ProxyFieldId = 206 };
    private readonly Guid _elementId = Guid.NewGuid();
    private readonly List<AppField> _parentFields;
    private readonly List<AppField> _childFields;

    public RecordPickerOptionsTests()
    {
        _parentFields =
        [
            Field(3, 3, "Record ID#", "Number", system: true),
            Field(101, 6, "Department Name", "Text"),
            Field(102, 7, "Department Code", "Text"),
            Field(103, 8, "Description", "Text"),
            Field(104, 9, "Budget", "Number"),
        ];
        _childFields =
        [
            Field(205, 10, "Related Dept", "Reference"),
            Field(206, 11, "Dept - Budget", "Lookup", LookupOf(relationshipId: 1, sourceFid: 9)),
            Field(207, 12, "Dept - Code", "Lookup", LookupOf(relationshipId: 1, sourceFid: 7)),
            Field(208, 13, "Other - Code", "Lookup", LookupOf(relationshipId: 99, sourceFid: 7)),
        ];

        _relRepo.GetByPublicIdAsync(_rel.PublicId, Arg.Any<CancellationToken>()).Returns(_rel);
        _tableRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(_parent);
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(_parentFields);
        _fieldRepo.ListByTableAsync(20, Arg.Any<CancellationToken>()).Returns(_childFields);
        _recordRepo.SearchForReferenceAsync(default!, default!, default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new List<ReferenceOption>());
    }

    private static AppField Field(long id, int fid, string name, string typeCode, string? settings = null, bool system = false) => new()
    {
        Id = id, Fid = fid, Name = name, TypeCode = typeCode, Settings = settings, IsSystem = system, IsReportable = true,
    };

    private static string LookupOf(long relationshipId, int sourceFid) =>
        JsonSerializer.Serialize(new LookupSettings { RelationshipId = relationshipId, SourceFid = sourceFid });

    private GetParentOptionsQueryHandler CreateSut() => new(
        _tableRepo, _fieldRepo, _relRepo, _recordRepo, _relationalProjector, _formulaProjector, _queryContext,
        enforcer: null, formRepo: _formRepo, reportRepo: _reportRepo);

    private void ElementIs(string? mode, Guid? reportId = null, long? fieldId = null, long tableId = 20, long? fieldFid = 10) =>
        _formRepo.GetRecordPickerElementAsync(_elementId, Arg.Any<CancellationToken>())
            .Returns(new RecordPickerElementDto { AppTableId = tableId, AppFieldFid = fieldFid, Mode = mode, ReportPublicId = reportId, FieldId = fieldId });

    private Report ReportWith(ReportDefinition definition, long tableId = 10, string type = "Table", string visibility = "Shared", long? relationshipId = null)
    {
        var report = new Report
        {
            Id = 500, PublicId = Guid.NewGuid(), AppTableId = tableId, ReportType = type, Visibility = visibility,
            RelationshipId = relationshipId, Definition = JsonSerializer.Serialize(definition),
        };
        _reportRepo.GetByPublicIdAsync(report.PublicId, Arg.Any<CancellationToken>()).Returns(report);
        return report;
    }

    /// <summary>The arguments of the one SearchForReferenceAsync call: 1 label fields, 4 primary label, 12 extra filter, 13 order by, 14 key field.</summary>
    private object?[] SearchCall()
    {
        var call = _recordRepo.ReceivedCalls().Single(c =>
            c.GetMethodInfo().Name == nameof(IRecordRepository.SearchForReferenceAsync) && c.GetArguments()[1] is not null);
        return call.GetArguments();
    }

    private static IReadOnlyList<AppField> Labels(object?[] args) => (IReadOnlyList<AppField>)args[1]!;

    [Fact]
    public async Task NoElement_UsesTheParentsDefaultRecordPicker()
    {
        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50);

        result.Headers.Should().Equal("Department Name");
        var args = SearchCall();
        args[12].Should().BeNull();
        args[13].Should().BeNull();
        args[14].Should().BeNull("a Record ID# key has no key text to carry");
    }

    // ── The parent's Set Key / Display Key is never a column (Quickbase: a table keyed on "Email Template ID" with
    //    picker Name / Subject / Related File Type lists exactly those 3). It only rides along as KeyValue. ──

    [Fact]
    public async Task SetKey_IsNotAddedAsAColumn_OnlyThePickerFieldsAreListed()
    {
        _parent.KeyFieldId = 101;                       // Department Name is the key
        _parent.DefaultRecordPickerField1Id = 103;      // the picker is Description only

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50);

        result.Headers.Should().Equal("Description");
        var args = SearchCall();
        Labels(args).Select(f => f.Fid).Should().Equal(8);
        ((AppField)args[14]!).Fid.Should().Be(6, "the key's text is carried separately so an existing value finds its option");
    }

    [Fact]
    public async Task SetKey_WithNoPickerFieldConfigured_ListsTheKey()
    {
        _parent.KeyFieldId = 102;                       // Department Code is the key
        _parent.DefaultRecordPickerField1Id = null;

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50);

        result.Headers.Should().Equal("Department Code");
    }

    [Fact]
    public async Task SetKey_WithOnlyTheSeededRecordIdAsPicker_ListsTheKey()
    {
        _parent.KeyFieldId = 102;
        _parent.DefaultRecordPickerField1Id = 3;        // a new table is seeded with Record ID# as its Primary field

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50);

        result.Headers.Should().Equal("Department Code");
    }

    [Fact]
    public async Task SetKey_ThatIsAlsoAPickerField_IsListedOnlyBecauseItIsConfigured()
    {
        _parent.KeyFieldId = 101;
        _parent.DefaultRecordPickerField1Id = 101;
        _parent.DefaultRecordPickerField2Id = 103;

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50);

        result.Headers.Should().Equal("Department Name", "Description");
    }

    [Fact]
    public async Task RelationshipDisplayKey_OverridesTheTablesKey_AndIsNotAColumnEither()
    {
        _parent.KeyFieldId = 101;
        _rel.DisplayKeyFieldId = 104;                   // Budget

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50);

        result.Headers.Should().Equal("Department Name");
        ((AppField)SearchCall()[14]!).Fid.Should().Be(9);
    }

    [Fact]
    public async Task SetKey_WithAnElementsFieldChoice_StillCarriesTheKeyText()
    {
        _parent.KeyFieldId = 101;
        ElementIs(RecordPickerModes.Field, fieldId: 12);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Code");
        ((AppField)SearchCall()[14]!).Fid.Should().Be(6);
    }

    [Fact]
    public async Task FieldMode_ShowsOnlyTheLookupsSourceField()
    {
        ElementIs(RecordPickerModes.Field, fieldId: 11);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Budget");
        var args = SearchCall();
        Labels(args).Select(f => f.Fid).Should().Equal(9);
        ((AppField)args[4]!).Fid.Should().Be(9);
    }

    [Fact]
    public async Task FieldMode_ForTheProxyElement_IsHonouredToo()
    {
        ElementIs(RecordPickerModes.Field, fieldId: 12, fieldFid: 11);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Code");
    }

    [Fact]
    public async Task FieldMode_WithALookupOfAnotherRelationship_FallsBackToTheDefaultPicker()
    {
        ElementIs(RecordPickerModes.Field, fieldId: 13);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Name");
    }

    [Fact]
    public async Task ReportMode_UsesTheFirstThreeColumnsFilterAndSortOfTheReport()
    {
        var report = ReportWith(new ReportDefinition
        {
            Columns = [7, 9, 6, 8],
            FilterTree = new FilterGroup
            {
                Logic = "and",
                Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "eq", Value = "X" } }],
            },
            TableSortGroup = [new SortGroupLevel { FieldId = 9, Desc = true }],
        });
        ElementIs(RecordPickerModes.Report, reportId: report.PublicId);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Code", "Budget", "Department Name");
        var args = SearchCall();
        Labels(args).Select(f => f.Fid).Should().Equal(7, 9, 6);
        ((AppField)args[4]!).Fid.Should().Be(7, "the closed input shows the report's first column");
        var filter = (FilterGroup)args[12]!;
        filter.Nodes.Should().ContainSingle().Which.Condition!.Value.Should().Be("X");
        var sort = (IReadOnlyList<SortSpec>)args[13]!;
        sort.Should().ContainSingle().Which.Should().BeEquivalentTo(new SortSpec { FieldId = 9, Desc = true });
    }

    [Fact]
    public async Task ReportMode_WithAReportOfAnotherTable_FallsBackToTheDefaultPicker()
    {
        var report = ReportWith(new ReportDefinition { Columns = [7] }, tableId: 99);
        ElementIs(RecordPickerModes.Report, reportId: report.PublicId);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Name");
        SearchCall()[12].Should().BeNull();
    }

    [Theory]
    [InlineData("Summary", null, "Shared")]
    [InlineData("Table", 7L, "Shared")]
    [InlineData("Table", null, "Personal")]
    public async Task ReportMode_WithAnIneligibleReport_FallsBackToTheDefaultPicker(string type, long? embeddedFor, string visibility)
    {
        var report = ReportWith(new ReportDefinition { Columns = [7] }, type: type, visibility: visibility, relationshipId: embeddedFor);
        ElementIs(RecordPickerModes.Report, reportId: report.PublicId);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Name");
    }

    [Fact]
    public async Task ReportMode_WithADeletedReport_FallsBackToTheDefaultPicker()
    {
        var missing = Guid.NewGuid();
        _reportRepo.GetByPublicIdAsync(missing, Arg.Any<CancellationToken>()).Returns<Report>(_ => throw new NotFoundException("Report", missing));
        ElementIs(RecordPickerModes.Report, reportId: missing);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Name");
    }

    [Fact]
    public async Task AnElementOfAnotherTable_IsIgnored()
    {
        ElementIs(RecordPickerModes.Field, fieldId: 11, tableId: 77);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Name");
    }

    [Fact]
    public async Task AnElementThatIsNeitherTheReferenceNorItsProxy_IsIgnored()
    {
        ElementIs(RecordPickerModes.Field, fieldId: 11, fieldFid: 12);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Name");
    }

    [Fact]
    public async Task AnUnknownElement_UsesTheDefaultPicker()
    {
        _formRepo.GetRecordPickerElementAsync(_elementId, Arg.Any<CancellationToken>()).Returns((RecordPickerElementDto?)null);

        var result = await CreateSut().HandleAsync(_rel.PublicId, null, 50, elementId: _elementId);

        result.Headers.Should().Equal("Department Name");
    }
}
