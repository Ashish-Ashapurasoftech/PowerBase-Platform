using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Relationships;

public class ReferenceDropdownPerformanceTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRelationalProjector _relationalProjector = Substitute.For<IRelationalProjector>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();

    private static AppTable MakeTable(long id, string name) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name,
        PhysicalTableName = PhysicalNaming.TableName(id)
    };

    private static AppField MakeField(long id, int fid, string name, string typeCode, bool isSystem = false) => new()
    {
        Id = id,
        Fid = fid,
        Name = name,
        TypeCode = typeCode,
        IsSystem = isSystem
    };

    [Fact]
    public async Task GetParentOptions_PassesPhysicalRoleFilterDirectlyToSql_WithoutLoadingAllIdsIntoMemory()
    {
        var parentTable = MakeTable(10, "Customers");
        var childTable = MakeTable(20, "Orders");
        var rel = new Relationship
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            ParentTableId = parentTable.Id,
            ChildTableId = childTable.Id,
            ReferenceFieldId = 205
        };

        var parentFields = new List<AppField>
        {
            MakeField(1, 1, "CreatedOn", "Date", true),
            MakeField(3, 3, "Record ID#", "Number", true),
            MakeField(4, 4, "CreatedBy", "Number", true),
            MakeField(101, 6, "CustomerName", "Text"),
            MakeField(102, 7, "Region", "Text")
        };
        parentTable.DefaultRecordPickerField1Id = 101;

        var childFields = new List<AppField>
        {
            MakeField(201, 1, "CreatedOn", "Date", true),
            MakeField(205, 10, "CustomerId", "Reference")
        };

        _relRepo.GetByPublicIdAsync(rel.PublicId, Arg.Any<CancellationToken>()).Returns(rel);
        _tableRepo.GetByIdAsync(parentTable.Id, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(parentTable.Id, Arg.Any<CancellationToken>()).Returns(parentFields);
        _fieldRepo.ListByTableAsync(childTable.Id, Arg.Any<CancellationToken>()).Returns(childFields);

        // Physical role filter: Region == "East"
        var physicalFilter = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "eq", Value = "East" } }]
        };

        _enforcer.GetTableAccessAsync(parentTable, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.AllRecords,
                ViewFilter = physicalFilter,
                VisibleFields = parentFields
            }));

        var expectedOptions = new List<ReferenceOption>
        {
            new() { Id = "1", Label = "Acme East", Value1 = "Acme East" }
        };

        _recordRepo.SearchForReferenceAsync(
            parentTable,
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<AppField?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<ReferenceFilterClause>?>(),
            onlyRowIds: null, // Must NOT pass onlyRowIds when formula is null
            roleFilter: Arg.Any<FilterGroup?>(), // Must pass roleFilter directly to SQL
            restrictToCreatedBy: null,
            timeoutSeconds: 30, // 30s timeout applied
            allParentFields: Arg.Any<IReadOnlyList<AppField>?>())
            .Returns(expectedOptions);

        var handler = new GetParentOptionsQueryHandler(
            _tableRepo, _fieldRepo, _relRepo, _recordRepo,
            _relationalProjector, _formulaProjector, _queryContext, _enforcer);

        var result = await handler.HandleAsync(rel.PublicId, "Acme", 10);

        result.Options.Should().HaveCount(1);
        result.Options[0].Label.Should().Be("Acme East");

        // Verify SearchForReferenceAsync received the physical role filter and timeout
        await _recordRepo.Received(1).SearchForReferenceAsync(
            parentTable,
            Arg.Any<IReadOnlyList<AppField>>(),
            "Acme",
            10,
            Arg.Any<AppField?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<ReferenceFilterClause>?>(),
            onlyRowIds: null,
            roleFilter: Arg.Is<FilterGroup?>(f => f != null && f.Nodes.Count > 0),
            restrictToCreatedBy: null,
            timeoutSeconds: 30,
            allParentFields: Arg.Any<IReadOnlyList<AppField>?>());

        // Verify ListAllMatchingAsync was NEVER called (avoiding 50k entity scan)
        await _recordRepo.DidNotReceive().ListAllMatchingAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(),
            Arg.Any<long?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetParentOptions_ResolvesSelectedRecordIdOutsidePage_SecurelyWithRoleFilter()
    {
        var parentTable = MakeTable(10, "Customers");
        var childTable = MakeTable(20, "Orders");
        var rel = new Relationship
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            ParentTableId = parentTable.Id,
            ChildTableId = childTable.Id,
            ReferenceFieldId = 205
        };

        var parentFields = new List<AppField>
        {
            MakeField(3, 3, "Record ID#", "Number", true),
            MakeField(101, 6, "CustomerName", "Text")
        };
        parentTable.DefaultRecordPickerField1Id = 101;

        var childFields = new List<AppField>
        {
            MakeField(205, 10, "CustomerId", "Reference")
        };

        _relRepo.GetByPublicIdAsync(rel.PublicId, Arg.Any<CancellationToken>()).Returns(rel);
        _tableRepo.GetByIdAsync(parentTable.Id, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(parentTable.Id, Arg.Any<CancellationToken>()).Returns(parentFields);
        _fieldRepo.ListByTableAsync(childTable.Id, Arg.Any<CancellationToken>()).Returns(childFields);

        _enforcer.GetTableAccessAsync(parentTable, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                ViewScope = RecordScopes.AllRecords,
                VisibleFields = parentFields
            }));

        // Page 1 returns option "1"
        _recordRepo.SearchForReferenceAsync(
            parentTable,
            Arg.Any<IReadOnlyList<AppField>>(),
            null,
            10,
            Arg.Any<AppField?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<ReferenceFilterClause>?>(),
            Arg.Any<IReadOnlyCollection<long>?>(),
            Arg.Any<FilterGroup?>(),
            Arg.Any<long?>(),
            30,
            Arg.Any<IReadOnlyList<AppField>?>())
            .Returns([new ReferenceOption { Id = "1", Label = "Customer 1", Value1 = "Customer 1" }]);

        // Direct lookup for selectedRecordId "999" (outside page 1)
        _recordRepo.SearchForReferenceAsync(
            parentTable,
            Arg.Any<IReadOnlyList<AppField>>(),
            null,
            1,
            Arg.Any<AppField?>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyList<ReferenceFilterClause>?>(clauses => clauses != null && clauses.Any(c => c.Value == "999")),
            Arg.Any<IReadOnlyCollection<long>?>(),
            Arg.Any<FilterGroup?>(),
            Arg.Any<long?>(),
            30,
            Arg.Any<IReadOnlyList<AppField>?>())
            .Returns([new ReferenceOption { Id = "999", Label = "Customer 999", Value1 = "Customer 999" }]);

        var handler = new GetParentOptionsQueryHandler(
            _tableRepo, _fieldRepo, _relRepo, _recordRepo,
            _relationalProjector, _formulaProjector, _queryContext, _enforcer);

        var result = await handler.HandleAsync(rel.PublicId, null, 10, selectedRecordId: "999");

        // The selected option must be included in the options list so the dropdown displays it
        result.Options.Should().Contain(o => o.Id == "999" && o.Label == "Customer 999");
    }

    [Fact]
    public async Task ViewableRowIds_WhenFormulaIsNull_QueriesOnlyIdColumnWithTimeout()
    {
        var table = MakeTable(50, "Invoices");
        var fields = new List<AppField>
        {
            MakeField(1, 1, "CreatedOn", "Date", true),
            MakeField(3, 3, "Record ID#", "Number", true),
            MakeField(4, 4, "CreatedBy", "Number", true),
            MakeField(10, 6, "Amount", "Currency"),
            MakeField(11, 7, "Description", "Text")
        };

        var physicalFilter = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 6, Operator = "gt", Value = "1000" } }]
        };

        var access = new TableAccessContext
        {
            Unrestricted = false,
            ViewScope = RecordScopes.AllRecords,
            ViewFilter = physicalFilter,
            RestrictToCreatedBy = 99L,
            VisibleFields = fields
        };

        _recordRepo.ListMatchingRowIdsAsync(
            table,
            fields,
            Arg.Any<FilterGroup?>(),
            restrictToCreatedBy: 99L,
            maxRows: 10000,
            timeoutSeconds: 30,
            ct: Arg.Any<CancellationToken>())
            .Returns([101L, 102L, 103L]);

        var rowIds = await ViewableRowIds.ResolveAsync(
            access, table, fields, _recordRepo, _relationalProjector, _formulaProjector, default);

        rowIds.Should().Equal(101L, 102L, 103L);

        // Verify ListMatchingRowIdsAsync was called with 30s timeout and 10k max rows
        await _recordRepo.Received(1).ListMatchingRowIdsAsync(
            table,
            fields,
            Arg.Is<FilterGroup?>(f => f != null && f.Nodes.Count > 0),
            99L,
            10000,
            30,
            Arg.Any<CancellationToken>());

        // Verify ListAllMatchingAsync was NOT called
        await _recordRepo.DidNotReceive().ListAllMatchingAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(),
            Arg.Any<long?>(),
            Arg.Any<CancellationToken>());
    }
}
