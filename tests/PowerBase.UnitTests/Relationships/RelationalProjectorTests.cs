using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class RelationalProjectorTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();

    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();

    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private RelationalProjector NewProjector() => new(_tableRepo, _fieldRepo, _recordRepo, _relRepo, _appRepo, _userRepo, _formulaProjector, _queryContext);

    public RelationalProjectorTests()
    {
        // A plain (unencrypted) app unless a test says otherwise.
        _appRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(ci => new App { Id = ci.Arg<long>() });
    }

    private static AppField Field(int fid, string name, string typeCode, string? settings = null) => new()
    {
        Id = fid, Fid = fid, Name = name, TypeCode = typeCode, Settings = settings,
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows) =>
        rows.Cast<IReadOnlyDictionary<string, object?>>().ToList();

    [Fact]
    public async Task Lookup_pulls_parent_field_value_onto_child_rows()
    {
        // Child table: reference (fid 10) → parent table 99; lookup (fid 11) pulls parent field fid 5.
        var childFields = new List<AppField>
        {
            Field(10, "Product", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Product - Name", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":5,\"sourceTypeCode\":\"Text\"}"),
        };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L },
            new Dictionary<string, object?> { ["Id"] = 2L, [PhysicalNaming.ColumnName(10)] = null });

        var parentTable = new AppTable { Id = 99, Name = "Product" };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(5, "Name", "Text") });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Widget" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][11].Should().Be("Widget");      // linked → parent's Name
        result[1][11].Should().BeNull();           // unlinked → null
    }

    [Fact]
    public async Task Lookup_reads_only_the_parent_columns_it_pulls_down()
    {
        var childFields = new List<AppField>
        {
            Field(10, "Product", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Product - Name", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":5,\"sourceTypeCode\":\"Text\"}"),
        };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99 });
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(5, "Name", "Text"), Field(6, "Description", "Text"), Field(7, "Notes", "Text") });

        await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, ChildRowLinkedTo42);

        await _recordRepo.Received(1).GetRowsByIdsAsync(Arg.Is<AppTable>(t => t.Id == 99),
            Arg.Is<IReadOnlyList<AppField>>(fs => fs.Select(f => f.Fid!.Value).SequenceEqual(new[] { 5 })),
            Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    // ── Lookups of computed fields (Formula / Lookup) ─────────────────────────────────────────

    private static IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> RowsById(params (long Id, Dictionary<string, object?> Row)[] rows) =>
        rows.ToDictionary(r => r.Id, r => (IReadOnlyDictionary<string, object?>)r.Row);

    private void ParentRowsFor(long tableId, IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> rows) =>
        _recordRepo.GetRowsByIdsAsync(Arg.Is<AppTable>(t => t.Id == tableId), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    /// <summary>The decrypted children of table <paramref name="tableId"/> a summary computed in memory reads — the
    /// children of the page's parents, picked out in SQL by the (never encrypted) reference column.</summary>
    private void ChildRowsByReference(long tableId, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        _recordRepo.ListRowsByReferenceAsync(Arg.Is<AppTable>(t => t.Id == tableId), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(),
                Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    /// <summary>The formula projector as the real one treats the seed: merged into the output.</summary>
    private void FormulaProjectorReturnsSeed() =>
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(2)
                ?? ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1).Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());

    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> ChildRowLinkedTo42 = Rows(
        new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L });

    [Fact]
    public async Task Lookup_of_a_formula_field_pulls_the_computed_value()
    {
        var childFields = new List<AppField>
        {
            Field(10, "Employee", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Employee - Code", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":5,\"sourceTypeCode\":\"Formula\"}"),
        };
        var parentFields = new List<AppField> { Field(5, "Code", "Formula") };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99 });
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(parentFields);
        ParentRowsFor(99, RowsById((42L, new Dictionary<string, object?> { ["Id"] = 42L })));
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(new List<IReadOnlyDictionary<long, object?>> { new Dictionary<long, object?> { [5] = "E-001" } });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, ChildRowLinkedTo42);

        result[0][11].Should().Be("E-001");
    }

    [Fact]
    public async Task Lookup_of_a_lookup_follows_the_chain_to_the_underlying_value()
    {
        // Salary(child) → Payroll(99) → Employee(88): Salary's lookup pulls Payroll's lookup of Employee's Code.
        var salaryFields = new List<AppField>
        {
            Field(10, "Payroll", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Payroll - Employee Code", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":6,\"sourceTypeCode\":\"Text\"}"),
        };
        var payrollFields = new List<AppField>
        {
            Field(20, "Employee", "Reference", "{\"relationshipId\":2,\"parentTableId\":88}"),
            Field(6, "Employee - Code", "Lookup", "{\"relationshipId\":2,\"referenceFid\":20,\"sourceTableId\":88,\"sourceFid\":5,\"sourceTypeCode\":\"Text\"}"),
        };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99 });
        _tableRepo.GetByIdAsync(88, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 88 });
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(payrollFields);
        _fieldRepo.ListByTableAsync(88, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(5, "Code", "Text") });
        ParentRowsFor(99, RowsById((42L, new Dictionary<string, object?> { ["Id"] = 42L, [PhysicalNaming.ColumnName(20)] = 7L })));
        ParentRowsFor(88, RowsById((7L, new Dictionary<string, object?> { ["Id"] = 7L, [PhysicalNaming.ColumnName(5)] = "E-001" })));
        FormulaProjectorReturnsSeed();

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, salaryFields, ChildRowLinkedTo42);

        result[0][11].Should().Be("E-001");
    }

    [Fact]
    public async Task Lookup_cycle_stops_at_the_chain_cap_and_stays_blank()
    {
        // A table whose lookup reads its own lookup through a self-reference: would recurse forever uncapped.
        var fields = new List<AppField>
        {
            Field(10, "Self", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Self - Loop", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":11,\"sourceTypeCode\":\"Text\"}"),
        };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99 });
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(fields);
        ParentRowsFor(99, RowsById((42L, new Dictionary<string, object?> { ["Id"] = 42L, [PhysicalNaming.ColumnName(10)] = 42L })));
        FormulaProjectorReturnsSeed();

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 99 }, fields, ChildRowLinkedTo42);

        result[0][11].Should().BeNull();
    }

    private static readonly List<AppField> PlainChildFields =[Field(10, "Item", "Reference"), Field(30, "Qty", "Number")];

    private void CountReturns(Dictionary<object, object?> result) =>
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>> ProjectOneSummary(string settings, long appId = 0) =>
        NewProjector().ProjectAsync(new AppTable { Id = 5, AppId = appId },
            new List<AppField> { Field(20, "S", "Summary", settings) },
            Rows(new Dictionary<string, object?> { ["Id"] = 1L }, new Dictionary<string, object?> { ["Id"] = 2L }));

    [Fact]
    public async Task Summary_matches_decimal_reference_keys_to_row_ids()
    {
        // A reference converted from a Number field is a DECIMAL column: keys come back as 1.0000m, rows are 1L.
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(PlainChildFields);
        CountReturns(new Dictionary<object, object?> { [1.0000m] = 3 });

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Count\"}");

        result[0][20].Should().Be(3);
        result[1][20].Should().Be(0);
    }

    [Fact]
    public async Task Summary_with_lowercase_function_from_an_old_import_is_computed()
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(PlainChildFields);
        CountReturns(new Dictionary<object, object?> { [1L] = 12m });

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"sum\",\"targetFid\":30}");

        result[0][20].Should().Be(12m);
        await _recordRepo.Received(1).AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Sum", 30,
            Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
    }

    // ── Matching criteria: resolved like a report's filter before reaching SQL ─────────────────

    private static readonly List<AppField> CriteriaChildFields =
        [Field(10, "Item", "Reference"), Field(40, "Due", "Date"), Field(41, "Owner", "User")];

    /// <summary>Projects a Count summary saved with these criteria; returns the filter and field
    /// lookup the Count query was given.</summary>
    private async Task<(Application.Reports.FilterGroup? Filter, IReadOnlyDictionary<long, AppField>? Fields)> CriteriaSentToSql(string criteria)
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(CriteriaChildFields);
        Application.Reports.FilterGroup? filter = null;
        IReadOnlyDictionary<long, AppField>? fields = null;
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null, Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Do<Application.Reports.FilterGroup?>(f => filter = f), Arg.Any<string?>(),
                Arg.Do<IReadOnlyDictionary<long, AppField>?>(d => fields = d), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?>());

        await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(
            new { childTableId = 77, referenceFid = 10, function = "Count", filterTree = criteria }));
        return (filter, fields);
    }

    // ── Matching criteria comparing to the parent record's own field ──────────────────────────

    private const string BetweenParentDates =
        """{"logic":"and","nodes":[{"condition":{"fieldId":40,"operator":"gte","valueMode":"parentField","valueFieldId":6}},{"condition":{"fieldId":40,"operator":"lte","valueMode":"parentField","valueFieldId":7}}]}""";

    [Fact]
    public async Task Summary_criteria_on_parent_fields_reads_the_whole_parent_table_and_passes_its_scope()
    {
        // The projected field list holds only the summary (e.g. what the viewer's role can see);
        // the parent's Start/End Date still have to be known, so they come from the table itself.
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(6, "Start Date", "Date"), Field(7, "End Date", "Date") });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(CriteriaChildFields);
        Application.Reports.FilterGroup? filter = null;
        ParentFieldScope? scope = null;
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null, Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Do<Application.Reports.FilterGroup?>(f => filter = f), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(),
                Arg.Do<ParentFieldScope?>(s => scope = s), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 4 });

        var result = await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(
            new { childTableId = 77, referenceFid = 10, function = "Count", filterTree = BetweenParentDates }));

        result[0][20].Should().Be(4);
        scope.Should().NotBeNull();
        scope!.ParentTableId.Should().Be(5);
        scope.ReferenceFid.Should().Be(10);
        scope.ParentFieldsByFid.Keys.Should().BeEquivalentTo(new long[] { 6, 7 });
        filter!.Nodes.Select(n => n.Condition!.ValueMode).Should().AllBe("parentField");   // left for SQL, not resolved away
    }

    [Fact]
    public async Task Summary_criteria_without_parent_fields_does_not_load_the_parent_table()
    {
        await CriteriaSentToSql("""{"logic":"and","nodes":[{"condition":{"fieldId":40,"operator":"eq","value":"2026-09-28"}}]}""");

        await _fieldRepo.DidNotReceive().ListByTableAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Summary_criteria_on_a_deleted_parent_field_is_shown_blank_not_computed_without_it()
    {
        // End Date (fid 7) is gone: dropping its condition would count children past the end.
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(6, "Start Date", "Date") });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(CriteriaChildFields);

        var result = await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(
            new { childTableId = 77, referenceFid = 10, function = "Count", filterTree = BetweenParentDates }));

        result[0][20].Should().BeNull();
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Summary_criteria_on_an_encrypted_parent_field_is_computed_in_memory_per_parent()
    {
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(6, "Start Date", "Date"), new() { Id = 7, Fid = 7, Name = "End Date", TypeCode = "Date", IsEncrypted = true } });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(CriteriaChildFields);
        ParentRowsFor(5, RowsById(
            (1L, new Dictionary<string, object?> { ["Id"] = 1L, ["f_6"] = "2026-01-01", ["f_7"] = "2026-12-31" }),
            (2L, new Dictionary<string, object?> { ["Id"] = 2L, ["f_6"] = "2026-01-01", ["f_7"] = "2026-03-01" })));
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1", ["f_40"] = "2026-06-01" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1", ["f_40"] = "2027-02-01" },
            new Dictionary<string, object?> { ["Id"] = 3L, ["f_10"] = "2", ["f_40"] = "2026-06-01" }));

        var result = await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(
            new { childTableId = 77, referenceFid = 10, function = "Count", filterTree = BetweenParentDates }));

        result[0][20].Should().Be(1);   // only the June task is inside parent 1's year
        result[1][20].Should().Be(0);   // parent 2 ends in March: its June task is outside
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
        // Only the page's parents (and their children) are read — never a whole table.
        await _recordRepo.Received(1).GetRowsByIdsAsync(Arg.Is<AppTable>(t => t.Id == 5), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.OrderBy(i => i).SequenceEqual(new long[] { 1, 2 })), Arg.Any<CancellationToken>());
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllRowsDecryptedAsync(default!, default!, default);
    }

    [Fact]
    public async Task Summary_criteria_on_a_keyed_reference_compare_the_key_it_shows()
    {
        // "Related D" (fid 12) points to Departments (9), whose display key is Department Name: "Related D is Sales"
        // reads that key, not the Record ID# the column holds.
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _tableRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 9 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"),
            Field(12, "Related D", "Reference", """{"relationshipId":181,"parentTableId":9,"originalTypeCode":null}"""),
        });
        _fieldRepo.ListByTableAsync(9, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(6, "Department Name", "Text") });
        _relRepo.ListByChildTableAsync(77, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 181, ParentTableId = 9, ChildTableId = 77, ReferenceFid = 12, DisplayKeyFieldId = 6 } });
        Application.Reports.FilterGroup? filter = null;
        IReadOnlyDictionary<long, AppField>? fields = null;
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null, Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Do<Application.Reports.FilterGroup?>(f => filter = f), Arg.Any<string?>(),
                Arg.Do<IReadOnlyDictionary<long, AppField>?>(d => fields = d), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?>());

        await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(new
        {
            childTableId = 77, referenceFid = 10, function = "Count",
            filterTree = """{"logic":"and","nodes":[{"condition":{"fieldId":12,"operator":"eq","value":"Sales"}}]}""",
        }));

        var standIn = ReferenceKeys.StandInFid(12);
        filter!.Nodes.Single().Condition!.FieldId.Should().Be(standIn);
        fields![standIn].TypeCode.Should().Be("Lookup");
        SummaryLookupSources.Settings(fields[standIn])!.SourceFid.Should().Be(6);
        SummaryLookupSources.Settings(fields[standIn])!.SourceTableId.Should().Be(9);
        fields[12].TypeCode.Should().Be("Reference");   // its own column — the Record ID# — is left alone
    }

    [Fact]
    public async Task Summary_criteria_on_a_reference_whose_parent_sets_no_key_compare_its_Record_ID()
    {
        // Converted from a Text field, but Departments (9) set no key: the reference shows — and is compared as — Record ID#.
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _tableRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 9 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"),
            Field(12, "Related D", "Reference", """{"relationshipId":181,"parentTableId":9,"originalTypeCode":"Text"}"""),
        });
        _fieldRepo.ListByTableAsync(9, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(6, "Department Name", "Text") });
        _relRepo.ListByChildTableAsync(77, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 181, ParentTableId = 9, ChildTableId = 77, ReferenceFid = 12 } });
        Application.Reports.FilterGroup? filter = null;
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null, Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Do<Application.Reports.FilterGroup?>(f => filter = f), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?>());

        await ProjectOneSummary("""{"childTableId":77,"referenceFid":10,"function":"Count","filterTree":"{\"logic\":\"and\",\"nodes\":[{\"condition\":{\"fieldId\":12,\"operator\":\"eq\",\"value\":\"4\"}}]}"}""");

        filter!.Nodes.Single().Condition!.FieldId.Should().Be(12);
    }

    [Fact]
    public async Task Summary_criteria_on_a_reference_with_an_encrypted_key_are_judged_in_memory()
    {
        // Departments' key, Department Name, is encrypted: SQL can't compare its ciphertext, so the rows are decrypted.
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _tableRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 9 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"),
            Field(12, "Related D", "Reference", """{"relationshipId":181,"parentTableId":9}"""),
        });
        var departmentName = Field(6, "Department Name", "Text");
        departmentName.IsEncrypted = true;
        _fieldRepo.ListByTableAsync(9, Arg.Any<CancellationToken>()).Returns(new List<AppField> { departmentName });
        _relRepo.ListByChildTableAsync(77, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 181, ParentTableId = 9, ChildTableId = 77, ReferenceFid = 12, DisplayKeyFieldId = 6 } });

        await ProjectOneSummary("""{"childTableId":77,"referenceFid":10,"function":"Count","filterTree":"{\"logic\":\"and\",\"nodes\":[{\"condition\":{\"fieldId\":12,\"operator\":\"eq\",\"value\":\"Sales\"}}]}"}""");

        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default, default);
        await _recordRepo.Received().ListRowsByReferenceAsync(Arg.Is<AppTable>(t => t.Id == 77), Arg.Any<IReadOnlyList<AppField>>(), 10,
            Arg.Any<IReadOnlyCollection<object>>(), Arg.Is<Application.Reports.FilterGroup?>(f => f == null),
            Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllRowsDecryptedAsync(default!, default!, default);
    }

    [Fact]
    public async Task Combined_text_of_a_keyed_reference_joins_the_keys_it_shows()
    {
        // "Related Client" (fid 12) was created as a Reference; Clients (9) show Client Name as their key: Combined Text
        // joins client names, not Record IDs.
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _tableRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 9, KeyFieldId = 6 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"),
            Field(12, "Related Client", "Reference", """{"relationshipId":181,"parentTableId":9,"originalTypeCode":null}"""),
        });
        _fieldRepo.ListByTableAsync(9, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(6, "Client Name", "Text") });
        _relRepo.ListByChildTableAsync(77, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 181, ParentTableId = 9, ChildTableId = 77, ReferenceFid = 12 } });
        var standIn = ReferenceKeys.StandInFid(12);
        _recordRepo.ListValuesByReferenceAsync(Arg.Any<AppTable>(), 10, standIn, Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(),
                Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object, object)> { (1L, "Sales"), (1L, "Marketing") });

        var result = await ProjectOneSummary("""{"childTableId":77,"referenceFid":10,"function":"CombinedText","targetFid":12}""");

        result[0][20].Should().Be("Sales, Marketing");
    }

    [Fact]
    public async Task Summary_criteria_on_a_reference_is_judged_as_a_number_in_memory()
    {
        // In an encrypted app a Text criterion (Note) is ciphertext to SQL, so the summary is judged in memory: there
        // "Related Item > 9" keeps 10 — as text, "10" sorts before "9".
        _appRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(new App { Id = 42, IsEncrypted = true });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, AppId = 42 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(12, "Related Item", "Reference"), Field(13, "Note", "Text") });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1", ["f_12"] = 10L, ["f_13"] = "a" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1", ["f_12"] = 9L, ["f_13"] = "b" }));

        var result = await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(new
        {
            childTableId = 77, referenceFid = 10, function = "Count",
            filterTree = """{"logic":"and","nodes":[{"condition":{"fieldId":12,"operator":"gt","value":"9"}},{"condition":{"fieldId":13,"operator":"isNotEmpty"}}]}""",
        }), appId: 42);

        result[0][20].Should().Be(1);
    }

    // ── … comparing to a parent Lookup / Summary / Formula (no column: each parent's value is worked out first) ──

    private const string AvgSalarySettings = """{"childTableId":77,"referenceFid":10,"function":"Avg","targetFid":30}""";

    /// <summary>Department (table 5) › Employees (77): "# Above Average" counts the employees whose Salary is greater
    /// than their own department's "Avg Salary" summary. Returns the scope the Count query was given.</summary>
    private async Task<(IReadOnlyList<IReadOnlyDictionary<long, object?>> Result, ParentFieldScope? Scope)> ProjectAboveAverage(
        string comparedSummarySettings)
    {
        var criteria = """{"logic":"and","nodes":[{"condition":{"fieldId":30,"operator":"gt","valueMode":"parentField","valueFieldId":8}}]}""";
        var aboveAverage = Field(20, "# Above Average", "Summary", System.Text.Json.JsonSerializer.Serialize(
            new { childTableId = 77, referenceFid = 10, function = "Count", filterTree = criteria }));
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(8, "Avg Salary", "Summary", comparedSummarySettings), aboveAverage });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(10, "Department", "Reference"), Field(30, "Salary", "Currency") });
        ParentRowsFor(5, RowsById((1L, new Dictionary<string, object?> { ["Id"] = 1L }), (2L, new Dictionary<string, object?> { ["Id"] = 2L })));
        FormulaProjectorReturnsSeed();
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Avg", 30, Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 50000m, [2L] = 72000m });
        ParentFieldScope? scope = null;
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null, Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(),
                Arg.Do<ParentFieldScope?>(s => scope = s), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 3, [2L] = 1 });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5 }, new List<AppField> { aboveAverage },
            Rows(new Dictionary<string, object?> { ["Id"] = 1L }, new Dictionary<string, object?> { ["Id"] = 2L }));
        return (result, scope);
    }

    [Fact]
    public async Task Summary_criteria_on_a_parent_summary_passes_each_parents_value_to_sql()
    {
        var (result, scope) = await ProjectAboveAverage(AvgSalarySettings);

        result[0][20].Should().Be(3);
        result[1][20].Should().Be(1);
        scope!.ComputedValues.Should().NotBeNull();
        scope.ComputedValues![8].Should().BeEquivalentTo(new Dictionary<long, object?> { [1L] = 50000m, [2L] = 72000m });
    }

    [Fact]
    public async Task Summary_criteria_on_a_parent_summary_that_compares_back_to_it_is_shown_blank()
    {
        // "Avg Salary" itself only averages the employees above "# Above Average" — neither can be worked out first.
        var (result, _) = await ProjectAboveAverage(System.Text.Json.JsonSerializer.Serialize(new
        {
            childTableId = 77, referenceFid = 10, function = "Avg", targetFid = 30,
            filterTree = """{"logic":"and","nodes":[{"condition":{"fieldId":30,"operator":"gt","valueMode":"parentField","valueFieldId":20}}]}""",
        }));

        result[0][20].Should().BeNull();
        result[1][20].Should().BeNull();
        await _recordRepo.DidNotReceive().AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<object>>(),
            Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Summary_criteria_on_a_stored_parent_field_works_out_no_parent_values()
    {
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(6, "Start Date", "Date"), Field(7, "End Date", "Date") });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(CriteriaChildFields);
        CountReturns(new Dictionary<object, object?>());

        await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(
            new { childTableId = 77, referenceFid = 10, function = "Count", filterTree = BetweenParentDates }));

        await _recordRepo.DidNotReceiveWithAnyArgs().GetRowsByIdsAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Summary_criteria_relative_date_becomes_todays_date()
    {
        var (filter, _) = await CriteriaSentToSql(
            """{"logic":"and","nodes":[{"condition":{"fieldId":40,"operator":"date_eq","valueMode":"today"}}]}""");

        var cond = filter!.Nodes.Single().Condition!;
        cond.Value.Should().Be(DateTime.UtcNow.Date.ToString("yyyy-MM-dd"));
        cond.ValueMode.Should().Be("literal");
    }

    [Fact]
    public async Task Summary_criteria_is_during_becomes_a_date_range_inside_its_group()
    {
        var (filter, _) = await CriteriaSentToSql(
            """{"logic":"or","nodes":[{"group":{"logic":"and","nodes":[{"condition":{"fieldId":40,"operator":"during","valueMode":"duringCurrent","value":"1:week"}}]}}]}""");

        var range = filter!.Nodes.Single().Group!.Nodes.Single().Group!;
        range.Logic.Should().Be("and");
        range.Nodes.Select(n => n.Condition!.Operator).Should().Equal("gte", "lte");
    }

    [Fact]
    public async Task Summary_criteria_picked_user_becomes_the_stored_user_id()
    {
        var picked = Guid.NewGuid();
        _userRepo.GetByPublicIdAsync(picked, Arg.Any<CancellationToken>()).Returns(new User { Id = 7 });

        var (filter, _) = await CriteriaSentToSql(
            $$$"""{"logic":"and","nodes":[{"condition":{"fieldId":41,"operator":"eq","value":"{{{picked}}}"}}]}""");

        filter!.Nodes.Single().Condition!.Value.Should().Be("7");
    }

    [Fact]
    public async Task Summary_criteria_is_built_with_the_child_field_types()
    {
        var (_, fields) = await CriteriaSentToSql(
            """{"logic":"and","nodes":[{"condition":{"fieldId":40,"operator":"isEmpty"}}]}""");

        fields.Should().NotBeNull();
        fields![40].TypeCode.Should().Be("Date");
    }

    // ── Encrypted apps: only what reads ciphertext is computed in memory ─────────────────────────

    [Theory]
    [InlineData("""{"childTableId":77,"referenceFid":10,"function":"Count"}""", "Count", null)]
    [InlineData("""{"childTableId":77,"referenceFid":10,"function":"Sum","targetFid":30}""", "Sum", 30)]
    public async Task Summary_in_an_encrypted_app_over_plain_columns_stays_a_sql_aggregate(string settings, string function, int? targetFid)
    {
        // The reference (a Record ID#) and a Number are never encrypted, even in an encrypted app: SQL can group and sum them.
        _appRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(new App { Id = 3, IsEncrypted = true });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, AppId = 3 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(PlainChildFields);
        CountReturns(new Dictionary<object, object?> { [1L] = 2, [2L] = 1 });

        var result = await ProjectOneSummary(settings, appId: 3);

        result[0][20].Should().Be(2);
        result[1][20].Should().Be(1);
        await _recordRepo.Received(1).AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, function, targetFid, Arg.Any<IReadOnlyCollection<object>>(),
            Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllRowsDecryptedAsync(default!, default!, default);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListRowsByReferenceAsync(default!, default!, default, default!, default, default, default, default);
    }

    private static readonly List<AppField> StatusChildFields = [Field(10, "Item", "Reference"), Field(31, "Status", "Text")];

    private static string CountWhereStatusIs(string status) => System.Text.Json.JsonSerializer.Serialize(new
    {
        childTableId = 77, referenceFid = 10, function = "Count",
        filterTree = $$$"""{"logic":"and","nodes":[{"condition":{"fieldId":31,"operator":"eq","value":"{{{status}}}"}}]}""",
    });

    /// <summary>A parent key or reference value (a boxed Record ID#) as a number.</summary>
    private static long KeyOf(object? key) => Convert.ToInt64(key, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Each read of table 77's children by reference: the parent keys asked for, and the criteria sent to SQL.</summary>
    private readonly List<(IReadOnlyCollection<object> Keys, Application.Reports.FilterGroup? Filter)> _childReads = new();

    /// <summary>An encrypted app (3) whose child table (77) holds these decrypted children: each read returns the
    /// children of the parent keys it is asked for, as the SQL does, and is recorded in <see cref="_childReads"/>.</summary>
    private void EncryptedAppChildren(params Dictionary<string, object?>[] children)
    {
        _appRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(new App { Id = 3, IsEncrypted = true });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, AppId = 3 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(StatusChildFields);
        _recordRepo.ListRowsByReferenceAsync(Arg.Is<AppTable>(t => t.Id == 77), Arg.Any<IReadOnlyList<AppField>>(), 10,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(),
                Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var asked = ci.ArgAt<IReadOnlyCollection<object>>(3);
                _childReads.Add((asked, ci.ArgAt<Application.Reports.FilterGroup?>(4)));
                var keys = asked.Select(KeyOf).ToHashSet();
                return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)children
                    .Where(c => keys.Contains(KeyOf(c["f_10"])))
                    .Cast<IReadOnlyDictionary<string, object?>>().ToList();
            });
    }

    [Fact]
    public async Task Summary_judged_in_memory_reads_only_the_children_of_the_pages_parents()
    {
        // Status is Text, ciphertext to SQL in an encrypted app: the criteria are judged on decrypted rows — but only the
        // children of this page's parents are read, picked out by the plain reference column.
        EncryptedAppChildren(
            new() { ["Id"] = 1L, ["f_10"] = 1L, ["f_31"] = "Open" },
            new() { ["Id"] = 2L, ["f_10"] = 1L, ["f_31"] = "Open" },
            new() { ["Id"] = 3L, ["f_10"] = 1L, ["f_31"] = "Closed" },
            new() { ["Id"] = 4L, ["f_10"] = 2L, ["f_31"] = "Open" });

        var result = await ProjectOneSummary(CountWhereStatusIs("Open"), appId: 3);

        result[0][20].Should().Be(2);
        result[1][20].Should().Be(1);
        _childReads.Should().ContainSingle();
        _childReads[0].Keys.Should().BeEquivalentTo(new object[] { 1L, 2L });
        _childReads[0].Filter.Should().BeNull();   // the criteria never reach SQL: they'd compare ciphertext
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllRowsDecryptedAsync(default!, default!, default);
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Summaries_over_the_same_children_share_one_read_but_other_parents_are_read_again()
    {
        EncryptedAppChildren(
            new() { ["Id"] = 1L, ["f_10"] = 1L, ["f_31"] = "Open" },
            new() { ["Id"] = 2L, ["f_10"] = 1L, ["f_31"] = "Closed" },
            new() { ["Id"] = 3L, ["f_10"] = 2L, ["f_31"] = "Open" },
            new() { ["Id"] = 4L, ["f_10"] = 3L, ["f_31"] = "Open" });
        var parent = new AppTable { Id = 5, AppId = 3 };
        var summaries = new List<AppField> { Field(20, "Open", "Summary", CountWhereStatusIs("Open")), Field(21, "Closed", "Summary", CountWhereStatusIs("Closed")) };
        static IReadOnlyList<IReadOnlyDictionary<string, object?>> Parents(params long[] ids) =>
            ids.Select(id => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = id }).ToList();
        var projector = NewProjector();   // one per request: a report reads the page, then (say) every row for its totals

        var page = await projector.ProjectAsync(parent, summaries, Parents(1, 2));
        var subset = await projector.ProjectAsync(parent, summaries, Parents(1));
        var other = await projector.ProjectAsync(parent, summaries, Parents(3));

        page[0][20].Should().Be(1); page[0][21].Should().Be(1); page[1][20].Should().Be(1); page[1][21].Should().Be(0);
        subset[0][20].Should().Be(1);
        other[0][20].Should().Be(1);   // parent 3's child — not missing because the rows read for parents 1 and 2 were kept
        // Parents 1 and 2 (both summaries, then parent 1 again): one read. Parent 3: a read of its own.
        _childReads.Select(r => r.Keys.Select(KeyOf).Order().ToList()).Should()
            .BeEquivalentTo(new[] { new long[] { 1, 2 }, new long[] { 3 } }, o => o.WithStrictOrdering());
    }

    private void EncryptedChildRows() =>
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1", ["f_30"] = "5" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1.0000", ["f_30"] = "7" },
            new Dictionary<string, object?> { ["Id"] = 3L, ["f_10"] = "2", ["f_30"] = "3" }));

    [Fact]
    public async Task Sum_over_an_encrypted_field_is_computed_in_memory()
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"),
            new() { Id = 30, Fid = 30, Name = "Qty", TypeCode = "Number", IsEncrypted = true },
        });
        EncryptedChildRows();

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Sum\",\"targetFid\":30}");

        result[0][20].Should().Be(12m);
        result[1][20].Should().Be(3m);
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Combined_text_over_a_lookup_in_an_encrypted_app_reads_the_decrypted_source_value()
    {
        // Child (77): Item reference (fid 10) to the parent being summarized, plus a lookup (fid 32) of
        // Name (fid 6) on table 99 through the child's reference fid 11.
        _appRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(new App { Id = 3, IsEncrypted = true });
        var lookup = Field(32, "Project Name", "Lookup", "{\"sourceTableId\":99,\"referenceFid\":11,\"sourceFid\":6}");
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, AppId = 3 });
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99, AppId = 3 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(11, "Project", "Reference"), lookup });
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(6, "Name", "Text") });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1", ["f_11"] = "100" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1", ["f_11"] = "101" }));
        ParentRowsFor(99, RowsById(
            (100L, new Dictionary<string, object?> { ["Id"] = 100L, ["f_6"] = "Alpha" }),
            (101L, new Dictionary<string, object?> { ["Id"] = 101L, ["f_6"] = "Beta" })));

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":32}", appId: 3);

        result[0][20].Should().Be("Alpha, Beta");
        result[1][20].Should().BeNull();
        // The lookup has no column of its own: the children's Project reference (11) is read for it.
        ChildColumnReads().Should().BeEquivalentTo(new[] { new[] { 10, 11 } });
        // Only the projects these children point to are read — not the whole project table.
        await _recordRepo.Received(1).GetRowsByIdsAsync(Arg.Is<AppTable>(t => t.Id == 99), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.OrderBy(i => i).SequenceEqual(new long[] { 100, 101 })), Arg.Any<CancellationToken>());
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllRowsDecryptedAsync(default!, default!, default);
    }

    [Fact]
    public async Task Sum_over_a_lookup_of_a_summary_in_an_encrypted_app_is_worked_out_per_decrypted_child_row()
    {
        // Department (5) › Employees (77), in an encrypted app: "Sum of Department - Min of Salary" sums each employee's
        // lookup (fid 31) of the department's own "Min of Salary" summary — no column, so worked out per decrypted row.
        const int lookupFid = 31;
        _appRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(new App { Id = 3, IsEncrypted = true });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, AppId = 3 });
        _tableRepo.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 5, AppId = 3 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":5}"),
            Field(lookupFid, "Department - Min of Salary", "Lookup",
                "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":5,\"sourceFid\":8,\"sourceTypeCode\":\"Summary\",\"resultTypeCode\":\"Currency\"}"),
        });
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(8, "Min of Salary", "Summary", "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Min\",\"targetTypeCode\":\"Currency\"}") });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>());
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1" }));
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(new List<IReadOnlyDictionary<long, object?>>
            {
                new Dictionary<long, object?> { [lookupFid] = 40000m },
                new Dictionary<long, object?> { [lookupFid] = 52000.5m },
            });

        var result = await ProjectOneSummary($"{{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Sum\",\"targetFid\":{lookupFid}}}", appId: 3);

        result[0][20].Should().Be(92000.5m);
        result[1][20].Should().BeNull();   // parent 2 has no employees
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Combined_text_over_an_encrypted_field_is_joined_in_memory()
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"),
            new() { Id = 31, Fid = 31, Name = "Note", TypeCode = "Text", IsEncrypted = true },
        });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1", ["f_31"] = "alpha" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1", ["f_31"] = "beta" }));

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":31,\"delimiter\":\"|\"}");

        result[0][20].Should().Be("alpha|beta");
        result[1][20].Should().BeNull();
        await _recordRepo.DidNotReceiveWithAnyArgs().ListValuesByReferenceAsync(default!, default, default, default, default!, default, default, default, default, default);
    }

    // ── Which child columns a summary computed in memory reads (each is decrypted for every child) ─────────

    private static AppField EncryptedField(int fid, string name, string typeCode = "Text") => new()
    {
        Id = fid, Fid = fid, Name = name, TypeCode = typeCode, IsEncrypted = true,
    };

    /// <summary>The Fids of the fields whose columns each read of children by reference asked for, in order.</summary>
    private List<int[]> ChildColumnReads() => _recordRepo.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(IRecordRepository.ListRowsByReferenceAsync))
        .Select(c => ((IReadOnlyList<AppField>)c.GetArguments()[1]!).Select(f => f.Fid!.Value).Order().ToArray())
        .ToList();

    [Fact]
    public async Task Combined_text_in_memory_reads_and_decrypts_only_the_reference_and_the_target()
    {
        // Employees hold a name, an email and a phone, all encrypted: joining the names needs no email or phone, which
        // would otherwise be decrypted for every employee of the page's departments.
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Department", "Reference"), EncryptedField(31, "Name"), EncryptedField(32, "Email"), EncryptedField(33, "Phone"),
        });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = 1L, ["f_31"] = "Asha" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = 1L, ["f_31"] = "Ravi" }));

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":31}");

        result[0][20].Should().Be("Asha, Ravi");
        ChildColumnReads().Should().BeEquivalentTo(new[] { new[] { 10, 31 } });
    }

    [Fact]
    public async Task Combined_text_in_memory_also_reads_the_columns_it_sorts_and_matches_on()
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Department", "Reference"), EncryptedField(31, "Name"), EncryptedField(32, "Email"), EncryptedField(33, "Phone"),
            Field(34, "Joined", "Date"),
        });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = 1L, ["f_31"] = "Asha", ["f_32"] = "a@x.com", ["f_34"] = new DateTime(2026, 2, 1) },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = 1L, ["f_31"] = "Ravi", ["f_32"] = "r@x.com", ["f_34"] = new DateTime(2026, 1, 1) }));

        await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(new
        {
            childTableId = 77, referenceFid = 10, function = "CombinedText", targetFid = 31, sortFid = 34,
            filterTree = """{"logic":"and","nodes":[{"condition":{"fieldId":32,"operator":"eq","value":"a@x.com"}}]}""",
        }));

        ChildColumnReads().Should().BeEquivalentTo(new[] { new[] { 10, 31, 32, 34 } });
    }

    [Fact]
    public async Task Summary_in_memory_over_a_formula_still_reads_every_column_the_formula_might()
    {
        // The criteria compare a formula, worked out per child from whatever fields it reads: every column is read.
        const int formulaFid = 32;
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"), Field(formulaFid, "F", "Formula_Number"), Field(33, "Qty", "Number"),
        });
        ChildRowsByReference(77, Rows(new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = 1L, ["f_33"] = 2m }));
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ((IReadOnlyList<IReadOnlyDictionary<string, object?>>)ci[1]).Select(r =>
                (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [formulaFid] = 20m }).ToList());

        await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(new
        {
            childTableId = 77, referenceFid = 10, function = "Count",
            filterTree = """{"logic":"and","nodes":[{"condition":{"fieldId":32,"operator":"gt","value":"15"}}]}""",
        }));

        ChildColumnReads().Should().BeEquivalentTo(new[] { new[] { 10, 33 } });   // the formula has no column of its own
    }

    [Fact]
    public async Task A_later_summary_needing_another_column_reads_again_with_both_and_the_rest_share_it()
    {
        _appRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(new App { Id = 3, IsEncrypted = true });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, AppId = 3 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(31, "Status", "Text"), Field(32, "Name", "Text") });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = 1L, ["f_31"] = "Open", ["f_32"] = "Alpha" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = 1L, ["f_31"] = "Closed", ["f_32"] = "Beta" }));
        var summaries = new List<AppField>
        {
            Field(20, "Open", "Summary", CountWhereStatusIs("Open")),
            Field(21, "Names", "Summary", "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":32}"),
            Field(22, "Closed", "Summary", CountWhereStatusIs("Closed")),
        };

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5, AppId = 3 }, summaries,
            Rows(new Dictionary<string, object?> { ["Id"] = 1L }));

        result[0][20].Should().Be(1); result[0][21].Should().Be("Alpha, Beta"); result[0][22].Should().Be(1);
        ChildColumnReads().Should().BeEquivalentTo(new[] { new[] { 10, 31 }, new[] { 10, 31, 32 } }, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("Sum", "Formula_Time")]        // a formula result that can't be summarized
    [InlineData("DistinctCount", "Formula_Duration")]   // a function the formula's result type doesn't support
    [InlineData("Sum", "Summary")]             // no column, and not a formula
    [InlineData("CombinedText", "Formula_Url")]   // a link: no value to show
    [InlineData("Max", "User")]                // saved before today: a user can't be ranked
    public async Task Summary_saved_under_older_rules_is_blank_and_never_computed(string function, string targetType)
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(32, "T", targetType) });

        var result = await ProjectOneSummary($"{{\"childTableId\":77,\"referenceFid\":10,\"function\":\"{function}\",\"targetFid\":32}}");

        result[0][20].Should().BeNull();
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListValuesByReferenceAsync(default!, default, default, default, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task Summary_criteria_on_a_formula_field_is_judged_on_its_computed_value()
    {
        const int formulaFid = 32;
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(formulaFid, "F", "Formula_Number") });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 3L, ["f_10"] = 2L }));
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ((IReadOnlyList<IReadOnlyDictionary<string, object?>>)ci[1]).Select(r =>
                (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [formulaFid] = (decimal)(long)r["Id"] * 10 }).ToList());

        var result = await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(new
        {
            childTableId = 77, referenceFid = 10, function = "Count",
            filterTree = """{"logic":"and","nodes":[{"condition":{"fieldId":32,"operator":"gt","value":"15"}}]}""",
        }));

        result[0][20].Should().Be(1);   // formula values 10 and 20: only 20 > 15
        result[1][20].Should().Be(1);   // 30
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Summary_over_formula_field_with_encrypted_reference_is_computed_in_memory()
    {
        const int formulaFid = 32;
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        // The reference is encrypted in an otherwise plain app, so the SQL path can't match it.
        var reference = Field(10, "Item", "Reference");
        reference.IsEncrypted = true;
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { reference, Field(formulaFid, "F", "Formula_Number") });
        _recordRepo.ListAllRowsDecryptedAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Rows(
                new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1" },
                new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1" },
                new Dictionary<string, object?> { ["Id"] = 3L, ["f_10"] = "2" }));
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ((IReadOnlyList<IReadOnlyDictionary<string, object?>>)ci[1]).Select(r =>
                (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [formulaFid] = (decimal)(long)r["Id"] * 10 }).ToList());

        var result = await ProjectOneSummary($"{{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Sum\",\"targetFid\":{formulaFid}}}");

        result[0][20].Should().Be(30m);   // rows 1 and 2 → 10 + 20
        result[1][20].Should().Be(30m);   // row 3 → 30
        await _recordRepo.DidNotReceiveWithAnyArgs().ListRowsByReferenceAsync(default!, default!, default, default!, default, default, default, default);
    }

    [Theory]
    [InlineData("Sum", "Formula_Number", new object?[] { 10.5, 4.5, null }, 15.0)]
    [InlineData("Avg", "Formula_Number", new object?[] { 10.0, 20.0, null }, 15.0)]
    [InlineData("Max", "Formula_Duration", new object?[] { 30.0, 90.0 }, 90.0)]
    [InlineData("Min", "Formula_Date", new object?[] { "2026-03-01", "2026-01-15" }, "2026-01-15")]
    [InlineData("Max", "Formula_DateTime", new object?[] { "2026-01-15T08:00:00", "2026-01-15T17:30:00" }, "2026-01-15T17:30:00")]
    [InlineData("DistinctCount", "Formula_Bool", new object?[] { true, false, true, null }, 2)]
    [InlineData("DistinctCount", "Formula_Text", new object?[] { "a", "A", "b", "", null }, 2)]
    [InlineData("CombinedText", "Formula_Email", new object?[] { "a@x.com", null, "b@x.com" }, "a@x.com, b@x.com")]
    // Combined Text over any calculated value, each shown in its display format (the app's defaults here).
    [InlineData("CombinedText", "Formula_Number", new object?[] { 1200.5, 30.0 }, "1,200.50, 30.00")]
    [InlineData("CombinedText", "Formula_Date", new object?[] { "2026-03-01", "2026-01-15" }, "03-01-2026, 01-15-2026")]
    [InlineData("CombinedText", "Formula_Bool", new object?[] { true, false }, "Yes, No")]
    [InlineData("CombinedText", "Formula_Time", new object?[] { "14:30", "09:05" }, "2:30 PM, 9:05 AM")]
    public async Task Summary_over_formula_field_is_computed_from_each_child_row(string function, string formulaType, object?[] childValues, object expected)
    {
        const int formulaFid = 32;
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        var childFields = new List<AppField> { Field(10, "Item", "Reference"), Field(formulaFid, "F", formulaType) };
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(childFields);

        // All children belong to parent 1; parent 2 has none.
        var childRows = Rows(childValues.Select((_, i) => new Dictionary<string, object?>
            { ["Id"] = (long)i + 1, [PhysicalNaming.ColumnName(10)] = 1L }).ToArray());
        _recordRepo.ListRowsByReferenceAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 10,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(),
                Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(childRows);
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(childValues
                .Select(v => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [formulaFid] = v is double d ? (decimal)d : v })
                .ToList());

        var result = await ProjectOneSummary($"{{\"childTableId\":77,\"referenceFid\":10,\"function\":\"{function}\",\"targetFid\":{formulaFid}}}");

        result[0][20].Should().Be(expected is double e ? (decimal)e : expected);
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Summary_over_a_lookup_of_a_summary_is_computed_from_each_child_row_as_its_result_type()
    {
        // Departments (5) › Employees (77): "Sum of Dept Min" sums each employee's "Dept - Min of Salary" lookup (fid 31),
        // which pulls the department's own "Min of Salary" summary down — no column, so worked out per child row.
        const int lookupFid = 31;
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _tableRepo.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 5 });
        var childFields = new List<AppField>
        {
            Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":5}"),
            Field(lookupFid, "Dept - Min of Salary", "Lookup",
                "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":5,\"sourceFid\":8,\"sourceTypeCode\":\"Summary\",\"resultTypeCode\":\"Currency\"}"),
        };
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(childFields);
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(8, "Min of Salary", "Summary", "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Min\",\"targetTypeCode\":\"Currency\"}") });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>());
        _recordRepo.ListRowsByReferenceAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 10,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(),
                Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(Rows(
                new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 1L },
                new Dictionary<string, object?> { ["Id"] = 2L, [PhysicalNaming.ColumnName(10)] = 1L }));
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(new List<IReadOnlyDictionary<long, object?>>
            {
                new Dictionary<long, object?> { [lookupFid] = 40000m },
                new Dictionary<long, object?> { [lookupFid] = 40000m },
            });

        var result = await ProjectOneSummary($"{{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Sum\",\"targetFid\":{lookupFid}}}");

        result[0][20].Should().Be(80000m);
        result[1][20].Should().BeNull();   // parent 2 has no employees
        await _recordRepo.DidNotReceive().AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Sum", Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<object>>(),
            Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Combined_text_of_a_lookup_of_a_summary_shows_each_value_in_the_type_it_produces()
    {
        // "Client - Min of Session Charges" pulls a Currency summary down: Combined Text shows "$40,000.00", not 40000.0000.
        const int lookupFid = 31;
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _tableRepo.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 5 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":5}"),
            Field(lookupFid, "Dept - Min of Salary", "Lookup",
                "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":5,\"sourceFid\":8,\"sourceTypeCode\":\"Summary\",\"resultTypeCode\":\"Currency\"}"),
        });
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(8, "Min of Salary", "Summary", "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Min\",\"targetTypeCode\":\"Currency\"}") });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>());
        _recordRepo.ListRowsByReferenceAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 10,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(),
                Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(Rows(
                new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 1L },
                new Dictionary<string, object?> { ["Id"] = 2L, [PhysicalNaming.ColumnName(10)] = 1L }));
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(new List<IReadOnlyDictionary<long, object?>>
            {
                new Dictionary<long, object?> { [lookupFid] = 40000.0000m },
                new Dictionary<long, object?> { [lookupFid] = 52000.5m },
            });

        var result = await ProjectOneSummary($"{{\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":{lookupFid}}}");

        result[0][20].Should().Be("$40,000.00, $52,000.50");
        await _recordRepo.DidNotReceiveWithAnyArgs().ListValuesByReferenceAsync(default!, default, default, default, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task Summary_over_a_child_summary_works_out_only_that_child_summary()
    {
        // Departments (5) › Projects (77) › Tasks (88): "Total Task" sums each project's own Count of tasks. The projects'
        // Combined Text of task names is another summary of theirs — not read for it.
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _tableRepo.GetByIdAsync(88, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 88 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Department", "Reference"),
            Field(16, "Task count", "Summary", "{\"childTableId\":88,\"referenceFid\":12,\"function\":\"Count\"}"),
            Field(17, "Task names", "Summary", "{\"childTableId\":88,\"referenceFid\":12,\"function\":\"CombinedText\",\"targetFid\":31}"),
        });
        _fieldRepo.ListByTableAsync(88, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(12, "Project", "Reference"), Field(31, "Name", "Text") });
        _recordRepo.ListRowsByReferenceAsync(Arg.Is<AppTable>(t => t.Id == 77), Arg.Any<IReadOnlyList<AppField>>(), 10,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(),
                Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(Rows(
                new Dictionary<string, object?> { ["Id"] = 100L, [PhysicalNaming.ColumnName(10)] = 1L },
                new Dictionary<string, object?> { ["Id"] = 101L, [PhysicalNaming.ColumnName(10)] = 1L }));
        _recordRepo.AggregateByReferenceAsync(Arg.Is<AppTable>(t => t.Id == 88), 12, "Count", Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [100L] = 3, [101L] = 2 });
        FormulaProjectorReturnsSeed();

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Sum\",\"targetFid\":16}");

        Convert.ToDecimal(result[0][20]).Should().Be(5m);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListValuesByReferenceAsync(default!, default, default, default, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task Lookup_of_a_parent_formula_works_out_only_what_the_formula_reads()
    {
        // "Department Code" is Name & "-" & Record ID#: the department's summaries play no part in it, so none is computed.
        var childFields = new List<AppField>
        {
            Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Department - Code", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":5,\"sourceTypeCode\":\"Formula\"}"),
        };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99 });
        _tableRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 7 });
        _fieldRepo.ListByTableAsync(7, Arg.Any<CancellationToken>()).Returns(childFields);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(5, "Code", "Formula"),
            Field(6, "Name", "Text"),
            Field(8, "# Employees", "Summary", "{\"childTableId\":7,\"referenceFid\":10,\"function\":\"Count\"}"),
        });
        _formulaProjector.GetFormulaDependencies(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IEnumerable<long>>()).Returns(new HashSet<long> { 5, 6, 3 });
        ParentRowsFor(99, RowsById((42L, new Dictionary<string, object?> { ["Id"] = 42L, [PhysicalNaming.ColumnName(6)] = "Sales" })));
        IReadOnlyList<AppField>? projectedForParent = null;
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Is<AppTable?>(t => t != null && t.Id == 99))
            .Returns(ci =>
            {
                projectedForParent = ci.ArgAt<IReadOnlyList<AppField>>(0);
                return new List<IReadOnlyDictionary<long, object?>> { new Dictionary<long, object?> { [5] = "Sales-42" } };
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, ChildRowLinkedTo42);

        result[0][11].Should().Be("Sales-42");
        projectedForParent!.Select(f => f.Fid).Should().BeEquivalentTo(new int?[] { 5, 6 });
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Combined_text_of_a_user_field_joins_the_users_names()
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(33, "Owner", "User") });
        _recordRepo.ListValuesByReferenceAsync(Arg.Any<AppTable>(), 10, 33, Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<object>>(),
                Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(),
                Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object, object)> { (1L, 7L), (1L, 9L), (2L, 7L) });
        _userRepo.GetNamesByIdsAsync(Arg.Is<IEnumerable<long>>(ids => ids.OrderBy(i => i).SequenceEqual(new long[] { 7, 9 })), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [7] = "Asha Patel", [9] = "Ravi Shah" });

        var result = await ProjectOneSummary("""{"childTableId":77,"referenceFid":10,"function":"CombinedText","targetFid":33}""");

        result[0][20].Should().Be("Asha Patel, Ravi Shah");
        result[1][20].Should().Be("Asha Patel");
    }

    [Fact]
    public async Task Combined_text_of_a_date_range_in_memory_shows_both_ends()
    {
        // In an encrypted app a Text criterion (Note) is judged over decrypted rows: there the range's end is its second column.
        _appRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(new App { Id = 42, IsEncrypted = true });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, AppId = 42 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(34, "Term", "DateRange"), Field(35, "Note", "Text") });
        ChildRowsByReference(77, Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_10"] = "1", ["f_34"] = new DateTime(2026, 1, 5), ["f_34_e"] = new DateTime(2026, 3, 31), ["f_35"] = "a" },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_10"] = "1", ["f_34"] = new DateTime(2026, 4, 1), ["f_34_e"] = null, ["f_35"] = "b" }));

        var result = await ProjectOneSummary("""{"childTableId":77,"referenceFid":10,"function":"CombinedText","targetFid":34,"filterTree":"{\"logic\":\"and\",\"nodes\":[{\"condition\":{\"fieldId\":35,\"operator\":\"isNotEmpty\"}}]}"}""", appId: 42);

        result[0][20].Should().Be("01-05-2026 – 03-31-2026, 04-01-2026 – …");
        await _recordRepo.DidNotReceive().AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Sum", Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<object>>(),
            Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Summary_count_rolls_up_child_records_onto_parent_rows()
    {
        // Parent table has a Count summary (fid 20) over child table 77 via reference fid 10.
        var parentFields = new List<AppField>
        {
            Field(20, "# of Items", "Summary", "{\"relationshipId\":1,\"childTableId\":77,\"referenceFid\":10,\"function\":\"Count\"}"),
        };
        var parentRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 2L });

        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Invoice Item" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 3 });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5 }, parentFields, parentRows);

        result[0][20].Should().Be(3);   // parent 1 has 3 children
        result[1][20].Should().Be(0);   // parent 2 has none → Count defaults to 0
    }

    [Fact]
    public async Task Summary_distinct_count_rolls_up_and_defaults_to_zero()
    {
        var parentFields = new List<AppField>
        {
            Field(22, "Distinct Status", "Summary", "{\"relationshipId\":1,\"childTableId\":77,\"referenceFid\":10,\"function\":\"DistinctCount\",\"targetFid\":30}"),
        };
        var parentRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 2L });

        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Task" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "DistinctCount", 30,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 2 });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5 }, parentFields, parentRows);

        result[0][22].Should().Be(2);   // e.g. statuses Open, Open, Done → 2 unique
        result[1][22].Should().Be(0);   // no children → 0, not blank
    }

    /// <summary>Arranges a Combined Text summary (fid 23) over child field fid 31 of the given
    /// type/settings, whose repo rows are <paramref name="values"/> (parentKey, raw value) in order.</summary>
    private async Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>> RunCombinedText(
        string targetType, string? targetSettings, string extraSummarySettings, string? appFormatting,
        params (long Parent, object Value)[] values)
    {
        var parentFields = new List<AppField>
        {
            Field(23, "Combined", "Summary",
                "{\"relationshipId\":1,\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":31" + extraSummarySettings + "}"),
        };
        var parentRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 2L });

        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Task" });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(31, "Target", targetType, targetSettings) });
        _appRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new App { Id = 9, Formatting = appFormatting });
        _recordRepo.ListValuesByReferenceAsync(Arg.Any<AppTable>(), 10, 31, Arg.Any<string?>(),
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(values.Select(v => ((object)v.Parent, v.Value)).ToList());

        return await NewProjector().ProjectAsync(new AppTable { Id = 5, AppId = 9 }, parentFields, parentRows);
    }

    [Fact]
    public async Task Summary_combined_text_joins_in_order_and_is_blank_without_children()
    {
        var result = await RunCombinedText("Text", null, "", null, (1L, "Call client"), (1L, ""), (1L, "Send invoice"));

        result[0][23].Should().Be("Call client, Send invoice");   // default ", ", blank skipped
        result[1][23].Should().BeNull();                           // no children → blank, not "0"
    }

    [Fact]
    public async Task Summary_combined_text_formats_currency_with_field_and_app_settings()
    {
        var result = await RunCombinedText("Currency", "{\"symbol\":\"₹\",\"decimals\":2}", ",\"delimiter\":\" | \"", null,
            (1L, 1200m), (1L, 99.5m));

        result[0][23].Should().Be("₹1,200.00 | ₹99.50");
    }

    [Fact]
    public async Task Summary_combined_text_formats_dates_with_app_date_format()
    {
        var result = await RunCombinedText("Date", null, "", "{\"date\":{\"formatString\":\"DD-MM-YYYY\",\"separator\":\"/\"}}",
            (1L, new DateTime(2026, 9, 23)), (1L, new DateTime(2026, 1, 5)));

        result[0][23].Should().Be("23/09/2026, 05/01/2026");
    }

    [Fact]
    public async Task Summary_combined_text_distinct_compares_displayed_text()
    {
        // 100 and 100.0000 are the same number — shown once. Distinct keeps first occurrence order.
        var result = await RunCombinedText("Number", "{\"decimals\":0}", ",\"distinctValues\":true", null,
            (1L, 250m), (1L, 100m), (1L, 100.0000m), (1L, 250m));

        result[0][23].Should().Be("250, 100");
    }

    [Fact]
    public async Task Summary_combined_text_joins_with_new_lines_keeping_repository_order()
    {
        var result = await RunCombinedText("Text", null, ",\"delimiter\":\"\\n\"", null,
            (1L, "Review contract"), (1L, "Call client"), (2L, "Only task"));

        result[0][23].Should().Be("Review contract\nCall client");   // order is the repository's (sorted) order
        result[1][23].Should().Be("Only task");
    }

    [Fact]
    public async Task Summary_combined_text_passes_its_sort_settings_to_the_repository()
    {
        await RunCombinedText("Text", null, ",\"sortFid\":7,\"sortDescending\":true", null, (1L, "x"));

        await _recordRepo.Received(1).ListValuesByReferenceAsync(Arg.Any<AppTable>(), 10, 31, Arg.Any<string?>(),
            Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), 7, true, Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Summary_combined_text_without_sort_uses_record_order_ascending()
    {
        await RunCombinedText("Text", null, "", null, (1L, "x"));

        await _recordRepo.Received(1).ListValuesByReferenceAsync(Arg.Any<AppTable>(), 10, 31, Arg.Any<string?>(),
            Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), null, false, Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Summary_exists_rolls_up_true_false_onto_parent_rows()
    {
        // Parent table has an Exists (True/False) summary (fid 21) over child table 77 via reference fid 10.
        var parentFields = new List<AppField>
        {
            Field(21, "Has Items", "Summary", "{\"relationshipId\":1,\"childTableId\":77,\"referenceFid\":10,\"function\":\"Exists\"}"),
        };
        var parentRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 2L });

        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Invoice Item" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Exists", null,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = true });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5 }, parentFields, parentRows);

        result[0][21].Should().Be(true);    // parent 1 has children
        result[1][21].Should().Be(false);   // parent 2 has none → Exists defaults to false
    }

    [Fact]
    public async Task Reference_projects_relationship_display_key_value_over_table_key()
    {
        // Child fid 10 → parent 99 via relationship 1. Relationship overrides the display key to
        // parent fid 6 ("Code"); the parent table's own KeyFieldId points at fid 5 ("Name").
        var childFields = new List<AppField> { Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}") };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L },
            new Dictionary<string, object?> { ["Id"] = 2L, [PhysicalNaming.ColumnName(10)] = null });

        var parentTable = new AppTable { Id = 99, Name = "Department", KeyFieldId = 5 };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(5, "Name", "Text"), Field(6, "Code", "Text") });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 1, ReferenceFid = 10, ParentTableId = 99, ChildTableId = 7, DisplayKeyFieldId = 6 } });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Engineering", [PhysicalNaming.ColumnName(6)] = "ENG" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][10].Should().Be("ENG");     // linked → relationship display key ("Code"), not "Name"
        result[1].Should().NotContainKey(10); // unlinked → nothing projected (stored NULL shows as null)
    }

    [Fact]
    public async Task Reference_falls_back_to_table_key_then_record_id()
    {
        // No per-relationship display key. Parent table key = fid 5 → projects "Name".
        var childFields = new List<AppField> { Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}") };
        var childRows = Rows(new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L });

        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99, Name = "Department", KeyFieldId = 5 });
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(5, "Name", "Text") });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 1, ReferenceFid = 10, ParentTableId = 99, ChildTableId = 7, DisplayKeyFieldId = null } });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Engineering" } });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][10].Should().Be("Engineering");

        // And with no table key either: Record ID# ⇒ nothing projected, the stored row Id shows as-is.
        _tableRepo.GetByIdAsync(98, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 98, Name = "Team" });
        _fieldRepo.ListByTableAsync(98, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        var refToPlainParent = new List<AppField> { Field(12, "Team", "Reference", "{\"relationshipId\":2,\"parentTableId\":98}") };
        var rows2 = Rows(new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(12)] = 7L });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 2, ReferenceFid = 12, ParentTableId = 98, ChildTableId = 7 } });

        var result2 = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, refToPlainParent, rows2);
        result2[0].Should().NotContainKey(12);
    }

    [Fact]
    public async Task No_relationship_fields_yields_empty_maps()
    {
        var fields = new List<AppField> { Field(1, "Qty", "Number") };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 5m });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 1 }, fields, rows);

        result[0].Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_with_encrypted_parent_field_maps_decrypted_value_by_fid_or_physical_name()
    {
        // Child table: lookup (fid 11) points to parent field (fid 5, IsEncrypted = true)
        var childFields = new List<AppField>
        {
            Field(10, "Employee", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Employee - SSN", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":5,\"sourceTypeCode\":\"Text\"}"),
        };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L });

        var parentTable = new AppTable { Id = 99, AppId = 1, Name = "Employee" };
        var ssnField = Field(5, "SSN", "Text");
        ssnField.IsEncrypted = true;
        ssnField.PhysicalColumnName = "SSN";

        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { ssnField });

        // RecordRepository decrypts and returns the row with f_5
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "123-45-6789" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][11].Should().Be("123-45-6789");
    }

    [Fact]
    public async Task Reference_display_key_with_encrypted_parent_field_projects_plaintext()
    {
        var childFields = new List<AppField>
        {
            Field(10, "Customer", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
        };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L });

        var parentTable = new AppTable { Id = 99, AppId = 1, Name = "Customer" };
        var nameField = Field(5, "Name", "Text");
        nameField.IsEncrypted = true;
        nameField.PhysicalColumnName = "CustName";

        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { nameField });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship>
            {
                new() { Id = 1, ReferenceFid = 10, ParentTableId = 99, ChildTableId = 7, DisplayKeyFieldId = 5 }
            });

        // RecordRepository returns decrypted row
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Acme Corp" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][10].Should().Be("Acme Corp");
    }
}
