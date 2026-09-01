using System.Linq;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Fields;

/// <summary>
/// System fields (Record ID#, Date Created/Modified, Record Owner, Last Modified By) only expose
/// a reduced settings surface on the Field Detail page — this proves the backend is the actual
/// enforcement point, not just the frontend hiding controls: whatever an UpdateField request
/// sends for a system field, the persisted values are coerced to the fixed allow-list. A custom
/// field of the same TypeCode must be completely unaffected.
/// </summary>
public class UpdateFieldSystemFieldCoercionTests
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
    // No validators registered — every TypeCode's Settings JSON passes through unvalidated, same
    // pattern FieldHandlerTests.cs already uses; the point of these tests is the coercion layer,
    // not per-type shape validation (that's covered by FieldSettingsValidators' own tests).
    private readonly FieldSettingsValidatorRegistry _settingsRegistry = new(Array.Empty<IFieldSettingsValidator>());

    private UpdateFieldCommandHandler MakeSut() => new(
        _tableRepo, _fieldRepo, _permRepo, _recordRepo, _auditRepo,
        _schemaEngine, _settingsRegistry, _fieldTypeRepo, _messagePublisher, _queryContext, _searchService);

    private AppTable MakeTable(long id = 5)
    {
        var table = new AppTable { Id = id, PublicId = Guid.NewGuid(), Name = "T" };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        return table;
    }

    /// <summary>An existing field with every "unsupported for a system field" flag deliberately
    /// true, so the coercion assertions below actually prove something flipped.</summary>
    private AppField MakeExistingField(AppTable table, bool isSystem, string typeCode, string? settings = null)
    {
        var field = new AppField
        {
            Id = 42,
            PublicId = Guid.NewGuid(),
            AppTableId = table.Id,
            TypeCode = typeCode,
            Label = "Record ID#",
            Description = "original description",
            IsSystem = isSystem,
            IsRequired = true,
            IsUnique = false,
            IsSearchable = true,
            IsSortable = true,
            IsFilterable = true,
            IsReportable = true,
            IsAuditable = true,
            IsEncrypted = false,
            Settings = settings,
            Fid = null, // skips the IsSearchable-changed search-index-sync branch entirely
        };
        _fieldRepo.GetByPublicIdAsync(field.PublicId, Arg.Any<CancellationToken>()).Returns(field);
        _fieldRepo.LabelExistsInTableAsync(table.Id, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.UpdateAsync(
            field.PublicId, table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(1);
        return field;
    }

    private static UpdateFieldCommand MakeAttackCommand(AppTable table, AppField field, string? settings = null) => new(
        table.PublicId, field.PublicId,
        Label: "Hacked Label",
        Description: "hacked description",
        IsRequired: true,
        DefaultValue: "some default",
        IsSearchable: field.IsSearchable, // unchanged — keeps the search-index-sync branch a no-op
        IsSortable: true,
        IsFilterable: true,
        IsReportable: false, // deliberately different from existing — should still pass through
        IsAuditable: true,
        IsUnique: true,
        IsEncrypted: false,
        Settings: settings);

    [Fact]
    public async Task SystemField_LabelAndDescriptionAreForcedBackToExistingValues()
    {
        var table = MakeTable();
        var field = MakeExistingField(table, isSystem: true, typeCode: "Number");
        var command = MakeAttackCommand(table, field);

        await MakeSut().HandleAsync(command);

        await _fieldRepo.Received(1).UpdateAsync(
            field.PublicId, table.Id, "Record ID#", "original description",
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemField_UnsupportedFlagsAreForcedFalse_EvenWhenRequestAsksForTrue()
    {
        var table = MakeTable();
        var field = MakeExistingField(table, isSystem: true, typeCode: "Number");
        var command = MakeAttackCommand(table, field);

        await MakeSut().HandleAsync(command);

        await _fieldRepo.Received(1).UpdateAsync(
            field.PublicId, table.Id, Arg.Any<string>(), Arg.Any<string?>(),
            /* isRequired */ false, /* defaultValue */ null,
            Arg.Any<bool>(), /* isSortable */ false,
            /* isFilterable */ false, Arg.Any<bool>(), /* isAuditable */ false,
            /* isUnique */ false, /* isEncrypted */ false, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemField_SearchableAndReportablePassThroughUnchanged()
    {
        var table = MakeTable();
        var field = MakeExistingField(table, isSystem: true, typeCode: "Number");
        var command = MakeAttackCommand(table, field); // IsSearchable unchanged (true), IsReportable requested false

        await MakeSut().HandleAsync(command);

        await _fieldRepo.Received(1).UpdateAsync(
            field.PublicId, table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            /* isSearchable */ true, Arg.Any<bool>(), Arg.Any<bool>(), /* isReportable */ false, Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemField_NumericSettings_StripsUnsupportedProperties()
    {
        var table = MakeTable();
        var requestedSettings = JsonSerializer.Serialize(new
        {
            displayBold = true,
            noWrap = true,
            columnWidth = 120,
            decimals = 4,
            separator = ",",
            displayAs = "Currency",
            symbol = "€",
        });
        var field = MakeExistingField(table, isSystem: true, typeCode: "Number");
        var command = MakeAttackCommand(table, field, requestedSettings);

        await MakeSut().HandleAsync(command);

        await _fieldRepo.Received(1).UpdateAsync(
            field.PublicId, table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Is<string?>(s => AllowsOnlyDisplayTrio(s)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>True when the settings JSON contains only the Value-display trio (displayBold/
    /// noWrap/columnWidth) — none of Number's other properties (decimals/separator/displayAs/
    /// symbol/...).</summary>
    private static bool AllowsOnlyDisplayTrio(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return false;
        using var doc = JsonDocument.Parse(settingsJson);
        var allowed = new HashSet<string> { "displayBold", "noWrap", "columnWidth" };
        return doc.RootElement.EnumerateObject().All(p => allowed.Contains(p.Name))
            && doc.RootElement.TryGetProperty("displayBold", out _)
            && doc.RootElement.TryGetProperty("columnWidth", out _);
    }

    [Fact]
    public async Task CustomField_SameTypeCode_IsNotCoerced()
    {
        var table = MakeTable();
        var field = MakeExistingField(table, isSystem: false, typeCode: "Number");
        var command = MakeAttackCommand(table, field);

        await MakeSut().HandleAsync(command);

        // Everything the request asked for is honored as-is for a custom field.
        await _fieldRepo.Received(1).UpdateAsync(
            field.PublicId, table.Id, "Hacked Label", "hacked description",
            /* isRequired */ true, /* defaultValue */ "some default",
            Arg.Any<bool>(), /* isSortable */ true,
            /* isFilterable */ true, /* isReportable */ false, /* isAuditable */ true,
            /* isUnique */ true, /* isEncrypted */ false, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
