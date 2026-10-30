using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms;
using PowerBase.Application.Forms.Commands.SaveFormLayout;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using Xunit;

namespace PowerBase.UnitTests.Forms;

/// <summary>Saving a form layout keeps a Reference (or proxy) element's "Select using record picker" choice, and only
/// when its target is eligible (see RecordPickerRules).</summary>
public class SaveFormLayoutRecordPickerTests
{
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();

    private static readonly Guid FormPublicId = Guid.NewGuid();

    // Form on table 20 (Employees): Name (Fid 6), Related Dept reference (Fid 10), its proxy lookup (Fid 11),
    // another lookup of the same relationship (Fid 12), a lookup of another relationship (Fid 13).
    private const long ParentTableId = 10;

    private SaveFormLayoutCommandHandler CreateSut()
    {
        _formRepo.GetByPublicIdAsync(FormPublicId, Arg.Any<CancellationToken>())
            .Returns(new Form { Id = 1, PublicId = FormPublicId, AppTableId = 20, Name = "Main Form" });
        _fieldRepo.ListByTableAsync(20, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 100, Fid = 6, Name = "Name", TypeCode = "Text" },
            new() { Id = 205, Fid = 10, Name = "Related Dept", TypeCode = "Reference" },
            new() { Id = 206, Fid = 11, Name = "Dept - Budget", TypeCode = "Lookup", Settings = LookupOf(1) },
            new() { Id = 207, Fid = 12, Name = "Dept - Code", TypeCode = "Lookup", Settings = LookupOf(1) },
            new() { Id = 208, Fid = 13, Name = "Other - Code", TypeCode = "Lookup", Settings = LookupOf(99) },
        });
        _relRepo.ListByChildTableAsync(20, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 1, ParentTableId = ParentTableId, ChildTableId = 20, ReferenceFieldId = 205, ReferenceFid = 10, ProxyFieldId = 206 },
        });
        return new SaveFormLayoutCommandHandler(_formRepo, _fieldRepo, _queryContext, _auditRepo, relRepo: _relRepo, reportRepo: _reportRepo);
    }

    private static string LookupOf(long relationshipId) => JsonSerializer.Serialize(new LookupSettings { RelationshipId = relationshipId });

    private static SaveFormLayoutCommand CommandFor(long fid, string? mode, Guid? reportId = null, long? fieldId = null, string elementType = "Field") =>
        new(FormPublicId,
        [
            new FormSectionLayout(null, "Section 1", false,
            [
                new FormBlockLayout(null, null, null, null,
                [
                    new FormElementLayout(null, fid, elementType, null, "Default", null,
                        true, true, true, "Auto", null, null, false, false, null,
                        RecordPickerMode: mode, RecordPickerReportPublicId: reportId, RecordPickerFieldId: fieldId),
                ]),
            ]),
        ]);

    private async Task<FormElement> SaveAsync(SaveFormLayoutCommand command)
    {
        IReadOnlyList<FormSection>? captured = null;
        await _formRepo.SaveLayoutAsync(
            Arg.Any<long>(), Arg.Do<IReadOnlyList<FormSection>>(s => captured = s), Arg.Any<IReadOnlyList<FormPage>?>(),
            Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<FormSection, Guid>?>(), Arg.Any<IReadOnlyDictionary<FormElement, Guid>?>(), Arg.Any<CancellationToken>());

        await CreateSut().HandleAsync(command);

        return captured!.Single().Blocks.Single().Elements.Single();
    }

    private Report ReportOf(long tableId = ParentTableId, string type = "Table", string visibility = "Shared", long? relationshipId = null, bool defaultSettings = false)
    {
        var report = new Report
        {
            Id = 500, PublicId = Guid.NewGuid(), AppTableId = tableId, ReportType = type, Visibility = visibility,
            RelationshipId = relationshipId, IsDefaultSettingsRecord = defaultSettings,
        };
        _reportRepo.GetByPublicIdAsync(report.PublicId, Arg.Any<CancellationToken>()).Returns(report);
        return report;
    }

    [Fact]
    public async Task ReferenceElement_KeepsAReportChoice()
    {
        var report = ReportOf();

        var saved = await SaveAsync(CommandFor(10, "report", reportId: report.PublicId));

        saved.RecordPickerMode.Should().Be(RecordPickerModes.Report);
        saved.RecordPickerReportPublicId.Should().Be(report.PublicId);
        saved.RecordPickerFieldId.Should().BeNull();
    }

    [Fact]
    public async Task ReferenceElement_KeepsAFieldChoice()
    {
        var saved = await SaveAsync(CommandFor(10, "field", fieldId: 12));

        saved.RecordPickerMode.Should().Be(RecordPickerModes.Field);
        saved.RecordPickerFieldId.Should().Be(12);
        saved.RecordPickerReportPublicId.Should().BeNull();
    }

    [Fact]
    public async Task ProxyElement_KeepsAFieldChoice()
    {
        var saved = await SaveAsync(CommandFor(11, "field", fieldId: 12));

        saved.RecordPickerMode.Should().Be(RecordPickerModes.Field);
        saved.RecordPickerFieldId.Should().Be(12);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public async Task DefaultChoice_IsStoredAsNothing(string? mode)
    {
        var saved = await SaveAsync(CommandFor(10, mode, fieldId: 12));

        saved.RecordPickerMode.Should().BeNull();
        saved.RecordPickerFieldId.Should().BeNull();
        saved.RecordPickerReportPublicId.Should().BeNull();
    }

    [Fact]
    public async Task ElementThatIsNotAReferenceOrItsProxy_DropsTheChoice()
    {
        var saved = await SaveAsync(CommandFor(6, "field", fieldId: 12));

        saved.RecordPickerMode.Should().BeNull();
        saved.RecordPickerFieldId.Should().BeNull();
    }

    [Fact]
    public async Task NonFieldElement_DropsTheChoice()
    {
        var saved = await SaveAsync(CommandFor(0, "field", fieldId: 12, elementType: "Divider"));

        saved.RecordPickerMode.Should().BeNull();
    }

    [Fact]
    public async Task DeletedReport_DropsTheChoiceInsteadOfBlockingTheSave()
    {
        var gone = Guid.NewGuid();
        _reportRepo.GetByPublicIdAsync(gone, Arg.Any<CancellationToken>()).Returns<Report>(_ => throw new NotFoundException("Report", gone));

        var saved = await SaveAsync(CommandFor(10, "report", reportId: gone));

        saved.RecordPickerMode.Should().BeNull();
        saved.RecordPickerReportPublicId.Should().BeNull();
    }

    [Fact]
    public async Task MissingLookup_DropsTheChoiceInsteadOfBlockingTheSave()
    {
        var saved = await SaveAsync(CommandFor(10, "field", fieldId: 999));

        saved.RecordPickerMode.Should().BeNull();
    }

    [Fact]
    public async Task UnknownMode_IsRejected()
    {
        var act = () => SaveAsync(CommandFor(10, "everything"));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ReportMode_WithoutAReport_IsRejected()
    {
        var act = () => SaveAsync(CommandFor(10, "report"));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task FieldMode_WithoutAField_IsRejected()
    {
        var act = () => SaveAsync(CommandFor(10, "field"));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Theory]
    [InlineData(99L, "Table", "Shared", null, false)]   // a report of another table
    [InlineData(ParentTableId, "Summary", "Shared", null, false)]
    [InlineData(ParentTableId, "Table", "Personal", null, false)]
    [InlineData(ParentTableId, "Table", "Shared", 7L, false)]  // a hidden embedded copy
    [InlineData(ParentTableId, "Table", "Shared", null, true)] // the Default Report Settings record
    public async Task ReportMode_WithAnIneligibleReport_IsRejected(long tableId, string type, string visibility, long? relationshipId, bool defaultSettings)
    {
        var report = ReportOf(tableId, type, visibility, relationshipId, defaultSettings);

        var act = () => SaveAsync(CommandFor(10, "report", reportId: report.PublicId));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task FieldMode_WithALookupOfAnotherRelationship_IsRejected()
    {
        var act = () => SaveAsync(CommandFor(10, "field", fieldId: 13));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task FieldMode_WithAFieldThatIsNotALookup_IsRejected()
    {
        var act = () => SaveAsync(CommandFor(10, "field", fieldId: 6));

        await act.Should().ThrowAsync<ValidationException>();
    }
}
