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

    private RelationalProjector NewProjector() => new(_tableRepo, _fieldRepo, _recordRepo, _relRepo, _appRepo, _userRepo, _formulaProjector);

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

    // ── Lookups of computed fields (Formula / Lookup) ─────────────────────────────────────────

    private static IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> RowsById(params (long Id, Dictionary<string, object?> Row)[] rows) =>
        rows.ToDictionary(r => r.Id, r => (IReadOnlyDictionary<string, object?>)r.Row);

    private void ParentRowsFor(long tableId, IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> rows) =>
        _recordRepo.GetRowsByIdsAsync(Arg.Is<AppTable>(t => t.Id == tableId), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
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
    public async Task Summary_criteria_on_an_encrypted_parent_field_is_shown_blank()
    {
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(6, "Start Date", "Date"), new() { Id = 7, Fid = 7, Name = "End Date", TypeCode = "Date", IsEncrypted = true } });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(CriteriaChildFields);

        var result = await ProjectOneSummary(System.Text.Json.JsonSerializer.Serialize(
            new { childTableId = 77, referenceFid = 10, function = "Count", filterTree = BetweenParentDates }));

        result[0][20].Should().BeNull();
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
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

    [Fact]
    public async Task Summary_in_encrypted_app_is_blank_and_never_computed()
    {
        _appRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(new App { Id = 3, IsEncrypted = true });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(PlainChildFields);

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Count\"}", appId: 3);

        result[0][20].Should().BeNull();
        result[1][20].Should().BeNull();
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task Combined_text_over_encrypted_field_is_blank_and_never_read()
    {
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Field(10, "Item", "Reference"),
            new() { Id = 31, Fid = 31, Name = "Secret", TypeCode = "Text", IsEncrypted = true },
        });

        var result = await ProjectOneSummary("{\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":31}");

        result[0][20].Should().BeNull();
        await _recordRepo.DidNotReceiveWithAnyArgs().ListValuesByReferenceAsync(default!, default, default, default, default!, default, default, default, default, default);
    }

    [Theory]
    [InlineData("Sum", "Formula_Time")]        // a formula result that can't be summarized
    [InlineData("CombinedText", "Formula_Number")]   // a function the formula's result type doesn't support
    [InlineData("Sum", "Summary")]             // no column, and not a formula
    [InlineData("CombinedText", "User")]       // saved before today: would show raw user ids
    public async Task Summary_saved_under_older_rules_is_blank_and_never_computed(string function, string targetType)
    {
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77 });
        _fieldRepo.ListByTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(10, "Item", "Reference"), Field(32, "T", targetType) });

        var result = await ProjectOneSummary($"{{\"childTableId\":77,\"referenceFid\":10,\"function\":\"{function}\",\"targetFid\":32}}");

        result[0][20].Should().BeNull();
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateByReferenceAsync(default!, default, default!, default, default!, default, default, default, default);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListValuesByReferenceAsync(default!, default, default, default, default!, default, default, default, default, default);
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
}
