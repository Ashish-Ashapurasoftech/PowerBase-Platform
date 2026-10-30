using System.Data;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Fields;

/// <summary>
/// Saving a field brings the search index along: switching Searchable on or off rewrites the records even when the field stays
/// filterable (so its text joins or leaves quick search), and a failing index never fails the save or loses the rest of the job.
/// </summary>
public class UpdateFieldSearchSyncTests
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

    private readonly AppTable _table = new() { Id = 5, AppId = 2, PublicId = Guid.NewGuid(), Name = "T" };

    public UpdateFieldSearchSyncTests()
    {
        _uow.Transaction.Returns((IDbTransaction?)null);
        _queryContext.TenantId.Returns(1);
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
    }

    private UpdateFieldCommandHandler MakeSut() => new(
        _tableRepo, _fieldRepo, _recordRepo, _auditRepo, _schemaEngine,
        new FieldSettingsGuard(_permRepo, _recordRepo, new FieldSettingsValidatorRegistry(Array.Empty<IFieldSettingsValidator>())),
        new FieldVersionService(_fieldVersionRepo, _queryContext),
        _uow, _fieldTypeRepo, _messagePublisher, _queryContext, _searchService,
        Substitute.For<IRelationshipRepository>());

    /// <summary>A plain Text field (Fid 6) as stored before the save, and the table as the database lists it after the save.</summary>
    private AppField ExistingField(bool searchable, bool filterable, bool searchableAfter)
    {
        var field = new AppField
        {
            Id = 42, PublicId = Guid.NewGuid(), AppTableId = _table.Id, TypeCode = "Text", Label = "Country", Fid = 6,
            IsSearchable = searchable, IsFilterable = filterable, IsReportable = true, SearchSlot = "str_01",
        };
        _fieldRepo.GetByPublicIdAsync(field.PublicId, Arg.Any<CancellationToken>()).Returns(field);
        _fieldRepo.LabelExistsInTableAsync(_table.Id, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.UpdateAsync(
            field.PublicId, _table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(1);
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 42, Fid = 6, TypeCode = "Text", IsSearchable = searchableAfter, IsFilterable = filterable, SearchSlot = "str_01" },
            new() { Id = 43, Fid = 7, TypeCode = "Text", IsSearchable = true, IsFilterable = true, SearchSlot = "str_02" },
        });
        _recordRepo.GetTableIndexPageAsync(Arg.Any<long>(), Arg.Is(_table.Id), Arg.Is(0L), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<int>())
            .Returns((new List<SearchIndexDocument>
            {
                new(1, 2, _table.Id, Guid.NewGuid(), new Dictionary<long, object?> { [6] = "Germany", [7] = "Berlin" })
            }, 1L));
        return field;
    }

    private UpdateFieldCommand Command(AppField field, bool searchable) => new(
        _table.PublicId, field.PublicId,
        Label: field.Label!,
        Description: null,
        IsRequired: false,
        DefaultValue: null,
        IsSearchable: searchable,
        IsSortable: false,
        IsFilterable: field.IsFilterable,
        IsReportable: true,
        IsAuditable: false,
        IsUnique: false,
        IsEncrypted: false,
        IsAutoFill: false,
        Settings: null,
        CommitMessage: "search settings");

    [Fact]
    public async Task Switching_searchable_on_for_a_filterable_field_rewrites_the_records_with_its_text()
    {
        var field = ExistingField(searchable: false, filterable: true, searchableAfter: true);

        await MakeSut().HandleAsync(Command(field, searchable: true));

        await _searchService.Received(1).BulkIndexRecordsAsync(
            Arg.Is<IEnumerable<SearchIndexDocument>>(docs => docs.Single().CompleteText
                && docs.Single().SearchTextSlots!.SetEquals(new[] { "str_01", "str_02" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switching_searchable_off_on_a_field_that_stays_filterable_rewrites_the_records_without_its_text()
    {
        var field = ExistingField(searchable: true, filterable: true, searchableAfter: false);

        await MakeSut().HandleAsync(Command(field, searchable: false));

        await _searchService.Received(1).BulkIndexRecordsAsync(
            Arg.Is<IEnumerable<SearchIndexDocument>>(docs => docs.Single().CompleteText
                && docs.Single().SearchTextSlots!.SetEquals(new[] { "str_02" })
                && (string?)docs.Single().SlotValues!["str_01"] == "Germany"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_save_that_does_not_change_search_settings_does_not_touch_the_index()
    {
        var field = ExistingField(searchable: true, filterable: true, searchableAfter: true);

        await MakeSut().HandleAsync(Command(field, searchable: true));

        await _recordRepo.DidNotReceiveWithAnyArgs().GetTableIndexPageAsync(default, default, default, default, default, default);
        await _messagePublisher.DidNotReceive().PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_index_failure_after_the_save_neither_fails_the_save_nor_loses_the_job()
    {
        var field = ExistingField(searchable: false, filterable: false, searchableAfter: true);
        _searchService.BulkIndexRecordsAsync(Arg.Any<IEnumerable<SearchIndexDocument>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Failed to bulk index records in Azure AI Search."));

        await MakeSut().Invoking(s => s.HandleAsync(Command(field, searchable: true))).Should().NotThrowAsync();

        await _messagePublisher.Received(1).PublishAsync(
            Arg.Is<SearchIndexMessage>(m => m.Action == IndexAction.BackfillField && m.FieldId == 6 && m.AfterId == 0), Arg.Any<CancellationToken>());
        await _auditRepo.ReceivedWithAnyArgs(1).LogActivityAsync(default!, default!, default!, default!);
    }
}
