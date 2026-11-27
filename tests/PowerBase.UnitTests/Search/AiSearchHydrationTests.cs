using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport.OData;
using PowerBase.Application.Search;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Search;

public class AiSearchHydrationTests
{
    private static AppField ReadySearchField(AppField f)
    {
        f.MaterializeMode = MaterializeModes.On;
        f.MaterializeState = MaterializeStates.Ready;
        f.SearchIndexState = SearchIndexStates.Indexed;
        f.MaterializeVersion = 1;
        f.SearchIndexVersion = 1;
        return f;
    }

    [Fact]
    public void ResolveSlotName_uses_slot_type_family_for_indexed_summary_and_lookup_fields()
    {
        var summaryCount = ReadySearchField(new AppField
        {
            Id = 10, Fid = 10, Name = "Task Count", TypeCode = "Summary",
            Settings = """{"relationshipId":1,"function":"Count","resultType":"Number"}""",
            IsFilterable = true, SearchExact = true, SearchSlot = "num_03"
        });
        var formulaDate = ReadySearchField(new AppField
        {
            Id = 11, Fid = 11, Name = "Due Calc", TypeCode = "Formula_DateTime",
            IsFilterable = true, SearchExact = true, SearchSlot = "date_02"
        });
        var lookupText = ReadySearchField(new AppField
        {
            Id = 12, Fid = 12, Name = "Project Name", TypeCode = "Lookup",
            Settings = """{"relationshipId":1,"referenceFid":6,"sourceTableId":1,"sourceFid":6,"sourceTypeCode":"Text"}""",
            IsFilterable = true, SearchExact = true, SearchSlot = "str_05"
        });

        var allFields = new List<AppField> { summaryCount, formulaDate, lookupText };

        SearchSlotResolver.ResolveSlotName(summaryCount, allFields).Should().Be("num_03");
        SearchSlotResolver.ResolveSlotName(formulaDate, allFields).Should().Be("date_02");
        SearchSlotResolver.ResolveSlotName(lookupText, allFields).Should().Be("str_05");
    }

    [Fact]
    public void ODataFilterBuilder_expresses_computed_fields_only_when_IsInSearchIndex_is_true()
    {
        var readySummary = ReadySearchField(new AppField
        {
            Id = 10, Fid = 10, Name = "Total Hours", TypeCode = "Summary",
            Settings = """{"relationshipId":1,"function":"Sum","targetTypeCode":"Number"}""",
            IsFilterable = true, SearchExact = true, SearchSlot = "num_04"
        });
        var unreadySummary = new AppField
        {
            Id = 11, Fid = 11, Name = "Pending Summary", TypeCode = "Summary",
            Settings = """{"relationshipId":1,"function":"Sum","targetTypeCode":"Number"}""",
            IsFilterable = true, SearchExact = true, SearchSlot = "num_05"
        };
        var fields = new List<AppField> { readySummary, unreadySummary };

        var readyFilter = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 10, Operator = "gt", Value = "5" } }]
        };
        var unreadyFilter = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 11, Operator = "gt", Value = "5" } }]
        };

        ODataFilterBuilder.CanExpress(readyFilter, fields).Should().BeTrue();
        ODataFilterBuilder.Build(readyFilter, fields).Should().Be("num_04 gt 5");
        ODataFilterBuilder.CanExpress(unreadyFilter, fields).Should().BeFalse();
    }

    [Fact]
    public void AiSearchRecordHydrator_hydrates_rows_and_isolates_only_missing_sql_fields()
    {
        var fId = new AppField { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id", IsFilterable = true, SearchExact = true, SearchSlot = "num_01" };
        var fName = new AppField { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text", IsSearchable = true, SearchSlot = "str_01" };
        var fHours = ReadySearchField(new AppField
        {
            Id = 7, Fid = 7, Name = "Total Hours", TypeCode = "Summary",
            Settings = """{"relationshipId":1,"function":"Sum","targetTypeCode":"Number"}""",
            IsFilterable = true, SearchExact = true, SearchSlot = "num_02"
        });
        var fNotes = new AppField { Id = 8, Fid = 8, Name = "Long Notes", TypeCode = "RichText", IsSearchable = false, IsFilterable = false };

        var allFields = new List<AppField> { fId, fName, fHours, fNotes };

        // All AI case: Name + Total Hours -> 0 SQL fields needed
        var allAiPlan = AiSearchRecordHydrator.Plan([fName, fHours], allFields, needRecordIdForRemainingProjections: false, needAuditColumns: false);
        allAiPlan.NeedsSqlFetch.Should().BeFalse();
        allAiPlan.MissingSqlFields.Should().BeEmpty();
        allAiPlan.PreHydratedComputedFids.Should().Contain(7);

        // Partial case: Name + Total Hours + Long Notes -> only fNotes requested from SQL
        var partialPlan = AiSearchRecordHydrator.Plan([fName, fHours, fNotes], allFields, needRecordIdForRemainingProjections: true, needAuditColumns: false);
        partialPlan.NeedsSqlFetch.Should().BeTrue();
        partialPlan.NeedSqlRecordId.Should().BeFalse(); // fId is in AI Search
        partialPlan.MissingSqlFields.Should().ContainSingle().Which.Fid.Should().Be(8);

        var pid = Guid.NewGuid();
        var aiDoc = new SearchDocumentRow(pid, new Dictionary<string, object?>
        {
            ["num_01"] = 42d,
            ["str_01"] = "Alpha Project",
            ["num_02"] = 12.5d
        });
        var sqlRow = new Dictionary<string, object?>
        {
            ["PublicId"] = pid,
            [PhysicalNaming.ColumnName(8)] = "<p>Details</p>"
        };

        var (rows, seeds) = AiSearchRecordHydrator.BuildRows(partialPlan, [aiDoc], [sqlRow]);
        rows.Should().HaveCount(1);
        rows[0]["Id"].Should().Be(42L);
        rows[0][PhysicalNaming.ColumnName(6)].Should().Be("Alpha Project");
        rows[0][PhysicalNaming.ColumnName(8)].Should().Be("<p>Details</p>");
        seeds[0][7].Should().Be(12.5m);
    }

    [Fact]
    public async Task RelationalProjector_aggregates_child_summary_from_ai_search_without_sql_when_indexed()
    {
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var relRepo = Substitute.For<IRelationshipRepository>();
        var appRepo = Substitute.For<IAppRepository>();
        var userRepo = Substitute.For<IUserRepository>();
        var formulaProjector = Substitute.For<Application.Formulas.IFormulaProjector>();
        var queryContext = Substitute.For<IQueryContext>();
        var searchService = Substitute.For<IAzureSearchService>();
        var searchOutbox = Substitute.For<ISearchOutbox>();

        queryContext.TenantId.Returns(17L);
        searchService.IsGridSearchEnabled.Returns(true);
        searchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        searchOutbox.ListPendingRecordsAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var parentTable = new AppTable { Id = 100, AppId = 1, Name = "Projects" };
        var childTable = new AppTable { Id = 200, AppId = 1, Name = "Tasks" };

        var parentSummary = new AppField
        {
            Id = 50, Fid = 50, AppTableId = 100, Name = "Project Total Hours", TypeCode = "Summary",
            Settings = """{"relationshipId":1,"childTableId":200,"referenceFid":6,"function":"Sum","targetFid":20,"targetTypeCode":"Summary"}"""
        };
        var childRef = new AppField
        {
            Id = 6, Fid = 6, AppTableId = 200, Name = "Project", TypeCode = "Reference",
            Settings = """{"relationshipId":1,"parentTableId":100}""",
            IsFilterable = true, SearchSlot = "str_01"
        };
        var childSummaryHours = ReadySearchField(new AppField
        {
            Id = 20, Fid = 20, AppTableId = 200, Name = "Task Logged Hours", TypeCode = "Summary",
            Settings = """{"relationshipId":2,"function":"Sum","targetTypeCode":"Number"}""",
            IsFilterable = true, SearchExact = true, SearchSlot = "num_01"
        });

        appRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        relRepo.ListByParentTableAsync(100, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 1, AppId = 1, ParentTableId = 100, ChildTableId = 200, ReferenceFid = 6 }
        });
        tableRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns(childTable);
        fieldRepo.ListByTableAsync(200, Arg.Any<CancellationToken>()).Returns(new List<AppField> { childRef, childSummaryHours });

        searchService.SearchDocumentsByFilterAsync(
                17L, 200L, Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), 2000, null, Arg.Any<CancellationToken>())
            .Returns(new List<SearchDocumentRow>
            {
                new(Guid.NewGuid(), new Dictionary<string, object?> { ["str_01"] = "10", ["num_01"] = 4.5d }),
                new(Guid.NewGuid(), new Dictionary<string, object?> { ["str_01"] = "10", ["num_01"] = 3.0d }),
                new(Guid.NewGuid(), new Dictionary<string, object?> { ["str_01"] = "11", ["num_01"] = 8.0d }),
            });

        var sut = new RelationalProjector(tableRepo, fieldRepo, recordRepo, relRepo, appRepo, userRepo, formulaProjector,
            queryContext, null, null, searchService, searchOutbox);

        var parentRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 10L },
            new Dictionary<string, object?> { ["Id"] = 11L },
            new Dictionary<string, object?> { ["Id"] = 12L },
        };

        var result = await sut.ProjectAsync(parentTable, [parentSummary], parentRows);

        result[0][50].Should().Be(7.5m);
        result[1][50].Should().Be(8.0m);
        result[2][50].Should().BeNull();
        await recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default, default);
        await recordRepo.DidNotReceiveWithAnyArgs().ListRowsByReferenceAsync(default!, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task RelationalProjector_hydrates_parent_lookup_and_display_key_from_ai_search_without_sql()
    {
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var relRepo = Substitute.For<IRelationshipRepository>();
        var appRepo = Substitute.For<IAppRepository>();
        var userRepo = Substitute.For<IUserRepository>();
        var formulaProjector = Substitute.For<Application.Formulas.IFormulaProjector>();
        var queryContext = Substitute.For<IQueryContext>();
        var searchService = Substitute.For<IAzureSearchService>();
        var searchOutbox = Substitute.For<ISearchOutbox>();

        queryContext.TenantId.Returns(17L);
        searchService.IsGridSearchEnabled.Returns(true);
        searchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        searchOutbox.ListPendingRecordsAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var childTable = new AppTable { Id = 200, AppId = 1, Name = "Tasks" };
        var parentTable = new AppTable { Id = 100, AppId = 1, Name = "Projects", KeyFieldId = 10 };

        var childRef = new AppField
        {
            Id = 6, Fid = 6, AppTableId = 200, Name = "Project", TypeCode = "Reference",
            Settings = """{"relationshipId":1,"parentTableId":100}"""
        };
        var childLookup = new AppField
        {
            Id = 7, Fid = 7, AppTableId = 200, Name = "Project - Budget", TypeCode = "Lookup",
            Settings = """{"relationshipId":1,"referenceFid":6,"sourceTableId":100,"sourceFid":11,"sourceTypeCode":"Number"}"""
        };

        var parentIdField = new AppField { Id = 3, Fid = 3, AppTableId = 100, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id", IsFilterable = true, SearchExact = true, SearchSlot = "num_01" };
        var parentName = new AppField { Id = 10, Fid = 10, AppTableId = 100, Name = "Name", TypeCode = "Text", IsSearchable = true, SearchSlot = "str_01" };
        var parentBudget = new AppField { Id = 11, Fid = 11, AppTableId = 100, Name = "Budget", TypeCode = "Number", IsFilterable = true, SearchExact = true, SearchSlot = "num_02" };

        appRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        relRepo.ListByChildTableAsync(200, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 1, AppId = 1, ParentTableId = 100, ChildTableId = 200, ReferenceFid = 6 }
        });
        tableRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parentTable);
        fieldRepo.ListByTableAsync(100, Arg.Any<CancellationToken>()).Returns(new List<AppField> { parentIdField, parentName, parentBudget });

        searchService.SearchDocumentsByFilterAsync(
                17L, 100L, Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), 2, null, Arg.Any<CancellationToken>())
            .Returns(new List<SearchDocumentRow>
            {
                new(Guid.NewGuid(), new Dictionary<string, object?> { ["num_01"] = 10d, ["str_01"] = "Alpha", ["num_02"] = 5000d }),
                new(Guid.NewGuid(), new Dictionary<string, object?> { ["num_01"] = 11d, ["str_01"] = "Beta", ["num_02"] = 9000d }),
            });

        var sut = new RelationalProjector(tableRepo, fieldRepo, recordRepo, relRepo, appRepo, userRepo, formulaProjector,
            queryContext, null, null, searchService, searchOutbox);

        var childRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_6"] = 10L },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_6"] = 11L },
        };

        var result = await sut.ProjectAsync(childTable, [childRef, childLookup], childRows);

        result[0][6].Should().Be("Alpha");
        result[0][7].Should().Be(5000m);
        result[1][6].Should().Be("Beta");
        result[1][7].Should().Be(9000m);
        await recordRepo.DidNotReceiveWithAnyArgs().GetRowsByIdsAsync(default!, default!, default!, default);
    }
}
