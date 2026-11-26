using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// CreateRelationship saves the type a Reference is shown as in its settings (ReferenceSettings.ResultTypeCode):
/// a new Reference field takes the parent key's type (Number for Record ID#), an existing child field converted into
/// the Reference keeps its own type.
/// </summary>
public class CreateRelationshipReferenceResultTypeTests
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
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly CreateRelationshipCommandHandler _handler;
    private readonly List<(long FieldId, string? Settings)> _settingsWrites = [];

    private const long AppId = 10;
    private const long DeptTableId = 6;     // parent
    private const long EmpTableId = 77;     // child
    private const long NewRelId = 2;
    private const long NewFieldId = 99;
    private static readonly Guid AppPublicId = Guid.NewGuid();
    private static readonly Guid DeptPublicId = Guid.NewGuid();
    private static readonly Guid EmpPublicId = Guid.NewGuid();

    private readonly AppTable _dept = new() { Id = DeptTableId, PublicId = DeptPublicId, AppId = AppId, Name = "Department" };
    private readonly List<AppField> _deptFields =
    [
        new() { Id = 3, Fid = 3, AppTableId = DeptTableId, Name = "Record ID#", TypeCode = "Number", IsSystem = true, IsUnique = true },
        new() { Id = 60, Fid = 6, AppTableId = DeptTableId, Name = "Dept Name", TypeCode = "Text", IsUnique = true },
        new() { Id = 70, Fid = 7, AppTableId = DeptTableId, Name = "Budget Code", TypeCode = "Currency", IsUnique = true },
    ];
    private readonly List<AppField> _empFields =
    [
        new() { Id = 120, Fid = 12, AppTableId = EmpTableId, Name = "Dept No", TypeCode = "Number" },
        new() { Id = 130, Fid = 13, AppTableId = EmpTableId, Name = "Dept Budget", TypeCode = "Currency" },
        new() { Id = 140, Fid = 14, AppTableId = EmpTableId, Name = "Dept Text", TypeCode = "Text" },
    ];

    public CreateRelationshipReferenceResultTypeTests()
    {
        var fieldFactory = new RelationshipFieldFactory(_fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _nameResolver);
        _handler = new CreateRelationshipCommandHandler(_tableRepo, _fieldRepo, _fieldTypeRepo, _relRepo, fieldFactory, _auditRepo, _appRepo,
            _schemaEngine, _recordRepo, Substitute.For<IFormulaProjector>(), Substitute.For<IRelationalProjector>());

        var emp = new AppTable { Id = EmpTableId, PublicId = EmpPublicId, AppId = AppId, Name = "Employee" };
        _tableRepo.GetByPublicIdAsync(DeptPublicId, Arg.Any<CancellationToken>()).Returns(_dept);
        _tableRepo.GetByPublicIdAsync(EmpPublicId, Arg.Any<CancellationToken>()).Returns(emp);
        _tableRepo.GetByIdAsync(DeptTableId, Arg.Any<CancellationToken>()).Returns(_dept);
        _tableRepo.GetByIdAsync(EmpTableId, Arg.Any<CancellationToken>()).Returns(emp);
        _appRepo.GetPublicIdByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(AppPublicId);
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(new App { Id = AppId });

        _fieldRepo.ListByTableAsync(DeptTableId, Arg.Any<CancellationToken>()).Returns(_ => _deptFields);
        _fieldRepo.ListByTableAsync(EmpTableId, Arg.Any<CancellationToken>()).Returns(_ => _empFields);
        _relRepo.ListByChildTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<Relationship>());
        _relRepo.ListByParentTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<Relationship>());

        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new FieldType { Id = 1, Code = ci.Arg<string>() });
        _nameResolver.GenerateUniqueNameAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => "C_" + ci.ArgAt<string>(1).Replace(" ", ""));
        _fieldRepo.GetNextFidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(15);
        _fieldRepo.LabelExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _relRepo.CreateAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>()).Returns((NewRelId, Guid.NewGuid()));
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>()).Returns((NewFieldId, Guid.NewGuid()));
        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Form>());
        _recordRepo.ListAllRowsDecryptedAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        _fieldRepo.When(r => r.UpdateSettingsAsync(Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(ci => _settingsWrites.Add((ci.ArgAt<long>(0), ci.ArgAt<string?>(1))));
    }

    private static CreateRelationshipCommand NewReference(int? displayKeyFid = null) => new(
        AppPublicId: AppPublicId, ParentTablePublicId: DeptPublicId, ChildTablePublicId: EmpPublicId,
        ReferenceFieldLabel: "Related Department", IsReferenceRequired: false, Lookups: [], Summaries: [],
        DisplayKeyFieldFid: displayKeyFid);

    private static CreateRelationshipCommand ExistingReference(int fid) => new(
        AppPublicId: AppPublicId, ParentTablePublicId: DeptPublicId, ChildTablePublicId: EmpPublicId,
        ReferenceFieldLabel: null, IsReferenceRequired: false, Lookups: [], Summaries: [], ReferenceFieldFid: fid);

    /// <summary>The Reference settings saved once the relationship exists (the write that carries its RelationshipId).</summary>
    private ReferenceSettings SavedReferenceSettings(long fieldId) =>
        _settingsWrites.Where(w => w.FieldId == fieldId)
            .Select(w => JsonSerializer.Deserialize<ReferenceSettings>(w.Settings ?? "null", JsonOpts))
            .Last(s => s?.RelationshipId == NewRelId)!;

    [Fact]
    public async Task NewReference_ParentKeyedByRecordId_SavesNumber()
    {
        await _handler.HandleAsync(NewReference());

        SavedReferenceSettings(NewFieldId).ResultTypeCode.Should().Be("Number");
    }

    [Fact]
    public async Task NewReference_ParentWithSetKey_SavesTheKeysType()
    {
        _dept.KeyFieldId = 60;   // Dept Name (Text)

        await _handler.HandleAsync(NewReference());

        SavedReferenceSettings(NewFieldId).ResultTypeCode.Should().Be("Text");
    }

    [Fact]
    public async Task NewReference_WithDisplayKey_SavesTheDisplayKeysType()
    {
        _dept.KeyFieldId = 60;   // the relationship's own display key wins over the table's Set Key

        await _handler.HandleAsync(NewReference(displayKeyFid: 7));   // Budget Code (Currency)

        SavedReferenceSettings(NewFieldId).ResultTypeCode.Should().Be("Currency");
    }

    [Fact]
    public async Task ExistingNumberField_RecordIdKey_SavesItsOwnType()
    {
        await _handler.HandleAsync(ExistingReference(12));   // Dept No (Number): metadata-only conversion

        var saved = SavedReferenceSettings(120);
        saved.ResultTypeCode.Should().Be("Number");
        saved.OriginalTypeCode.Should().BeNull();            // restore info unchanged
    }

    [Fact]
    public async Task ExistingCurrencyField_NumberKey_SavesItsOwnType()
    {
        await _handler.HandleAsync(ExistingReference(13));   // Dept Budget (Currency) against Record ID# (Number)

        var saved = SavedReferenceSettings(130);
        saved.ResultTypeCode.Should().Be("Currency");
        saved.OriginalTypeCode.Should().Be("Currency");
    }

    [Fact]
    public async Task ExistingTextField_TextKey_SavesItsOwnType()
    {
        _dept.KeyFieldId = 60;   // Dept Name (Text)

        await _handler.HandleAsync(ExistingReference(14));   // Dept Text (Text)

        SavedReferenceSettings(140).ResultTypeCode.Should().Be("Text");
    }
}
