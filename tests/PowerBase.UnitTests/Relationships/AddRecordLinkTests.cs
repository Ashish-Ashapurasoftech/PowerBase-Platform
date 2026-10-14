using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Application.Relationships.Commands.DeleteRelationship;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Relationships;

/// <summary>Creating a relationship also creates the parent's "Add {child}" Formula URL (Quickbase's
/// "Add Child" link), which opens a new child record with the reference pre-filled; deleting the
/// relationship removes it.</summary>
public class AddRecordLinkTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    private readonly CreateRelationshipCommandHandler _handler;
    private readonly List<AppField> _created = [];

    private static readonly Guid AppPublicId = Guid.NewGuid();
    private static readonly Guid DeptPublicId = Guid.NewGuid();
    private static readonly Guid EmpPublicId = Guid.NewGuid();
    private const long AppId = 10, DeptTableId = 100, EmpTableId = 200, RelId = 7;
    private const int ReferenceFid = 8;

    private readonly AppTable _dept = new() { Id = DeptTableId, PublicId = DeptPublicId, AppId = AppId, Name = "Department" };
    private readonly AppTable _emp = new() { Id = EmpTableId, PublicId = EmpPublicId, AppId = AppId, Name = "Employee" };
    private readonly List<AppField> _deptFields =
    [
        new() { Id = 3, Fid = 3, Name = "Record ID#", Label = "Record ID#", TypeCode = "Number", IsSystem = true, IsUnique = true },
        new() { Id = 10, Fid = 6, Name = "Department Name", Label = "Department Name", TypeCode = "Text" },
    ];

    public AddRecordLinkTests()
    {
        var fieldFactory = new RelationshipFieldFactory(
            _fieldRepo, _fieldTypeRepo, Substitute.For<ISchemaEngineService>(), _formRepo,
            Substitute.For<IQueryContext>(), _nameResolver);
        _handler = new CreateRelationshipCommandHandler(
            _tableRepo, _fieldRepo, _fieldTypeRepo, _relRepo, fieldFactory, Substitute.For<IAuditRepository>(), _appRepo,
            Substitute.For<ISchemaEngineService>(), _recordRepo, Substitute.For<IFormulaProjector>(), Substitute.For<IRelationalProjector>());

        _tableRepo.GetByPublicIdAsync(DeptPublicId, Arg.Any<CancellationToken>()).Returns(_dept);
        _tableRepo.GetByPublicIdAsync(EmpPublicId, Arg.Any<CancellationToken>()).Returns(_emp);
        _tableRepo.GetByIdAsync(DeptTableId, Arg.Any<CancellationToken>()).Returns(_dept);
        _tableRepo.GetByIdAsync(EmpTableId, Arg.Any<CancellationToken>()).Returns(_emp);
        _appRepo.GetPublicIdByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(AppPublicId);

        _fieldRepo.ListByTableAsync(DeptTableId, Arg.Any<CancellationToken>()).Returns(_deptFields);
        _fieldRepo.ListByTableAsync(EmpTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new FieldType { Id = 1, Code = ci.Arg<string>() });
        _nameResolver.GenerateUniqueNameAsync(Arg.Any<long>(), Arg.Any<string>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => "C_" + ci.ArgAt<string>(1).Replace(" ", ""));
        _fieldRepo.GetNextFidAsync(EmpTableId, Arg.Any<CancellationToken>()).Returns(ReferenceFid);
        _fieldRepo.GetNextFidAsync(DeptTableId, Arg.Any<CancellationToken>()).Returns(15, 16);
        _fieldRepo.LabelExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _relRepo.CreateAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>()).Returns((RelId, Guid.NewGuid()));
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _created.Add(ci.Arg<AppField>()); return (99L, Guid.NewGuid()); });
        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Form>());
    }

    private Task<RelationshipDto> CreateAsync() => _handler.HandleAsync(new CreateRelationshipCommand(
        AppPublicId: AppPublicId,
        ParentTablePublicId: DeptPublicId,
        ChildTablePublicId: EmpPublicId,
        ReferenceFieldLabel: "Related Department",
        IsReferenceRequired: false,
        Lookups: [],
        Summaries: [],
        ReferenceFieldFid: null,
        DisplayKeyFieldFid: null));

    private AppField AddLink => _created.Single(f => f.TypeCode == "Formula_Url");

    private static FormulaUrlSettings SettingsOf(AppField f) =>
        JsonSerializer.Deserialize<FormulaUrlSettings>(f.Settings!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private sealed class MapContext(Dictionary<long, object?> values) : IRecordContext
    {
        public object? GetValue(long fid) => values.TryGetValue(fid, out var v) ? v : null;
    }

    private string Evaluate(object? recordId)
    {
        var engine = new FormulaEngine();
        var compiled = engine.Compile(SettingsOf(AddLink).Template!, new AppFieldSchema(_deptFields), FormulaType.Text);
        compiled.HasErrors.Should().BeFalse();
        var options = new EvaluationOptions { AppId = AppPublicId.ToString(), UrlRoot = "https://pb.test" };
        return engine.Evaluate(compiled, new MapContext(new() { [3] = recordId }), options).AsText();
    }

    [Fact]
    public async Task Handle_CreatesAddChildFormulaUrlOnParent()
    {
        var result = await CreateAsync();

        AddLink.AppTableId.Should().Be(DeptTableId);
        AddLink.Label.Should().Be("Add Employee");
        AddLink.IsSearchable.Should().BeFalse();   // Formula URL's own default — its text is a URL
        var settings = SettingsOf(AddLink);
        settings.RelationshipId.Should().Be(RelId);
        settings.LinkText.Should().Be("Add Employee");
        settings.DisplayAsButton.Should().BeTrue();
        settings.OpenTarget.Should().Be(UrlOpenTargets.SamePage);
        result.Fields.Should().Contain(f => f.Role == "addrecord" && f.TypeCode == "Formula_Url" && f.Name == "Add Employee");
    }

    [Fact]
    public async Task Template_LinksToChildNewRecordWithReferencePrefilled()
    {
        await CreateAsync();

        Evaluate(5m).Should().Be($"https://pb.test/app/{AppPublicId}/tables/{EmpPublicId}/records/new?prefill_{ReferenceFid}=5");
    }

    [Fact]
    public async Task Template_IsBlankOnUnsavedParent()
    {
        await CreateAsync();

        Evaluate(null).Should().BeEmpty();
    }

    [Fact]
    public async Task Template_StoresRecordIdByFid_SoARenameDoesNotBreakIt()
    {
        await CreateAsync();

        SettingsOf(AddLink).Template.Should().Contain("[fid_3|Record ID#]");
        _deptFields[0].Label = "Dept #";
        Evaluate(5m).Should().EndWith($"prefill_{ReferenceFid}=5");
    }

    [Fact]
    public async Task Delete_RemovesTheAddChildLink()
    {
        var rel = new Relationship
        {
            Id = RelId, PublicId = Guid.NewGuid(), AppId = AppId, ParentTableId = DeptTableId, ChildTableId = EmpTableId,
            ReferenceFieldId = 50, ReferenceFid = ReferenceFid,
        };
        var addLink = new AppField
        {
            Id = 60, PublicId = Guid.NewGuid(), Fid = 16, AppTableId = DeptTableId, Name = "Add Employee", TypeCode = "Formula_Url",
            Settings = $$"""{"relationshipId":{{RelId}},"template":"\"x\""}""",
        };
        var otherLink = new AppField
        {
            Id = 61, PublicId = Guid.NewGuid(), Fid = 17, AppTableId = DeptTableId, Name = "Website", TypeCode = "Formula_Url",
            Settings = """{"template":"\"https://example.com\""}""",
        };
        _deptFields.AddRange([addLink, otherLink]);
        _relRepo.GetByPublicIdAsync(rel.PublicId, Arg.Any<CancellationToken>()).Returns(rel);
        var handler = new DeleteRelationshipCommandHandler(_tableRepo, _fieldRepo, _relRepo, _recordRepo, Substitute.For<IAuditRepository>());

        await handler.HandleAsync(new DeleteRelationshipCommand(rel.PublicId, Force: false));

        await _fieldRepo.Received(1).DeleteAsync(addLink.PublicId, DeptTableId, Arg.Any<CancellationToken>());
        await _fieldRepo.DidNotReceive().DeleteAsync(otherLink.PublicId, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
