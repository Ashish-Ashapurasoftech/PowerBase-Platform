using System.Data;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Fields;

/// <summary>UpdateField's optional help text (Quickbase "fieldHelp"): null leaves it alone, a value sets it, blank clears it.</summary>
public class UpdateFieldHelpTextTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();

    private UpdateFieldCommandHandler MakeSut()
    {
        _uow.Transaction.Returns((IDbTransaction?)null);
        return new UpdateFieldCommandHandler(
            _tableRepo, _fieldRepo, Substitute.For<IRecordRepository>(), Substitute.For<IAuditRepository>(),
            Substitute.For<ISchemaEngineService>(),
            new FieldSettingsGuard(Substitute.For<IAppRolePermissionRepository>(), Substitute.For<IRecordRepository>(),
                new FieldSettingsValidatorRegistry(Array.Empty<IFieldSettingsValidator>())),
            new FieldVersionService(Substitute.For<IFieldVersionRepository>(), _queryContext),
            _uow, Substitute.For<IFieldTypeRepository>(), Substitute.For<IMessagePublisher>(), _queryContext, Substitute.For<IAzureSearchService>(),
            Substitute.For<IRelationshipRepository>());
    }

    private (AppTable Table, AppField Field) Arrange(string? helpText)
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid(), Name = "Task" };
        var field = new AppField
        {
            Id = 42, PublicId = Guid.NewGuid(), AppTableId = table.Id, TypeCode = "Text", Label = "T Text",
            IsSearchable = true, IsReportable = true, HelpText = helpText,
        };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.GetByPublicIdAsync(field.PublicId, Arg.Any<CancellationToken>()).Returns(field);
        _fieldRepo.LabelExistsInTableAsync(table.Id, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.UpdateAsync(
                field.PublicId, table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(1);
        return (table, field);
    }

    private static UpdateFieldCommand Command(AppTable table, AppField field, string? helpText) => new(
        table.PublicId, field.PublicId, "T Text", null, false, null, true, true, true, true, true,
        false, false, false, null, "help text", HelpText: helpText);

    [Fact]
    public async Task HelpText_Given_IsSaved()
    {
        var (table, field) = Arrange(null);
        await MakeSut().HandleAsync(Command(table, field, "  T Text help "));
        await _fieldRepo.Received(1).SetHelpTextAsync(42, "T Text help", Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>());
    }

    [Fact]
    public async Task HelpText_Null_LeavesItUnchanged()
    {
        var (table, field) = Arrange("old help");
        await MakeSut().HandleAsync(Command(table, field, null));
        await _fieldRepo.DidNotReceiveWithAnyArgs().SetHelpTextAsync(default, default, default, default);
    }

    [Fact]
    public async Task HelpText_Blank_ClearsIt()
    {
        var (table, field) = Arrange("old help");
        await MakeSut().HandleAsync(Command(table, field, ""));
        await _fieldRepo.Received(1).SetHelpTextAsync(42, null, Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>());
    }

    [Fact]
    public async Task HelpText_Same_IsNotWrittenAgain()
    {
        var (table, field) = Arrange("same");
        await MakeSut().HandleAsync(Command(table, field, "same"));
        await _fieldRepo.DidNotReceiveWithAnyArgs().SetHelpTextAsync(default, default, default, default);
    }
}
