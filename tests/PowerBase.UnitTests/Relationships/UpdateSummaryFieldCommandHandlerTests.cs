using System.Data;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Queries.GetFieldUsage;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.UpdateSummaryField;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Relationships;

/// <summary>What UpdateSummaryField lets through (criteria, label, same-type calculation changes),
/// what it refuses (bad criteria, another relationship's field, a result-type change something
/// depends on) and exactly what it saves.</summary>
public class UpdateSummaryFieldCommandHandlerTests
{
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IFieldVersionRepository _versionRepo = Substitute.For<IFieldVersionRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private readonly UpdateSummaryFieldCommandHandler _handler;
    private string? _savedLabel;
    private string? _savedSettings;

    private const long ClientTableId = 5;
    private const long TaskTableId = 77;
    private const long NoteTableId = 88;
    private const long RelId = 1;
    private static readonly Guid RelPublicId = Guid.NewGuid();

    // Task fields: Title (Text, fid 5), Amount (Currency, fid 6), Due Date (Date, fid 7), Client (Reference, fid 10).
    private readonly List<AppField> _taskFields =
    [
        new() { Id = 50, Fid = 5, AppTableId = TaskTableId, Name = "Title", TypeCode = "Text" },
        new() { Id = 60, Fid = 6, AppTableId = TaskTableId, Name = "Amount", TypeCode = "Currency" },
        new() { Id = 70, Fid = 7, AppTableId = TaskTableId, Name = "Due Date", TypeCode = "Date" },
        new() { Id = 100, Fid = 10, AppTableId = TaskTableId, Name = "Client", TypeCode = "Reference" },
    ];

    // Client › "Open Tasks": a Count summary over Task, filtered to Title = "Open".
    private readonly AppField _summary = new()
    {
        Id = 300, Fid = 30, PublicId = Guid.NewGuid(), AppTableId = ClientTableId,
        Name = "Open_Tasks", Label = "Open Tasks", TypeCode = "Summary", IsSearchable = true, IsReportable = true,
        Settings = RelationshipFieldFactory.Serialize(new SummarySettings
        {
            RelationshipId = RelId, ChildTableId = TaskTableId, ReferenceFid = 10, Function = "Count",
            FilterTree = "{\"logic\":\"and\",\"nodes\":[{\"condition\":{\"fieldId\":5,\"operator\":\"eq\",\"value\":\"Open\"}}]}",
        }),
    };

    private readonly List<AppField> _clientFields;
    private readonly List<AppField> _noteFields = [];
    private FieldUsageDto _usage = new();

    public UpdateSummaryFieldCommandHandlerTests()
    {
        _clientFields = [_summary];
        var queries = new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo);
        _handler = new UpdateSummaryFieldCommandHandler(_relRepo, _tableRepo, _fieldRepo, _appRepo, new FormulaEngine(),
            new FieldVersionService(_versionRepo, _queryContext), _uow, queries, _auditRepo);

        _uow.Transaction.Returns((IDbTransaction?)null);
        _appRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(ci => new App { Id = ci.Arg<long>() });

        var rel = new Relationship { Id = RelId, PublicId = RelPublicId, ParentTableId = ClientTableId, ChildTableId = TaskTableId, ReferenceFid = 10 };
        _relRepo.GetByPublicIdAsync(RelPublicId, Arg.Any<CancellationToken>()).Returns(rel);
        // Client is also the parent of Note (relationship 2), so lookups there can pull the summary down.
        _relRepo.ListByParentTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            rel,
            new() { Id = 2, PublicId = Guid.NewGuid(), ParentTableId = ClientTableId, ChildTableId = NoteTableId, ReferenceFid = 3 },
        });

        _tableRepo.GetByIdAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ClientTableId, AppId = 9, PublicId = Guid.NewGuid(), Name = "Client" });
        _tableRepo.GetByIdAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = TaskTableId, AppId = 9, PublicId = Guid.NewGuid(), Name = "Task" });
        _tableRepo.GetByIdAsync(NoteTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = NoteTableId, AppId = 9, PublicId = Guid.NewGuid(), Name = "Note" });
        _fieldRepo.ListByTableAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(_taskFields);
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(_clientFields);
        _fieldRepo.ListByTableAsync(NoteTableId, Arg.Any<CancellationToken>()).Returns(_noteFields);
        _fieldRepo.GetByPublicIdAsync(_summary.PublicId, Arg.Any<CancellationToken>()).Returns(_summary);
        _fieldRepo.LabelExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.GetFieldUsageAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => _usage);
        _fieldRepo.UpdateAsync(_summary.PublicId, ClientTableId,
                Arg.Do<string?>(l => _savedLabel = l), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Do<string?>(s => _savedSettings = s),
                Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(1);
    }

    private UpdateSummaryFieldCommand Command(string function, int? targetFid, FilterGroup? criteria = null,
        CombinedTextOptions? combinedText = null, string label = "Open Tasks", Guid? fieldId = null) =>
        new(RelPublicId, fieldId ?? _summary.PublicId, label, function, targetFid, criteria, combinedText);

    private static FilterGroup Where(int fieldId, string op, string value) =>
        new() { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = op, Value = value } }] };

    private SummarySettings SavedSettings() =>
        JsonSerializer.Deserialize<SummarySettings>(_savedSettings!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private async Task ShouldNotSave(Func<Task> act, Type exceptionType)
    {
        (await FluentActions.Invoking(act).Should().ThrowAsync<Exception>()).Which.Should().BeOfType(exceptionType);
        _savedSettings.Should().BeNull();
    }

    // ── Matching criteria and label ──

    [Fact]
    public async Task Handle_NewCriteria_SavesThem_AndKeepsRelationshipWiring()
    {
        await _handler.HandleAsync(Command("Count", null, Where(6, "gt", "1000")));

        var s = SavedSettings();
        s.FilterTree.Should().Contain("\"fieldId\":6").And.Contain("\"operator\":\"gt\"").And.Contain("\"value\":\"1000\"");
        s.Function.Should().Be("Count");
        s.RelationshipId.Should().Be(RelId);
        s.ChildTableId.Should().Be(TaskTableId);
        s.ReferenceFid.Should().Be(10);
    }

    [Fact]
    public async Task Handle_EmptyOrNullCriteria_ClearsTheFilter()
    {
        await _handler.HandleAsync(Command("Count", null, new FilterGroup { Logic = "and", Nodes = [] }));
        SavedSettings().FilterTree.Should().BeNull();

        _savedSettings = null;
        await _handler.HandleAsync(Command("Count", null, criteria: null));
        SavedSettings().FilterTree.Should().BeNull();
    }

    [Fact]
    public async Task Handle_NewLabel_IsTrimmedAndSaved_AndAVersionIsRecorded()
    {
        await _handler.HandleAsync(Command("Count", null, label: "  Big Tasks  "));

        _savedLabel.Should().Be("Big Tasks");
        await _versionRepo.Received(1).InsertVersionAsync(
            Arg.Is<AppFieldVersion>(v => v.AppFieldId == _summary.Id && v.CommitMessage == "Summary field settings updated"),
            Arg.Any<IReadOnlyList<FieldChangeEntry>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyLabel_ThrowsValidation() =>
        await ShouldNotSave(() => _handler.HandleAsync(Command("Count", null, label: " ")), typeof(ValidationException));

    [Fact]
    public async Task Handle_LabelTakenByAnotherField_ThrowsDuplicate()
    {
        _fieldRepo.LabelExistsInTableAsync(ClientTableId, "Status", _summary.Id, Arg.Any<CancellationToken>()).Returns(true);

        await ShouldNotSave(() => _handler.HandleAsync(Command("Count", null, label: "Status")), typeof(DuplicateException));
    }

    [Fact]
    public async Task Handle_CriteriaOnUnknownField_ThrowsValidation() =>
        await ShouldNotSave(() => _handler.HandleAsync(Command("Count", null, Where(999, "eq", "x"))), typeof(ValidationException));

    // ── Which field can be edited ──

    [Fact]
    public async Task Handle_UnknownField_ThrowsNotFound() =>
        await ShouldNotSave(() => _handler.HandleAsync(Command("Count", null, fieldId: Guid.NewGuid())), typeof(NotFoundException));

    [Fact]
    public async Task Handle_SummaryOfAnotherRelationship_ThrowsNotFound()
    {
        var other = new AppField
        {
            Id = 400, Fid = 40, PublicId = Guid.NewGuid(), AppTableId = ClientTableId, Name = "Notes", TypeCode = "Summary",
            Settings = RelationshipFieldFactory.Serialize(new SummarySettings { RelationshipId = 2, Function = "Count" }),
        };
        _fieldRepo.GetByPublicIdAsync(other.PublicId, Arg.Any<CancellationToken>()).Returns(other);

        await ShouldNotSave(() => _handler.HandleAsync(Command("Count", null, fieldId: other.PublicId)), typeof(NotFoundException));
    }

    [Fact]
    public async Task Handle_NonSummaryField_ThrowsNotFound()
    {
        var text = new AppField { Id = 500, Fid = 50, PublicId = Guid.NewGuid(), AppTableId = ClientTableId, Name = "Name", TypeCode = "Text" };
        _fieldRepo.GetByPublicIdAsync(text.PublicId, Arg.Any<CancellationToken>()).Returns(text);

        await ShouldNotSave(() => _handler.HandleAsync(Command("Count", null, fieldId: text.PublicId)), typeof(NotFoundException));
    }

    // ── Calculation changes ──

    [Fact]
    public async Task Handle_InvalidFunctionForTarget_ThrowsValidation() =>
        await ShouldNotSave(() => _handler.HandleAsync(Command("Sum", 5)), typeof(ValidationException));   // Sum of Text

    [Fact]
    public async Task Handle_ChangeToCombinedText_SavesTargetAndOptions()
    {
        await _handler.HandleAsync(Command("CombinedText", 5, combinedText: new CombinedTextOptions(" | ", 7, true, true)));

        var s = SavedSettings();
        s.Function.Should().Be("CombinedText");
        s.TargetFid.Should().Be(5);
        s.TargetTypeCode.Should().Be("Text");
        s.Delimiter.Should().Be(" | ");
        s.SortFid.Should().Be(7);
    }

    [Fact]
    public async Task Handle_ResultTypeChange_WithFormulaReadingIt_ThrowsConflict()
    {
        _clientFields.Add(new AppField { Id = 310, Fid = 31, AppTableId = ClientTableId, Name = "Double", Label = "Double",
            TypeCode = "Formula_Number", Settings = "{\"expression\":\"[Open Tasks] * 2\"}" });

        (await FluentActions.Invoking(() => _handler.HandleAsync(Command("CombinedText", 5)))
            .Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("formula 'Double'");
        _savedSettings.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ResultTypeChange_WithLookupPullingItDown_ThrowsConflict()
    {
        _noteFields.Add(new AppField { Id = 800, Fid = 8, AppTableId = NoteTableId, Name = "Client Open Tasks", TypeCode = "Lookup",
            Settings = RelationshipFieldFactory.Serialize(new LookupSettings { RelationshipId = 2, SourceFid = 30, SourceTypeCode = "Summary" }) });

        (await FluentActions.Invoking(() => _handler.HandleAsync(Command("Exists", null)))
            .Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("lookup 'Client Open Tasks'");
    }

    [Fact]
    public async Task Handle_ResultTypeChange_WithReportFilteringOnIt_ThrowsConflict()
    {
        _usage = new FieldUsageDto { Reports = [new FieldUsageReportItem(Guid.NewGuid(), "Busy clients", ["column", "filter"])] };

        (await FluentActions.Invoking(() => _handler.HandleAsync(Command("Exists", null)))
            .Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("report filter 'Busy clients'");
    }

    [Fact]
    public async Task Handle_ResultTypeChange_WhenOnlyShownAsReportColumn_IsAllowed()
    {
        _usage = new FieldUsageDto { Reports = [new FieldUsageReportItem(Guid.NewGuid(), "All clients", ["column", "sort"])] };

        await _handler.HandleAsync(Command("Exists", null));

        SavedSettings().Function.Should().Be("Exists");
    }

    [Fact]
    public async Task Handle_SameResultType_WithDependents_IsAllowed()
    {
        // Count → Sum of Amount: both Number, so the formula keeps working.
        _clientFields.Add(new AppField { Id = 310, Fid = 31, AppTableId = ClientTableId, Name = "Double", Label = "Double",
            TypeCode = "Formula_Number", Settings = "{\"expression\":\"[Open Tasks] * 2\"}" });
        _usage = new FieldUsageDto { Reports = [new FieldUsageReportItem(Guid.NewGuid(), "Busy clients", ["filter"])] };

        await _handler.HandleAsync(Command("Sum", 6, Where(5, "eq", "Done")));

        var s = SavedSettings();
        s.Function.Should().Be("Sum");
        s.TargetFid.Should().Be(6);
    }
}
