using System.Data;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Fields;

/// <summary>
/// A field of an encryption-exempt type (Reference, Number, Date, Boolean, User, ...) can still carry IsEncrypted = true in its metadata
/// from before those types were exempted (the ExemptFieldEncryptionCleanupService exists to migrate them; until it has run the stored
/// column may still hold ciphertext). Saving that field's ordinary settings must not be read as "un-encrypting" it: the server used to
/// force the flag to false for exempt types, saw true -> false, and refused with "A field that has been encrypted cannot be un-encrypted
/// if the table has existing records" even though the user changed something unrelated. The flag is now left exactly as stored.
/// A genuine un-encrypt of a field that can be encrypted must still be refused.
/// </summary>
public class UpdateFieldLegacyEncryptionFlagTests
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
    private readonly FieldSettingsValidatorRegistry _settingsRegistry = new(Array.Empty<IFieldSettingsValidator>());

    public UpdateFieldLegacyEncryptionFlagTests()
    {
        _uow.Transaction.Returns((IDbTransaction?)null);
    }

    private UpdateFieldCommandHandler MakeSut() => new(
        _tableRepo, _fieldRepo, _recordRepo, _auditRepo, _schemaEngine,
        new FieldSettingsGuard(_permRepo, _recordRepo, _settingsRegistry),
        new FieldVersionService(_fieldVersionRepo, _queryContext),
        _uow, _fieldTypeRepo, _messagePublisher, _queryContext, _searchService,
        Substitute.For<IRelationshipRepository>());

    /// <summary>A table that already has records, which is what turns an encryption change into a refusal.</summary>
    private AppTable MakeTableWithRecords()
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid(), Name = "T" };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(),
            Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>()).Returns(25);
        return table;
    }

    private AppField MakeField(AppTable table, string typeCode, bool isEncrypted)
    {
        var field = new AppField
        {
            Id = 42, PublicId = Guid.NewGuid(), AppTableId = table.Id, TypeCode = typeCode, Label = "Customer",
            IsSystem = false, IsEncrypted = isEncrypted, IsSearchable = false, IsSortable = true, IsFilterable = true,
            IsReportable = true, Fid = null,
        };
        _fieldRepo.GetByPublicIdAsync(field.PublicId, Arg.Any<CancellationToken>()).Returns(field);
        _fieldRepo.LabelExistsInTableAsync(table.Id, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.UpdateAsync(
            field.PublicId, table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(1);
        return field;
    }

    /// <summary>A save that touches nothing about encryption: same label, same flags, only the description changes.</summary>
    private static UpdateFieldCommand OrdinarySave(AppTable table, AppField field, bool isEncrypted) => new(
        table.PublicId, field.PublicId,
        Label: field.Label!, Description: "a new description", IsRequired: false, DefaultValue: null,
        IsSearchable: false, IsSortable: true, IsFilterable: true, IsReportable: true, IsAuditable: false,
        IsUnique: false, IsEncrypted: isEncrypted, IsAutoFill: false, Settings: null, CommitMessage: "Ordinary settings save");

    [Theory]
    [InlineData("Reference", true)]
    [InlineData("Reference", false)]
    [InlineData("Number", true)]
    [InlineData("Number", false)]
    [InlineData("Date", false)]
    [InlineData("User", false)]
    public async Task ExemptTypeStillFlaggedEncrypted_SavesWithoutBeingTreatedAsUnencrypting(string typeCode, bool requestedFlag)
    {
        var table = MakeTableWithRecords();
        var field = MakeField(table, typeCode, isEncrypted: true);

        var save = () => MakeSut().HandleAsync(OrdinarySave(table, field, requestedFlag));

        await save.Should().NotThrowAsync();
        // The stored flag is left as it was, and the column is not re-typed: the cleanup service owns that migration.
        await _fieldRepo.Received(1).UpdateAsync(
            Arg.Is(field.PublicId), Arg.Is(table.Id), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), /* isEncrypted */ Arg.Is(true), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>());
        await _schemaEngine.DidNotReceiveWithAnyArgs().SetColumnEncryptionAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("Reference")]
    [InlineData("Number")]
    public async Task ExemptTypeNotEncrypted_StaysUnencrypted_EvenWhenTheRequestAsksForTrue(string typeCode)
    {
        var table = MakeTableWithRecords();
        var field = MakeField(table, typeCode, isEncrypted: false);

        await MakeSut().HandleAsync(OrdinarySave(table, field, isEncrypted: true));

        await _fieldRepo.Received(1).UpdateAsync(
            Arg.Is(field.PublicId), Arg.Is(table.Id), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), /* isEncrypted */ Arg.Is(false), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>());
    }

    [Fact]
    public async Task EncryptableFieldWithRecords_ReallyBeingUnencrypted_IsStillRefused()
    {
        var table = MakeTableWithRecords();
        var field = MakeField(table, "Text", isEncrypted: true);

        var save = () => MakeSut().HandleAsync(OrdinarySave(table, field, isEncrypted: false));

        var thrown = await save.Should().ThrowAsync<ValidationException>();
        thrown.Which.Errors["IsEncrypted"].Should().ContainSingle(m => m.Contains("cannot be un-encrypted"));
    }

    [Fact]
    public async Task EncryptableFieldWithRecords_KeepingItsEncryption_SavesNormally()
    {
        var table = MakeTableWithRecords();
        var field = MakeField(table, "Text", isEncrypted: true);

        var save = () => MakeSut().HandleAsync(OrdinarySave(table, field, isEncrypted: true));

        await save.Should().NotThrowAsync();
    }
}
