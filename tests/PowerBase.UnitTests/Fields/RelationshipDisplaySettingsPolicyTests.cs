using System.Data;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Fields;

/// <summary>
/// A Reference / Lookup / Summary field's Behavior Settings live in its settings' <c>display</c> object, next to the
/// relationship's wiring. Saving them from Field Detail must change that object and nothing else — a save that put
/// back the whole settings blob could break the lookup / summary.
/// </summary>
public class RelationshipDisplaySettingsPolicyTests
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly string LookupSettingsJson = RelationshipFieldFactory.Serialize(new LookupSettings
    {
        RelationshipId = 7, ReferenceFid = 10, SourceTableId = 20, SourceFid = 6,
        SourceTypeCode = "Currency", ResultTypeCode = "Currency", DisplayAsLinkToParent = true,
    });

    private static LookupSettings Lookup(string? json) => JsonSerializer.Deserialize<LookupSettings>(json!, ReadOpts)!;

    // ── Merge ──

    [Fact]
    public void Merge_NoRequestSettings_KeepsStored() =>
        RelationshipDisplaySettingsPolicy.Merge("Lookup", LookupSettingsJson, null).Should().Be(LookupSettingsJson);

    [Fact]
    public void Merge_RequestWithoutDisplay_KeepsStored_EvenWhenItCarriesOtherKeys() =>
        RelationshipDisplaySettingsPolicy.Merge("Lookup", LookupSettingsJson, "{\"relationshipId\":999,\"sourceFid\":1}")
            .Should().Be(LookupSettingsJson);

    [Fact]
    public void Merge_SetsDisplay_AndKeepsTheRelationshipWiring()
    {
        var merged = RelationshipDisplaySettingsPolicy.Merge("Lookup", LookupSettingsJson,
            "{\"relationshipId\":999,\"display\":{\"decimals\":0,\"displayBold\":true,\"symbol\":\"₹\"}}");

        var s = Lookup(merged);
        s.RelationshipId.Should().Be(7);
        s.SourceFid.Should().Be(6);
        s.ResultTypeCode.Should().Be("Currency");
        s.DisplayAsLinkToParent.Should().BeTrue();
        s.Display!.Decimals.Should().Be(0);
        s.Display.DisplayBold.Should().BeTrue();
        s.Display.Symbol.Should().Be("₹");
    }

    [Fact]
    public void Merge_NullOrEmptyDisplay_RemovesIt()
    {
        var withDisplay = RelationshipDisplaySettingsPolicy.Merge("Lookup", LookupSettingsJson, "{\"display\":{\"noWrap\":true}}");

        Lookup(RelationshipDisplaySettingsPolicy.Merge("Lookup", withDisplay, "{\"display\":null}")).Display.Should().BeNull();
        Lookup(RelationshipDisplaySettingsPolicy.Merge("Lookup", withDisplay, "{\"display\":{}}")).Display.Should().BeNull();
    }

    [Fact]
    public void Merge_UnchangedDisplay_ReturnsTheStoredTextAsIs()
    {
        var withDisplay = RelationshipDisplaySettingsPolicy.Merge("Lookup", LookupSettingsJson, "{\"display\":{\"decimals\":2}}");

        RelationshipDisplaySettingsPolicy.Merge("Lookup", withDisplay, "{\"display\":{\"decimals\":2}}").Should().BeSameAs(withDisplay);
        // A typed round-trip writes "display":null for a field that has none — sending no display back changes nothing.
        RelationshipDisplaySettingsPolicy.Merge("Lookup", LookupSettingsJson, "{\"display\":null}").Should().BeSameAs(LookupSettingsJson);
    }

    [Fact]
    public void Merge_Reference_KeepsItsKeyFormats_ButNotTotals()
    {
        // Like a Quickbase reference of a number key: decimals / Display as are kept. Totals aren't — the column holds
        // the parent's Record ID#, so there's nothing of the key to add up.
        var stored = RelationshipFieldFactory.Serialize(new ReferenceSettings { RelationshipId = 7, ParentTableId = 20, OriginalTypeCode = "Text" });

        var merged = RelationshipDisplaySettingsPolicy.Merge("Reference", stored,
            "{\"display\":{\"displayBold\":true,\"columnWidth\":150,\"separator\":\",\",\"displayAs\":\"Currency\","
            + "\"showTotalInReports\":true,\"showAverageInReports\":true,\"treatBlankAsZero\":false,\"showTotalsRow\":true}}");

        var s = JsonSerializer.Deserialize<ReferenceSettings>(merged!, ReadOpts)!;
        s.RelationshipId.Should().Be(7);
        s.ParentTableId.Should().Be(20);
        s.OriginalTypeCode.Should().Be("Text");
        s.Display.Should().BeEquivalentTo(new RelationshipDisplaySettings
        {
            DisplayBold = true, ColumnWidth = 150, Separator = ",", DisplayAs = "Currency",
        });
    }

    [Theory]
    [InlineData("{\"display\":{\"decimals\":11}}", "Settings.Display.Decimals")]
    [InlineData("{\"display\":{\"displayAs\":\"Money\"}}", "Settings.Display.DisplayAs")]
    [InlineData("{\"display\":{\"position\":\"middle\"}}", "Settings.Display.Position")]
    [InlineData("{\"display\":{\"columnWidth\":5}}", "Settings.Display.ColumnWidth")]
    [InlineData("{\"display\":{\"display\":\"Fortnights\"}}", "Settings.Display.Display")]
    [InlineData("{\"display\":{\"sourceFid\":3}}", "Settings")]   // not a Behavior Setting
    public void Merge_InvalidDisplay_ThrowsValidation(string request, string errorKey) =>
        FluentActions.Invoking(() => RelationshipDisplaySettingsPolicy.Merge("Lookup", LookupSettingsJson, request))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainKey(errorKey);

    [Fact]
    public void AppliesTo_OnlyReferenceLookupAndSummary()
    {
        RelationshipDisplaySettingsPolicy.AppliesTo("Reference").Should().BeTrue();
        RelationshipDisplaySettingsPolicy.AppliesTo("Lookup").Should().BeTrue();
        RelationshipDisplaySettingsPolicy.AppliesTo("Summary").Should().BeTrue();
        RelationshipDisplaySettingsPolicy.AppliesTo("Number").Should().BeFalse();
        RelationshipDisplaySettingsPolicy.AppliesTo("Formula_Number").Should().BeFalse();
    }

    // ── Through UpdateField ──

    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private string? _savedSettings;

    private UpdateFieldCommandHandler MakeSut()
    {
        _uow.Transaction.Returns((IDbTransaction?)null);
        return new UpdateFieldCommandHandler(
            _tableRepo, _fieldRepo, Substitute.For<IRecordRepository>(), Substitute.For<IAuditRepository>(),
            Substitute.For<ISchemaEngineService>(),
            new FieldSettingsGuard(Substitute.For<IAppRolePermissionRepository>(), Substitute.For<IRecordRepository>(),
                new FieldSettingsValidatorRegistry(Array.Empty<IFieldSettingsValidator>())),
            new FieldVersionService(Substitute.For<IFieldVersionRepository>(), _queryContext),
            _uow, _fieldTypeRepo, Substitute.For<IMessagePublisher>(), _queryContext, Substitute.For<IAzureSearchService>(),
            Substitute.For<IRelationshipRepository>());
    }

    private (AppTable Table, AppField Field) Arrange(string typeCode, string settings)
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid(), Name = "Task" };
        var field = new AppField
        {
            Id = 42, PublicId = Guid.NewGuid(), AppTableId = table.Id, TypeCode = typeCode, Label = "Client Budget",
            IsSearchable = true, IsReportable = true, Settings = settings,
        };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.GetByPublicIdAsync(field.PublicId, Arg.Any<CancellationToken>()).Returns(field);
        _fieldRepo.LabelExistsInTableAsync(table.Id, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldRepo.UpdateAsync(
                field.PublicId, table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Do<string?>(s => _savedSettings = s),
                Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>())
            .Returns(1);
        return (table, field);
    }

    private static UpdateFieldCommand Command(AppTable table, AppField field, string? settings) => new(
        table.PublicId, field.PublicId, "Client Budget", null, false, null, true, true, true, true, true,
        false, false, false, settings, "Behavior settings");

    [Fact]
    public async Task UpdateField_Lookup_SavesDisplay_KeepsWiring_AndNeverSwitchesType()
    {
        var (table, field) = Arrange("Lookup", LookupSettingsJson);

        await MakeSut().HandleAsync(Command(table, field, "{\"display\":{\"displayAs\":\"Percent\",\"decimals\":1}}"));

        var s = Lookup(_savedSettings);
        s.SourceFid.Should().Be(6);
        s.RelationshipId.Should().Be(7);
        s.Display!.DisplayAs.Should().Be("Percent");
        s.Display.Decimals.Should().Be(1);
        await _fieldRepo.DidNotReceiveWithAnyArgs().UpdateFieldTypeAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task UpdateField_Summary_WithNoSettings_KeepsThemAsStored()
    {
        var stored = RelationshipFieldFactory.Serialize(new SummarySettings { RelationshipId = 7, ChildTableId = 20, Function = "Count" });
        var (table, field) = Arrange("Summary", stored);

        await MakeSut().HandleAsync(Command(table, field, null));

        _savedSettings.Should().Be(stored);
    }
}
