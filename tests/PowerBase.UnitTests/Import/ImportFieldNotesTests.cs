using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Reporting;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Import;

public class ImportFieldNotesTests
{
    private static PblDocument Doc() => new()
    {
        App = new PblApp { LogicalRef = "$App", Name = "App" },
        Tables =
        [
            new PblTable
            {
                LogicalRef = "$Table_T", Name = "Tasks",
                Fields =
                [
                    new PblField { LogicalRef = "$Table_T::$Field_A", Name = "Title", TypeCode = "Text", SourceId = 6 },
                    new PblField
                    {
                        LogicalRef = "$Table_T::$Field_B", Name = "Legacy", Label = "Legacy Link", TypeCode = "Text", SourceId = 30, SourceType = "QB::Field::Unsupported",
                        Placeholder = new PblPlaceholderInfo { Code = "QBL_UNSUPPORTED_FIELD_TYPE", Reason = "Refers to a deleted field", OriginalMetadataJson = "{\"type\":\"QB::Field::Unsupported\"}", FullDescription = "long" },
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Build_StandInField_GetsAPlaceholderNoteWithItsOriginalMetadataAndFullDescription()
    {
        var drafts = ImportFieldNoteBuilder.Build(Doc(), [], []);

        var note = drafts.Should().ContainSingle().Subject;
        note.IsPlaceholder.Should().BeTrue();
        note.TableName.Should().Be("Tasks");
        note.FieldName.Should().Be("Legacy");
        note.Severity.Should().Be(ImportNoteSeverities.Warning);
        note.Code.Should().Be("QBL_UNSUPPORTED_FIELD_TYPE");
        note.OriginalMetadata.Should().Contain("QB::Field::Unsupported").And.Contain("\"fullDescription\":\"long\"");
        note.SourceFid.Should().Be(30);
    }

    [Fact]
    public void Build_OtherFieldLevelIssues_BecomeNotes_ButNotForAStandInThatAlreadyExplainsItself()
    {
        var skipped = new List<ImportSkippedItem>
        {
            new() { LogicalRef = "$Table_T::$Field_A", Name = "Title", Reason = "Field settings not applied: bad width", Code = "QBL_FIELD_SETTING_NOT_CARRIED" },
            new() { LogicalRef = "$Table_T::$Field_B", Name = "Legacy", Reason = "Refers to a deleted field; a placeholder Text field was created in its place." },
            new() { LogicalRef = "$Table_T", Name = "Tasks", Reason = "A table-level thing" }, // not a field: stays in the report only
        };

        var drafts = ImportFieldNoteBuilder.Build(Doc(), skipped, []);

        drafts.Should().HaveCount(2);
        var cosmetic = drafts.Single(d => !d.IsPlaceholder);
        cosmetic.FieldName.Should().Be("Title");
        cosmetic.Severity.Should().Be(ImportNoteSeverities.Information, "a setting that could not be carried over is cosmetic");
        drafts.Count(d => d.FieldName == "Legacy").Should().Be(1);
    }

    [Fact]
    public void Build_KeepsTheNotesTheImporterCollectedWhileItRan()
    {
        var late = new ImportFieldNoteDraft { TableName = "Tasks", FieldName = "Total", IsPlaceholder = true, OriginalDefinition = "[A] + [B]" };

        var drafts = ImportFieldNoteBuilder.Build(Doc(), [], [late]);

        drafts.Should().Contain(late);
    }

    [Fact]
    public void WithFullDescription_AddsTheTextWithoutLosingTheSnapshot_AndToleratesBadJson()
    {
        ImportFieldNoteBuilder.WithFullDescription("{\"a\":1}", "text").Should().Contain("\"a\":1").And.Contain("\"fullDescription\":\"text\"");
        ImportFieldNoteBuilder.WithFullDescription("{\"a\":1}", null).Should().Be("{\"a\":1}");
        ImportFieldNoteBuilder.WithFullDescription("not json", "text").Should().Be("not json");
    }

    private static (ImportFieldNoteRecorder Recorder, List<AppFieldImportNote> Stored) Recorder(params AppField[] fields)
    {
        var appRepo = Substitute.For<IAppRepository>();
        appRepo.GetIdByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(10L);
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(10L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppTable>)[new AppTable { Id = 5, Name = "Tasks" }]);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(5L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)fields);
        var noteRepo = Substitute.For<IAppFieldImportNoteRepository>();
        var stored = new List<AppFieldImportNote>();
        noteRepo.AddManyAsync(Arg.Do<IReadOnlyList<AppFieldImportNote>>(n => stored.AddRange(n)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return (new ImportFieldNoteRecorder(appRepo, tableRepo, fieldRepo, noteRepo), stored);
    }

    [Fact]
    public async Task Recorder_FindsTheFieldByNameThenLabelThenSourceNumber_AndHandsBackWhatItCannotPlace()
    {
        var (recorder, stored) = Recorder(
            new AppField { Id = 1, Name = "Alpha", Label = "Alpha", Fid = 6 },
            new AppField { Id = 2, Name = "regenerated_name", Label = "Beta", Fid = 7 },
            new AppField { Id = 3, Name = "other", Label = "Other label", Fid = 8 });

        var result = await recorder.RecordAsync(Guid.NewGuid(),
        [
            new ImportFieldNoteDraft { TableName = "tasks", FieldName = "Alpha", Message = "by name" },
            new ImportFieldNoteDraft { TableName = "Tasks", FieldName = "Beta source name", FieldLabel = "Beta", Message = "by label" },
            new ImportFieldNoteDraft { TableName = "Tasks", FieldName = "Unknown", FieldLabel = "Nothing", SourceFid = 8, Message = "by fid" },
            new ImportFieldNoteDraft { TableName = "Tasks", FieldName = "Gone", Message = "nowhere", IsPlaceholder = true },
            new ImportFieldNoteDraft { TableName = "No such table", FieldName = "X", Message = "no table" },
        ], default);

        result.Stored.Should().Be(3);
        stored.Select(n => (n.Message, n.AppFieldId)).Should().BeEquivalentTo([("by name", 1L), ("by label", 2L), ("by fid", 3L)]);
        result.Unmatched.Select(d => d.Message).Should().BeEquivalentTo("nowhere", "no table");
        stored.Should().OnlyContain(n => n.AppId == 10 && n.AppTableId == 5);
    }

    [Fact]
    public async Task Recorder_KeepsTheCompleteDefinitionAndMetadata_AndCutsOnlyTheShortColumns()
    {
        var (recorder, stored) = Recorder(new AppField { Id = 1, Name = "Alpha", Label = "Alpha", Fid = 6 });
        var huge = new string('f', 50_000);

        await recorder.RecordAsync(Guid.NewGuid(),
        [
            new ImportFieldNoteDraft
            {
                TableName = "Tasks", FieldName = "Alpha", Message = new string('m', 3000), OriginalDefinition = huge, OriginalMetadata = huge,
                RecommendedAction = new string('r', 5000), Severity = "nonsense",
            },
        ], default);

        var note = stored.Single();
        note.OriginalDefinition.Should().HaveLength(50_000);
        note.OriginalMetadata.Should().HaveLength(50_000);
        note.Message.Should().HaveLength(3000);
        note.RecommendedAction.Should().HaveLength(1000);
        note.Severity.Should().Be(ImportNoteSeverities.Warning, "an unknown severity never reaches the CHECK constraint");
    }

    [Fact]
    public void FriendlyDiagnostics_SaysUnknownFieldsInPlainWords_AndKeepsOtherMessages()
    {
        var text = ImportFieldNoteBuilder.FriendlyDiagnostics(
            ["Unknown field [Master Vendor Name].", "Unknown field [Vendor Name].", "Operator '>=' cannot compare Text and Date.", "Unknown field [Vendor Name]."]);

        text.Should().Be("the field 'Master Vendor Name' does not exist on this table; the field 'Vendor Name' does not exist on this table; Operator '>=' cannot compare Text and Date");
        ImportFieldNoteBuilder.FriendlyDiagnostics([]).Should().Be("the formula has an error");
    }

    [Fact]
    public void CommitMessage_LeadsWithTheWorstNote_CountsTheRest_AndFitsTheColumn()
    {
        var drafts = new List<ImportFieldNoteDraft>
        {
            new() { Severity = ImportNoteSeverities.Information, Message = "A setting was not carried over" },
            new() { Severity = ImportNoteSeverities.Warning, Code = ImportFieldNoteBuilder.BROKEN_FORMULA_CODE, Message = "The formula could not be compiled: the field 'X' does not exist on this table." },
        };

        var message = ImportFieldNoteBuilder.CommitMessage(drafts);

        message.Should().StartWith("Imported from Quickbase. The formula could not be compiled: the field 'X' does not exist on this table.");
        message.Should().Contain("(+1 more");

        var huge = new List<ImportFieldNoteDraft> { new() { Message = new string('m', 2000) } };
        ImportFieldNoteBuilder.CommitMessage(huge).Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public void VersionChanges_KeepTheCompleteOriginalDefinition()
    {
        var huge = new string('f', 20_000);

        var rows = ImportFieldNoteBuilder.VersionChanges(
            [new ImportFieldNoteDraft { Message = "m", RecommendedAction = "fix it", SourceType = "QB::Field::Text::Formula", OriginalDefinition = huge, OriginalMetadata = "{}" }]);

        rows.Select(r => r.PropertyName).Should().Equal("Import warning", "What to do", "Quickbase field type", "Original Quickbase definition", "Original Quickbase field details");
        rows.Single(r => r.PropertyName == "Original Quickbase definition").NewValue.Should().HaveLength(20_000);
        rows.Should().OnlyContain(r => r.PropertyName.Length <= 100);
    }

    [Fact]
    public async Task Recorder_WritesOneImportEntryPerFieldToTheAuditHistory_EvenWhenOneEntryFails()
    {
        var versionRepo = Substitute.For<IFieldVersionRepository>();
        var written = new List<(AppFieldVersion Version, IReadOnlyList<PowerBase.Application.Fields.Versioning.FieldChangeEntry> Changes)>();
        versionRepo.InsertStandaloneVersionAsync(Arg.Any<AppFieldVersion>(), Arg.Any<IReadOnlyList<PowerBase.Application.Fields.Versioning.FieldChangeEntry>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var v = ci.Arg<AppFieldVersion>();
                if (v.AppFieldId == 1) return Task.FromException<int>(new InvalidOperationException("boom"));
                written.Add((v, ci.Arg<IReadOnlyList<PowerBase.Application.Fields.Versioning.FieldChangeEntry>>()));
                return Task.FromResult(1);
            });
        var ctx = Substitute.For<IQueryContext>();
        ctx.UserId.Returns(7L);
        ctx.UserName.Returns("Importer");

        var appRepo = Substitute.For<IAppRepository>();
        appRepo.GetIdByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(10L);
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(10L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppTable>)[new AppTable { Id = 5, Name = "Tasks" }]);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(5L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)
            [new AppField { Id = 1, Name = "A", Label = "A" }, new AppField { Id = 2, Name = "B", Label = "B" }]);
        var noteRepo = Substitute.For<IAppFieldImportNoteRepository>();
        var recorder = new ImportFieldNoteRecorder(appRepo, tableRepo, fieldRepo, noteRepo, new PowerBase.Application.Fields.Versioning.FieldVersionService(versionRepo, ctx));

        var result = await recorder.RecordAsync(Guid.NewGuid(),
        [
            new ImportFieldNoteDraft { TableName = "Tasks", FieldName = "A", Message = "first" },
            new ImportFieldNoteDraft { TableName = "Tasks", FieldName = "B", Message = "second" },
            new ImportFieldNoteDraft { TableName = "Tasks", FieldName = "B", Message = "third" },
        ], default);

        result.Stored.Should().Be(3, "notes are stored even when an audit-history entry fails");
        result.VersionFailures.Should().Be(1);
        result.VersionsWritten.Should().Be(1);
        var entry = written.Should().ContainSingle().Subject;
        entry.Version.AppFieldId.Should().Be(2);
        entry.Version.ChangeType.Should().Be(FieldVersionChangeType.Import);
        entry.Version.ChangedByName.Should().Be("Importer");
        entry.Version.CommitMessage.Should().StartWith("Imported from Quickbase. second").And.Contain("(+1 more");
        entry.Version.SnapshotJson.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("QBL_FORM_ELEMENT_FIELD_UNAVAILABLE")]
    [InlineData("QBL_REPORT_FIELD_UNAVAILABLE")]
    [InlineData("QBL_REFERENCE_PROXY_NOT_SEPARATE")]
    [InlineData("FORMULA_ADJUSTED")]
    public void Classifier_DanglingReferencesAndRepresentedFields_AreInformationNotWarnings(string code) =>
        ImportIssueClassifier.Classify(code, "x", ImportIssueSeverity.Warning).Severity.Should().Be(ImportIssueSeverity.Information);

    [Theory]
    [InlineData("QBL_UNSUPPORTED_FIELD_TYPE")]
    [InlineData("QBL_SUMMARY_FIELD_UNRESOLVED")]
    [InlineData("QBL_REPORT_LINK_TARGET_UNAVAILABLE")]
    public void Classifier_StandInFields_StayWarnings(string code) =>
        ImportIssueClassifier.Classify(code, "x", ImportIssueSeverity.Warning).Severity.Should().Be(ImportIssueSeverity.Warning);

    [Theory]
    [InlineData("QB::Field::Currency::Formula", "Currency")]
    [InlineData("QB::Field::Numeric::Formula", "Number")]
    [InlineData("QB::Field::Checkbox::Formula", "Boolean")]
    [InlineData("QB::Field::Date::Formula", "Date")]
    [InlineData("QB::Field::User::Formula", "User")]
    [InlineData("QB::Field::MultiselectText::Formula", "Text")]
    [InlineData("QB::Field::TextMultipleChoice::Formula", "Text")]
    [InlineData("QB::Field::Anything", "Text")]
    public void PlaceholderType_ForAFormulaType_IsTheResultTypeOnlyWhenCertain(string qblType, string expected) =>
        QblPlaceholders.TypeForFormula(qblType).Should().Be(expected);

    [Theory]
    [InlineData("Number", null, null, "Number")]
    [InlineData("Bool", null, null, "Boolean")]
    [InlineData("Text", "Url", null, "Url")]
    [InlineData("Text", null, null, "Text")]
    [InlineData("TextList", null, null, "Text")]
    [InlineData("Number", null, "QB::Field::Currency::Formula", "Currency")]
    public void PlaceholderType_ForAPblFormula_FollowsResultTypeVariantAndSourceType(string resultType, string? variant, string? sourceType, string expected) =>
        QblPlaceholders.TypeForFormula(resultType, variant, sourceType).Should().Be(expected);

    [Theory]
    [InlineData("Count", "Number")]
    [InlineData("StdDeviation", "Number")]
    [InlineData("AnyRelatedRecords", "Boolean")]
    [InlineData("Maximum", "Text")]
    [InlineData(null, "Text")]
    public void PlaceholderType_ForASummaryFunction(string? function, string expected) =>
        QblPlaceholders.TypeForSummaryFunction(function).Should().Be(expected);

    [Theory]
    [InlineData("Formula_Number", "Number")]
    [InlineData("Currency", "Currency")]
    [InlineData("Formula_Bool", "Boolean")]
    [InlineData("Lookup", "Text")]
    [InlineData(null, "Text")]
    public void PlaceholderType_ForAnExistingField(string? typeCode, string expected) =>
        QblPlaceholders.TypeForExistingField(typeCode).Should().Be(expected);
}
