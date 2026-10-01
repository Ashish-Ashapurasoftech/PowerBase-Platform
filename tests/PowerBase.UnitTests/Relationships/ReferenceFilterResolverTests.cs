using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

public class ReferenceFilterResolverTests
{
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();

    private static readonly List<AppField> StatusFields =
    [
        new() { Id = 1, Fid = 6, Name = "Name", TypeCode = "Text" },
        new() { Id = 2, Fid = 7, Name = "Type", TypeCode = "Number" },
    ];

    private static readonly List<AppField> TaskFields =
    [
        new() { Id = 30, Fid = 12, Name = "TaskType", TypeCode = "Reference" },
    ];

    private static readonly AppTable Junction = new() { Id = 99, PublicId = Guid.NewGuid() };

    private static readonly List<AppField> JunctionFields =
    [
        new() { Id = 20, Fid = 6, Name = "StatusId", TypeCode = "Reference" },
        new() { Id = 21, Fid = 7, Name = "TaskTypeId", TypeCode = "Reference" },
    ];

    public ReferenceFilterResolverTests()
    {
        _tables.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(Junction);
        _fields.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(JunctionFields);
    }

    private Task<IReadOnlyList<ReferenceFilterClause>> Build(ReferenceSettings? s, Dictionary<int, string?> values) =>
        ReferenceFilterResolver.BuildAsync(s, TaskFields, StatusFields, values, _tables, _fields, default);

    [Fact]
    public async Task NoConditions_ReturnsNoClauses()
    {
        (await Build(new ReferenceSettings(), new())).Should().BeEmpty();
        (await Build(null, new())).Should().BeEmpty();
    }

    [Fact]
    public async Task DirectCondition_ResolvesParentFieldAndFormValue()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 12, ParentFid = 7 }] };

        var clauses = await Build(settings, new() { [12] = " 5 " });

        clauses.Should().ContainSingle();
        clauses[0].ParentField!.Fid.Should().Be(7);
        clauses[0].Value.Should().Be("5");
        clauses[0].JunctionTable.Should().BeNull();
    }

    [Fact]
    public async Task JunctionCondition_ResolvesJunctionTableAndFields()
    {
        var settings = new ReferenceSettings
        {
            FilterConditions = [new() { FormFid = 12, JunctionTableId = 99, JunctionParentFid = 6, JunctionValueFid = 7 }],
        };

        var clauses = await Build(settings, new() { [12] = "3" });

        clauses.Should().ContainSingle();
        clauses[0].JunctionTable.Should().BeSameAs(Junction);
        clauses[0].JunctionParentField!.Name.Should().Be("StatusId");
        clauses[0].JunctionValueField!.Name.Should().Be("TaskTypeId");
        clauses[0].Value.Should().Be("3");
    }

    [Fact]
    public async Task MissingOrBlankFormValue_YieldsNullValue_SoNothingMatches()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 12, ParentFid = 7 }] };

        (await Build(settings, new() { [12] = null }))[0].Value.Should().BeNull();
        (await Build(settings, new() { [12] = "  " }))[0].Value.Should().BeNull();
    }


    [Fact]
    public async Task ControllingFieldNotSupplied_IsSkipped_SoNoFilterApplies()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 12, ParentFid = 7 }] };

        (await Build(settings, new())).Should().BeEmpty();
    }

    [Fact]
    public async Task ControllingFieldDeleted_IsSkipped_SoDropdownIsNotPermanentlyEmpty()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 99, ParentFid = 7 }] };

        (await Build(settings, new() { [99] = "1" })).Should().BeEmpty();
    }
    [Fact]
    public async Task StaleCondition_PointingAtDeletedField_IsSkipped()
    {
        var settings = new ReferenceSettings
        {
            FilterConditions =
            [
                new() { FormFid = 12, ParentFid = 999 },
                new() { FormFid = 12, JunctionTableId = 99, JunctionParentFid = 6, JunctionValueFid = 999 },
            ],
        };

        (await Build(settings, new() { [12] = "1" })).Should().BeEmpty();
    }
}
