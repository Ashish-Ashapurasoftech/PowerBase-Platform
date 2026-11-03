using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Import;

/// <summary>What an import needs from the relationship command: the file's own report link on its own number, no second "Add …" link, and
/// no report link at all when the source has none. The defaults (the relationship wizard) must stay exactly as they were.</summary>
public class ImportRelationshipLinksTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly List<AppField> _created = [];
    private readonly AppTable _parent = new() { Id = 1, PublicId = Guid.NewGuid(), AppId = 10, Name = "Departments" };
    private readonly AppTable _child = new() { Id = 2, PublicId = Guid.NewGuid(), AppId = 10, Name = "Employees" };
    private int _nextFid = 6;
    private long _nextId = 100;

    private CreateRelationshipCommandHandler CreateSut()
    {
        _tableRepo.GetByPublicIdAsync(_parent.PublicId, Arg.Any<CancellationToken>()).Returns(_parent);
        _tableRepo.GetByPublicIdAsync(_child.PublicId, Arg.Any<CancellationToken>()).Returns(_child);
        _fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci => new FieldType { Id = 1, Code = ci.Arg<string>() });
        _relRepo.CreateAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>()).Returns((5L, Guid.NewGuid()));
        _appRepo.GetPublicIdByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _fieldRepo.LabelExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.GetNextFidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(_ => _nextFid++);
        _nameResolver.GenerateUniqueNameAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(ci => ci.ArgAt<string>(1));
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var f = ci.Arg<AppField>();
            _created.Add(f);
            return (_nextId++, Guid.NewGuid());
        });

        var factory = new RelationshipFieldFactory(_fieldRepo, _fieldTypeRepo, Substitute.For<ISchemaEngineService>(), Substitute.For<IFormRepository>(),
            Substitute.For<IQueryContext>(), _nameResolver);
        return new CreateRelationshipCommandHandler(_tableRepo, _fieldRepo, _fieldTypeRepo, _relRepo, factory, Substitute.For<IAuditRepository>(), _appRepo,
            Substitute.For<ISchemaEngineService>(), Substitute.For<IRecordRepository>(), Substitute.For<IFormulaProjector>(), Substitute.For<IRelationalProjector>());
    }

    private CreateRelationshipCommand Command(Func<CreateRelationshipCommand, CreateRelationshipCommand>? change = null)
    {
        var command = new CreateRelationshipCommand(Guid.NewGuid(), _parent.PublicId, _child.PublicId, "Related Department", false, [], []);
        return change is null ? command : change(command);
    }

    [Fact]
    public async Task Handle_Defaults_CreatesTheReportLinkAndTheAddLinkAsBefore()
    {
        await CreateSut().HandleAsync(Command());

        _created.Where(f => f.TypeCode == "ReportLink").Should().ContainSingle().Which.Label.Should().Be("Employees records");
        _created.Where(f => f.TypeCode == "Formula_Url").Should().ContainSingle().Which.Label.Should().Be("Add Employees");
    }

    [Fact]
    public async Task Handle_ImportWithTheSourceReportLink_UsesItsLabelAndNumberAndAddsNoAddLink()
    {
        await CreateSut().HandleAsync(Command(c => c with { ReportLinkFid = 9, ReportLinkLabel = "Employee records", ReportLinkText = "See employees", CreateAddRecordLink = false }));

        var link = _created.Should().ContainSingle(f => f.TypeCode == "ReportLink").Subject;
        link.Fid.Should().Be(9);
        link.Label.Should().Be("Employee records");
        link.Settings.Should().Contain("See employees");
        _created.Should().NotContain(f => f.TypeCode == "Formula_Url");
    }

    [Fact]
    public async Task Handle_ImportWhoseSourceHasNoReportLink_CreatesNeitherLink()
    {
        await CreateSut().HandleAsync(Command(c => c with { CreateReportLink = false, CreateAddRecordLink = false }));

        _created.Should().NotContain(f => f.TypeCode == "ReportLink" || f.TypeCode == "Formula_Url");
        _created.Should().ContainSingle(f => f.TypeCode == "Reference");
    }

    [Fact]
    public async Task Handle_SourceNumberAlreadyTaken_FallsBackToTheNextFreeNumberInsteadOfFailing()
    {
        var sut = CreateSut();
        _fieldRepo.GetByFidInTableAsync(_parent.Id, 9, Arg.Any<CancellationToken>()).Returns(new AppField { Id = 1, Fid = 9, AppTableId = _parent.Id });

        await sut.HandleAsync(Command(c => c with { ReportLinkFid = 9, CreateAddRecordLink = false }));

        _created.Single(f => f.TypeCode == "ReportLink").Fid.Should().NotBe(9);
    }
}
