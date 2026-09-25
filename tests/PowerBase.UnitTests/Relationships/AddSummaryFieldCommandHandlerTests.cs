using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.AddSummaryField;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>What AddSummaryField validates and — for valid requests — exactly what it saves
/// into the new Summary field's settings.</summary>
public class AddSummaryFieldCommandHandlerTests
{
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();

    private readonly AddSummaryFieldCommandHandler _handler;
    private AppField? _created;

    private const long ClientTableId = 5;
    private const long TaskTableId = 77;
    private static readonly Guid RelPublicId = Guid.NewGuid();

    // Task fields: Title (Text, fid 5), Amount (Currency, fid 6), Due Date (Date, fid 7), Days Left (formula, fid 8).
    private readonly List<AppField> TaskFields =
    [
        new() { Id = 50, Fid = 5, AppTableId = TaskTableId, Name = "Title", TypeCode = "Text" },
        new() { Id = 60, Fid = 6, AppTableId = TaskTableId, Name = "Amount", TypeCode = "Currency" },
        new() { Id = 70, Fid = 7, AppTableId = TaskTableId, Name = "Due Date", TypeCode = "Date" },
        new() { Id = 80, Fid = 8, AppTableId = TaskTableId, Name = "Days Left", TypeCode = "Formula_Number" },
        new() { Id = 100, Fid = 10, AppTableId = TaskTableId, Name = "Client", TypeCode = "Reference" },
    ];

    public AddSummaryFieldCommandHandlerTests()
    {
        var fieldFactory = new RelationshipFieldFactory(_fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _nameResolver);
        var queries = new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo);
        _handler = new AddSummaryFieldCommandHandler(_relRepo, _tableRepo, _fieldRepo, fieldFactory, queries, _auditRepo, _appRepo);
        _appRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(ci => new App { Id = ci.Arg<long>() });

        var rel = new Relationship { Id = 1, PublicId = RelPublicId, ParentTableId = ClientTableId, ChildTableId = TaskTableId, ReferenceFid = 10 };
        _relRepo.GetByPublicIdAsync(RelPublicId, Arg.Any<CancellationToken>()).Returns(rel);
        _tableRepo.GetByIdAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ClientTableId, PublicId = Guid.NewGuid(), Name = "Client" });
        _tableRepo.GetByIdAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = TaskTableId, PublicId = Guid.NewGuid(), Name = "Task" });
        _fieldRepo.ListByTableAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(TaskFields);
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new FieldType { Id = 1, Code = ci.Arg<string>() });
        _nameResolver.GenerateUniqueNameAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<string>(1));
        _fieldRepo.GetNextFidAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(30);
        _fieldRepo.LabelExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.CreateAsync(Arg.Do<AppField>(f => _created = f), Arg.Any<CancellationToken>()).Returns((300L, Guid.NewGuid()));
        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Form>());
    }

    private static AddSummaryFieldCommand Command(string function, int? targetFid, FilterGroup? criteria = null,
        CombinedTextOptions? combinedText = null, string label = "My summary") =>
        new(RelPublicId, label, function, targetFid, criteria, combinedText);

    /// <summary>The settings JSON exactly as the handler would store it, read back independently.</summary>
    private SummarySettings SavedSettings() =>
        JsonSerializer.Deserialize<SummarySettings>(_created!.Settings!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    // ── Validation ──

    [Fact]
    public async Task Handle_EmptyLabel_ThrowsValidation_AndCreatesNothing()
    {
        await FluentActions.Invoking(() => _handler.HandleAsync(Command("Count", null, label: "  ")))
            .Should().ThrowAsync<ValidationException>();
        _created.Should().BeNull();
    }

    [Fact]
    public async Task Handle_UnknownFunction_ThrowsValidation() =>
        await FluentActions.Invoking(() => _handler.HandleAsync(Command("Median", 6)))
            .Should().ThrowAsync<ValidationException>();

    [Theory]
    [InlineData("Sum", 5)]     // Sum on Text
    [InlineData("Max", 5)]     // Max on Text
    [InlineData("Avg", 7)]     // Avg on Date
    [InlineData("Sum", 8)]     // any function on a formula field
    public async Task Handle_FunctionNotValidForField_ThrowsValidation_AndCreatesNothing(string function, int targetFid)
    {
        await FluentActions.Invoking(() => _handler.HandleAsync(Command(function, targetFid)))
            .Should().ThrowAsync<ValidationException>();
        _created.Should().BeNull();
    }

    [Fact]
    public async Task Handle_CombinedTextWithEmptyDelimiter_ThrowsValidation() =>
        await FluentActions.Invoking(() => _handler.HandleAsync(Command("CombinedText", 5, combinedText: new CombinedTextOptions("", null, false, false))))
            .Should().ThrowAsync<ValidationException>();

    [Fact]
    public async Task Handle_EncryptedApp_ThrowsValidation_AndCreatesNothing()
    {
        _appRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 9, IsEncrypted = true });

        (await FluentActions.Invoking(() => _handler.HandleAsync(Command("Count", null)))
            .Should().ThrowAsync<ValidationException>()).Which.Errors["targetFid"].Single().Should().Contain("encrypted app");
        _created.Should().BeNull();
    }

    [Fact]
    public async Task Handle_EncryptedTargetField_ThrowsValidation_AndCreatesNothing()
    {
        TaskFields.Single(f => f.Fid == 6).IsEncrypted = true;   // this test instance's own list

        (await FluentActions.Invoking(() => _handler.HandleAsync(Command("Sum", 6)))
            .Should().ThrowAsync<ValidationException>()).Which.Errors["targetFid"].Single().Should().Contain("'Amount'");
        _created.Should().BeNull();
    }

    // ── What gets saved ──

    [Theory]
    [InlineData("sum", "Sum")]
    [InlineData("DISTINCTCOUNT", "DistinctCount")]
    [InlineData("count", "Count")]
    public async Task Handle_FunctionNameInAnyCasing_IsSavedCanonical(string sent, string saved)
    {
        await _handler.HandleAsync(Command(sent, 6));

        SavedSettings().Function.Should().Be(saved);
    }

    [Fact]
    public async Task Handle_Count_SavesNoTargetEvenIfOneWasSent()
    {
        await _handler.HandleAsync(Command("Count", 6));

        var s = SavedSettings();
        s.Function.Should().Be("Count");
        s.TargetFid.Should().BeNull();
        s.ChildTableId.Should().Be(TaskTableId);
        s.ReferenceFid.Should().Be(10);
        _created!.TypeCode.Should().Be("Summary");
    }

    [Fact]
    public async Task Handle_MaxOfDate_SavesTargetAndItsType()
    {
        await _handler.HandleAsync(Command("Max", 7));

        var s = SavedSettings();
        s.TargetFid.Should().Be(7);
        s.TargetTypeCode.Should().Be("Date");   // drives date rendering of the result
    }

    [Theory]
    [InlineData("DistinctCount", 5)]
    [InlineData("DistinctCount", 7)]
    [InlineData("CombinedText", 6)]
    public async Task Handle_AnyTypeFunctions_AcceptTextDateAndNumberFields(string function, int targetFid)
    {
        await _handler.HandleAsync(Command(function, targetFid));

        SavedSettings().Function.Should().Be(function);
    }

    [Fact]
    public async Task Handle_CombinedText_SavesDelimiterSortAndDistinct()
    {
        await _handler.HandleAsync(Command("CombinedText", 5, combinedText: new CombinedTextOptions("\n", 7, true, true)));

        var s = SavedSettings();
        s.Delimiter.Should().Be("\n");
        s.SortFid.Should().Be(7);
        s.SortDescending.Should().BeTrue();
        s.DistinctValues.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_OtherFunction_DoesNotSaveCombinedTextOptions()
    {
        await _handler.HandleAsync(Command("Sum", 6, combinedText: new CombinedTextOptions(" | ", 7, true, true)));

        var s = SavedSettings();
        s.Delimiter.Should().BeNull();
        s.SortFid.Should().BeNull();
        s.SortDescending.Should().BeFalse();
        s.DistinctValues.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MatchingCriteria_SavedAsFilterTree_AndOmittedWhenEmpty()
    {
        var criteria = new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 6, Operator = "gt", Value = "1000" } }] };

        await _handler.HandleAsync(Command("Count", null, criteria));
        SavedSettings().FilterTree.Should().Contain("\"fieldId\":6").And.Contain("\"operator\":\"gt\"").And.Contain("\"value\":\"1000\"");

        _created = null;
        await _handler.HandleAsync(Command("Count", null, new FilterGroup { Logic = "and", Nodes = [] }));
        SavedSettings().FilterTree.Should().BeNull();
    }
}
