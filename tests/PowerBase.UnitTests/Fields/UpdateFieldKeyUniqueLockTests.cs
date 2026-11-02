using System.Data;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Fields;

/// <summary>
/// A key must stay unique: Unique can't be turned off on the table's key field (Set Key) or on a field a
/// relationship uses as its key (FieldSettingsGuard.ValidateKeyStaysUniqueAsync). The refusal happens before the
/// unique index is dropped or anything is saved.
/// </summary>
public class UpdateFieldKeyUniqueLockTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppRolePermissionRepository _permRepo = Substitute.For<IAppRolePermissionRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IMessagePublisher _messagePublisher = Substitute.For<IMessagePublisher>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAzureSearchService _searchService = Substitute.For<IAzureSearchService>();
    private readonly IFieldVersionRepository _fieldVersionRepo = Substitute.For<IFieldVersionRepository>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly FieldSettingsValidatorRegistry _settingsRegistry = new(Array.Empty<IFieldSettingsValidator>());

    private const long DepartmentTableId = 10;
    private const long EmployeeTableId = 20;
    private const long DeptNameId = 101;
    private const long DeptCodeId = 102;

    public UpdateFieldKeyUniqueLockTests()
    {
        _uow.Transaction.Returns((IDbTransaction?)null);
        _tableRepo.GetByIdAsync(EmployeeTableId, Arg.Any<CancellationToken>())
            .Returns(new AppTable { Id = EmployeeTableId, Name = "Employees" });
        _relRepo.ListByTableAsync(DepartmentTableId, Arg.Any<CancellationToken>()).Returns(new List<Relationship>());
    }

    private UpdateFieldCommandHandler MakeSut() => new(
        _tableRepo, _fieldRepo, _recordRepo, _auditRepo, _schemaEngine,
        new FieldSettingsGuard(_permRepo, _recordRepo, _settingsRegistry, _relRepo, _tableRepo),
        new FieldVersionService(_fieldVersionRepo, _queryContext),
        _uow, _fieldTypeRepo, _messagePublisher, _queryContext, _searchService, _relRepo);

    private AppTable ArrangeDepartment(long? keyFieldId)
    {
        var table = new AppTable { Id = DepartmentTableId, PublicId = Guid.NewGuid(), Name = "Department", KeyFieldId = keyFieldId };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.LabelExistsInTableAsync(DepartmentTableId, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.UpdateAsync(
            Arg.Any<Guid>(), DepartmentTableId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(1);
        return table;
    }

    private AppField ArrangeField(long id, string label, bool isUnique = true)
    {
        var field = new AppField
        {
            Id = id, Fid = (int)(id - 95), PublicId = Guid.NewGuid(), AppTableId = DepartmentTableId,
            Name = label, Label = label, TypeCode = "Text", IsUnique = isUnique, IsRequired = true,
        };
        _fieldRepo.GetByPublicIdAsync(field.PublicId, Arg.Any<CancellationToken>()).Returns(field);
        return field;
    }

    private void ArrangeRelationships(params Relationship[] relationships) =>
        _relRepo.ListByTableAsync(DepartmentTableId, Arg.Any<CancellationToken>()).Returns(relationships.ToList());

    private static Relationship EmployeesToDepartment(long? displayKeyFieldId = null) =>
        new() { Id = 1, ParentTableId = DepartmentTableId, ChildTableId = EmployeeTableId, DisplayKeyFieldId = displayKeyFieldId };

    private static UpdateFieldCommand Update(AppTable table, AppField field, bool isUnique, string? label = null) => new(
        table.PublicId, field.PublicId,
        Label: label ?? field.Label!, Description: null, IsRequired: true, DefaultValue: null,
        IsSearchable: true, IsSortable: false, IsFilterable: false, IsReportable: true, IsAuditable: false,
        IsUnique: isUnique, IsEncrypted: false, IsAutoFill: false,
        Settings: null, CommitMessage: "test");

    private async Task ShouldRefuseAsync(AppTable table, AppField field, string expectedMessagePart)
    {
        var act = () => MakeSut().HandleAsync(Update(table, field, isUnique: false));

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().ContainKey("IsUnique");
        ex.Which.Errors["IsUnique"].Single().Should().Contain(expectedMessagePart);

        // Refused before the index is dropped or the field row is written.
        await _schemaEngine.DidNotReceiveWithAnyArgs().SetUniqueAsync(default!, default!, default, default);
        await _fieldRepo.DidNotReceiveWithAnyArgs().UpdateAsync(
            default, default, default!, default, default, default, default, default, default, default, default,
            default, default, default, default, default, default);
    }

    [Fact]
    public async Task Handle_TurnUniqueOffOnTableKeyUsedByRelationship_IsRefused()
    {
        var table = ArrangeDepartment(keyFieldId: DeptNameId);
        var deptName = ArrangeField(DeptNameId, "Department Name");
        ArrangeRelationships(EmployeesToDepartment());

        await ShouldRefuseAsync(table, deptName, "key of the relationship Employees → Department");
    }

    [Fact]
    public async Task Handle_TurnUniqueOffOnTableKeyWithNoRelationship_IsRefused()
    {
        var table = ArrangeDepartment(keyFieldId: DeptNameId);
        var deptName = ArrangeField(DeptNameId, "Department Name");

        await ShouldRefuseAsync(table, deptName, "key field of Department");
    }

    [Fact]
    public async Task Handle_TurnUniqueOffOnAlternateKey_IsRefused()
    {
        var table = ArrangeDepartment(keyFieldId: null);
        var deptCode = ArrangeField(DeptCodeId, "Department Code");
        ArrangeRelationships(EmployeesToDepartment(displayKeyFieldId: DeptCodeId));

        await ShouldRefuseAsync(table, deptCode, "key of the relationship Employees → Department");
    }

    [Fact]
    public async Task Handle_TurnUniqueOffOnFieldNoRelationshipUses_IsSaved()
    {
        var table = ArrangeDepartment(keyFieldId: DeptNameId);
        var deptCode = ArrangeField(DeptCodeId, "Department Code");
        ArrangeRelationships(EmployeesToDepartment());

        await MakeSut().HandleAsync(Update(table, deptCode, isUnique: false));

        await _schemaEngine.Received(1).SetUniqueAsync(table, deptCode, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RenameKeyFieldKeepingUnique_IsSaved()
    {
        var table = ArrangeDepartment(keyFieldId: DeptNameId);
        var deptName = ArrangeField(DeptNameId, "Department Name");
        ArrangeRelationships(EmployeesToDepartment());

        await MakeSut().HandleAsync(Update(table, deptName, isUnique: true, label: "Dept Name"));

        await _schemaEngine.DidNotReceive().SetUniqueAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_KeySavedWithoutUniqueBeforeTheRule_CanStillBeEdited()
    {
        var table = ArrangeDepartment(keyFieldId: DeptNameId);
        var deptName = ArrangeField(DeptNameId, "Department Name", isUnique: false);

        await MakeSut().HandleAsync(Update(table, deptName, isUnique: false, label: "Dept Name"));
    }
}
