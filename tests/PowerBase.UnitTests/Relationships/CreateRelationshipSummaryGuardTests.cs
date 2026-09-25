using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// Reusing an existing Number field as a new relationship's reference turns it into a Reference
/// field. CreateRelationship must refuse that when a Summary already aggregates the field in a way
/// a Reference can't support — and refuse it before anything is written.
/// </summary>
public class CreateRelationshipSummaryGuardTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();

    private readonly CreateRelationshipCommandHandler _handler;

    private const long AppId = 10;
    private const long ClientTableId = 5;    // existing parent of Task (has the summaries)
    private const long ProjectTableId = 6;   // new parent being linked
    private const long TaskTableId = 77;     // child
    private static readonly Guid AppPublicId = Guid.NewGuid();
    private static readonly Guid ProjectPublicId = Guid.NewGuid();
    private static readonly Guid TaskPublicId = Guid.NewGuid();

    // Task.Amount (fid 12) — the Number field the new relationship wants to reuse as its reference.
    private const int AmountFid = 12;

    public CreateRelationshipSummaryGuardTests()
    {
        var fieldFactory = new RelationshipFieldFactory(_fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _nameResolver);
        _handler = new CreateRelationshipCommandHandler(_tableRepo, _fieldRepo, _fieldTypeRepo, _relRepo, fieldFactory, _auditRepo, _appRepo);

        var project = new AppTable { Id = ProjectTableId, PublicId = ProjectPublicId, AppId = AppId, Name = "Project" };
        var task = new AppTable { Id = TaskTableId, PublicId = TaskPublicId, AppId = AppId, Name = "Task" };
        _tableRepo.GetByPublicIdAsync(ProjectPublicId, Arg.Any<CancellationToken>()).Returns(project);
        _tableRepo.GetByPublicIdAsync(TaskPublicId, Arg.Any<CancellationToken>()).Returns(task);
        _tableRepo.GetByIdAsync(ProjectTableId, Arg.Any<CancellationToken>()).Returns(project);
        _tableRepo.GetByIdAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(task);
        _tableRepo.GetByIdAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ClientTableId, AppId = AppId, Name = "Client" });
        _appRepo.GetPublicIdByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(AppPublicId);

        _fieldRepo.ListByTableAsync(ProjectTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, IsUnique = true },
        });
        _fieldRepo.ListByTableAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(_ => new List<AppField>
        {
            new() { Id = 120, Fid = AmountFid, AppTableId = TaskTableId, Name = "Amount", TypeCode = "Number" },
            new() { Id = 100, Fid = 10, AppTableId = TaskTableId, Name = "Client", TypeCode = "Reference" },
        });

        // Task is already the child of Client (reference fid 10).
        _relRepo.ListByChildTableAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 1, ParentTableId = ClientTableId, ChildTableId = TaskTableId, ReferenceFid = 10 },
        });

        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new FieldType { Id = 1, Code = ci.Arg<string>() });
        _nameResolver.GenerateUniqueNameAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => "C_" + ci.ArgAt<string>(1).Replace(" ", ""));
        _fieldRepo.GetNextFidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(15);
        _fieldRepo.LabelExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _relRepo.CreateAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>()).Returns((2L, Guid.NewGuid()));
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>()).Returns((99L, Guid.NewGuid()));
        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Form>());
    }

    private void ClientHasSummaryOverAmount(string function) =>
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new()
            {
                Id = 30, Fid = 30, AppTableId = ClientTableId, Name = $"{function} of Amount", Label = $"{function} of Amount", TypeCode = "Summary",
                Settings = $"{{\"childTableId\":{TaskTableId},\"referenceFid\":10,\"function\":\"{function}\",\"targetFid\":{AmountFid}}}",
            },
        });

    private static CreateRelationshipCommand ReuseAmountAsReference() => new(
        AppPublicId: AppPublicId,
        ParentTablePublicId: ProjectPublicId,
        ChildTablePublicId: TaskPublicId,
        ReferenceFieldLabel: null,
        IsReferenceRequired: false,
        Lookups: [],
        Summaries: [],
        ReferenceFieldFid: AmountFid);

    // ── Summaries created together with a (brand-new) relationship ──

    private static CreateRelationshipCommand NewRelationshipWithSummary(string function, int? targetFid = AmountFid) => new(
        AppPublicId: AppPublicId,
        ParentTablePublicId: ProjectPublicId,
        ChildTablePublicId: TaskPublicId,
        ReferenceFieldLabel: "Project",
        IsReferenceRequired: false,
        Lookups: [],
        Summaries: [new CreateSummarySpec("Total", function, targetFid)]);

    private List<AppField> CaptureCreatedFields()
    {
        var created = new List<AppField>();
        _fieldRepo.When(r => r.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>()))
            .Do(ci => created.Add(ci.Arg<AppField>()));
        return created;
    }

    [Fact]
    public async Task Handle_SummaryWithUnknownFunction_ThrowsValidation_AndWritesNothing()
    {
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(new App { Id = AppId });
        var created = CaptureCreatedFields();

        (await FluentActions.Invoking(() => _handler.HandleAsync(NewRelationshipWithSummary("Bogus")))
            .Should().ThrowAsync<ValidationException>()).Which.Errors["summaries"].Single().Should().Contain("Unknown summary function 'Bogus'");
        created.Should().BeEmpty();
        await _relRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Handle_SummaryFunctionInLowercase_IsSavedCanonical()
    {
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(new App { Id = AppId });
        var created = CaptureCreatedFields();

        await _handler.HandleAsync(NewRelationshipWithSummary("sum"));

        created.Single(f => f.TypeCode == "Summary").Settings.Should().Contain("\"function\":\"Sum\"");
    }

    [Fact]
    public async Task Handle_SummariesInEncryptedApp_ThrowsValidation_AndWritesNothing()
    {
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(new App { Id = AppId, IsEncrypted = true });
        var created = CaptureCreatedFields();

        (await FluentActions.Invoking(() => _handler.HandleAsync(NewRelationshipWithSummary("Count", targetFid: null)))
            .Should().ThrowAsync<ValidationException>()).Which.Errors["summaries"].Single().Should().Contain("encrypted app");
        created.Should().BeEmpty();
        await _relRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Handle_NumberFieldSummedByAnotherRelationship_ThrowsConflictAndWritesNothing()
    {
        ClientHasSummaryOverAmount("Sum");

        var act = () => _handler.HandleAsync(ReuseAmountAsReference());

        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("Client › Sum of Amount (Sum)");
        await _fieldRepo.DidNotReceiveWithAnyArgs().UpdateFieldTypeAsync(default, default, default, default, default);
        await _relRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Handle_NumberFieldWithSummaryThatSupportsReference_ConvertsIt()
    {
        // Distinct Count works on any type — converting Amount doesn't break it.
        ClientHasSummaryOverAmount("DistinctCount");

        await _handler.HandleAsync(ReuseAmountAsReference());

        await _fieldRepo.Received(1).UpdateFieldTypeAsync(120, Arg.Any<long>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>());
        await _relRepo.Received(1).CreateAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }
}
