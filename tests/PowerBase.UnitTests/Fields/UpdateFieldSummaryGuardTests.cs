using System.Data;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Fields;

/// <summary>
/// The numeric "Display As" switch (Number ↔ Currency ↔ Percent ↔ Rating) changes a field's type,
/// so it must consult SummaryDependencyGuard before persisting. Every summary function supports
/// all four numeric types, so the guard can't block such a switch today — these tests pin down that
/// it is consulted (so it protects any wider switch added later) and that it lets the switch through.
/// </summary>
public class UpdateFieldSummaryGuardTests
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

    private const long TaskTableId = 77;
    private const long ClientTableId = 5;

    public UpdateFieldSummaryGuardTests()
    {
        _uow.Transaction.Returns((IDbTransaction?)null);
        _fieldTypeRepo.GetByCodeAsync("Currency", Arg.Any<CancellationToken>()).Returns(new FieldType { Id = 9, Code = "Currency" });

        // Client › "Sum of Amount" summarizes Task.Amount (fid 12) through reference fid 10.
        _relRepo.ListByChildTableAsync(TaskTableId, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 1, ParentTableId = ClientTableId, ChildTableId = TaskTableId, ReferenceFid = 10 },
        });
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 30, Fid = 30, AppTableId = ClientTableId, Name = "Sum of Amount", TypeCode = "Summary",
                    Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Sum\",\"targetFid\":12}" },
        });
    }

    private UpdateFieldCommandHandler MakeSut() => new(
        _tableRepo, _fieldRepo, _recordRepo, _auditRepo, _schemaEngine,
        new FieldSettingsGuard(_permRepo, _recordRepo, _settingsRegistry),
        new FieldVersionService(_fieldVersionRepo, _queryContext),
        _uow, _fieldTypeRepo, _messagePublisher, _queryContext, _searchService, _relRepo);

    private (AppTable Table, AppField Amount) ArrangeAmountField()
    {
        var table = new AppTable { Id = TaskTableId, PublicId = Guid.NewGuid(), Name = "Task" };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);

        var amount = new AppField
        {
            Id = 120, Fid = 12, PublicId = Guid.NewGuid(), AppTableId = TaskTableId,
            Name = "Amount", Label = "Amount", TypeCode = "Number", IsSearchable = true,
        };
        _fieldRepo.GetByPublicIdAsync(amount.PublicId, Arg.Any<CancellationToken>()).Returns(amount);
        _fieldRepo.LabelExistsInTableAsync(TaskTableId, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.UpdateAsync(
            amount.PublicId, TaskTableId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(1);
        return (table, amount);
    }

    private static UpdateFieldCommand Update(AppTable table, AppField field, string? settings) => new(
        table.PublicId, field.PublicId,
        Label: "Amount", Description: null, IsRequired: false, DefaultValue: null,
        IsSearchable: true, IsSortable: false, IsFilterable: false, IsReportable: true, IsAuditable: false,
        IsUnique: false, IsEncrypted: false, IsAutoFill: false,
        Settings: settings, CommitMessage: "Display as currency");

    [Fact]
    public async Task Handle_DisplayAsSwitch_ConsultsSummaryGuardThenSwitchesType()
    {
        var (table, amount) = ArrangeAmountField();

        await MakeSut().HandleAsync(Update(table, amount, "{\"displayAs\":\"Currency\"}"));

        // The guard looked for summaries over Amount…
        await _relRepo.Received(1).ListByChildTableAsync(TaskTableId, Arg.Any<CancellationToken>());
        await _fieldRepo.Received(1).ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>());
        // …and, since Sum still works on Currency, the switch went through.
        await _fieldRepo.Received(1).UpdateFieldTypeAsync(120, 9, Arg.Any<string?>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoTypeChange_DoesNotConsultSummaryGuard()
    {
        var (table, amount) = ArrangeAmountField();

        await MakeSut().HandleAsync(Update(table, amount, "{\"decimals\":2}"));

        await _relRepo.DidNotReceiveWithAnyArgs().ListByChildTableAsync(default, default);
        await _fieldRepo.DidNotReceiveWithAnyArgs().UpdateFieldTypeAsync(default, default, default, default, default);
    }
}
