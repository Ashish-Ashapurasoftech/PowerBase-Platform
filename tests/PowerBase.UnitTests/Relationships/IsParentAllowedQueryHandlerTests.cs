using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

/// <summary>"Would the Reference picker offer this parent record?" — the single exists-check behind the
/// Add-child actions (IsParentAllowedQueryHandler).</summary>
public class IsParentAllowedQueryHandlerTests
{
    private const long ParentRowId = 3;

    private static readonly AppTable DepartmentTable = new() { Id = 5, Name = "Departments" };

    private static readonly List<AppField> DepartmentFields =
    [
        new() { Id = 1, Fid = 6, Name = "Department Code", TypeCode = "Text" },
        new() { Id = 2, Fid = 7, Name = "Type", TypeCode = "Number" },
    ];

    private static readonly string CodeEqualsMkt = System.Text.Json.JsonSerializer.Serialize(new FilterGroup
    {
        Logic = "and",
        Nodes =
        [
            new FilterNode
            {
                Condition = new FilterCondition { FieldId = 6, Operator = "eq", Value = "MKT", ValueMode = "literal" },
            },
        ],
    });

    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IRelationshipRepository _rels = Substitute.For<IRelationshipRepository>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();
    private readonly IQueryContext _context = Substitute.For<IQueryContext>();

    private readonly Relationship _rel = new()
    {
        Id = 1, PublicId = Guid.NewGuid(), ParentTableId = 5, ChildTableId = 1, ReferenceFieldId = 31, ReferenceFid = 13,
    };

    private readonly IsParentAllowedQueryHandler _handler;

    public IsParentAllowedQueryHandlerTests()
    {
        _rels.GetByPublicIdAsync(_rel.PublicId, Arg.Any<CancellationToken>()).Returns(_rel);
        _tables.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(DepartmentTable);
        _fields.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(DepartmentFields);
        _records.ExistsAsync(DepartmentTable, ParentRowId, Arg.Any<CancellationToken>()).Returns(true);
        UseChildReferenceSettings("""{"parentTableId":5}""");

        _handler = new IsParentAllowedQueryHandler(_tables, _fields, _rels, _records, _context);
    }

    private void UseChildReferenceSettings(string settings) =>
        _fields.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 31, Fid = 13, Name = "Department", TypeCode = "Reference", Settings = settings },
        });

    private static string TreeSettings(string tree) =>
        System.Text.Json.JsonSerializer.Serialize(new { parentTableId = 5, filterTree = tree });

    private void MatchResultIs(bool matches) =>
        _records.MatchesReferenceFilterAsync(
                DepartmentTable, ParentRowId, Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>())
            .Returns(matches);

    private Task<bool> Run(string parentRecord = "3") => _handler.HandleAsync(_rel.PublicId, parentRecord);

    [Fact]
    public async Task NoFilter_LiveParent_IsAllowed_WithoutRunningAFilterQuery()
    {
        (await Run()).Should().BeTrue();

        await _records.DidNotReceive().MatchesReferenceFilterAsync(
            Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Filter_ParentPassesIt_IsAllowed()
    {
        UseChildReferenceSettings(TreeSettings(CodeEqualsMkt));
        MatchResultIs(true);

        (await Run()).Should().BeTrue();
    }

    [Fact]
    public async Task Filter_ParentFailsIt_IsNotAllowed()
    {
        UseChildReferenceSettings(TreeSettings(CodeEqualsMkt));
        MatchResultIs(false);

        (await Run()).Should().BeFalse();
    }

    [Fact]
    public async Task Filter_IsJudgedAgainstTheParentTableFields_WithThePickersClauses()
    {
        UseChildReferenceSettings(TreeSettings(CodeEqualsMkt));
        MatchResultIs(true);

        await Run();

        await _records.Received(1).MatchesReferenceFilterAsync(
            DepartmentTable, ParentRowId,
            Arg.Is<IReadOnlyList<ReferenceFilterClause>>(c => c.Count == 1 && c[0].Tree != null && c[0].TreeFields == DepartmentFields),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Filter_OnlyOnUnsuppliedFormFields_HasNothingToJudge_SoIsAllowed()
    {
        // Compares against a field of the child form; outside a form there is no value, so the condition drops out.
        var tree = System.Text.Json.JsonSerializer.Serialize(new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new FilterNode
                {
                    Condition = new FilterCondition { FieldId = 6, Operator = "eq", ValueMode = "parentField", ValueFieldId = 12 },
                },
            ],
        });
        UseChildReferenceSettings(TreeSettings(tree));

        (await Run()).Should().BeTrue();

        await _records.DidNotReceive().MatchesReferenceFilterAsync(
            Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParentRecordDoesNotExist_IsNotAllowed()
    {
        _records.ExistsAsync(DepartmentTable, 99, Arg.Any<CancellationToken>()).Returns(false);

        (await Run("99")).Should().BeFalse();
    }

    [Fact]
    public async Task ParentGivenAsPublicId_IsResolvedToItsRowId()
    {
        var publicId = Guid.NewGuid();
        _records.GetRecordIdByPublicIdAsync(DepartmentTable, publicId, Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(ParentRowId);
        UseChildReferenceSettings(TreeSettings(CodeEqualsMkt));
        MatchResultIs(true);

        (await Run(publicId.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task ParentPublicIdNotFound_IsNotAllowed()
    {
        _records.GetRecordIdByPublicIdAsync(DepartmentTable, Arg.Any<Guid>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(0L);

        (await Run(Guid.NewGuid().ToString())).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public async Task UnrecognisedRecordReference_IsNotAllowed(string parentRecord)
    {
        (await Run(parentRecord)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownRelationship_ThrowsNotFound()
    {
        var act = () => _handler.HandleAsync(Guid.NewGuid(), "3");

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
