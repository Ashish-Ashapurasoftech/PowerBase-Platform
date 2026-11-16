using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Import;

/// <summary>A real QBL import (real SQL Server, real migrations) of a table mixing supported fields with ones PowerBase cannot create as
/// themselves: nothing is lost, the original definition is kept whole in the field's import notes, the notes are readable and resolvable
/// through the API, and none of it touches the field's own description or help text.</summary>
[Collection("PowerBase")]
public class ImportFieldNotesTests : IntegrationTestBase
{
    public ImportFieldNotesTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    // Longer than a field description may hold (500), referencing a field that does not exist in the file.
    private static readonly string LongBrokenFormula = "[Hours] * [Rate That Was Deleted]" + string.Concat(Enumerable.Repeat(" + [Hours]", 100));

    private static string MixedQbl() => $$"""
    Version: '0.12'
    Resources:
      $App_Mixed:
        Type: QB::Application
        Properties:
          Name: Mixed Fields App
        Tables:
          $Table_Tasks:
            Type: QB::Table
            Properties:
              Name: Tasks
            Fields:
              $Field_Title:
                Type: QB::Field::Text
                Id: 6
                Properties:
                  Label: Title
                  Comments: The task title
                  HelpText: Short and clear
              $Field_Hours:
                Type: QB::Field::Number
                Id: 7
                Properties:
                  Label: Hours
              $Field_Double:
                Type: QB::Field::Numeric::Formula
                Id: 8
                Properties:
                  Label: Double Hours
                  Formula: "[Hours] * 2"
              $Field_Legacy:
                Type: QB::Field::Unsupported
                Id: 30
                Properties:
                  Label: Legacy Link
                  Comments: Kept from Quickbase
                  Explanation: Refers to a deleted field.
              $Field_Choice_Calc:
                Type: QB::Field::TextMultipleChoice::Formula
                Id: 31
                Properties:
                  Label: Choice Calc
                  Formula: If([Hours] > 8, "Long", "Short")
              $Field_Broken_Total:
                Type: QB::Field::Currency::Formula
                Id: 32
                Properties:
                  Label: Broken Total
                  Formula: "{{LongBrokenFormula}}"
              $Field_Orphan_Reference:
                Type: QB::Field::Reference
                Id: 34
                Properties:
                  Label: Orphan Reference
              $Field_Dead_Link:
                Type: QB::Field::ReportLink
                Id: 36
                Properties:
                  Label: Dead Link
              $Field_Blank_Duration:
                Type: QB::Field::Duration::Formula
                Id: 37
                Properties:
                  Label: Blank Duration
                  Formula:
              $Field_Blank_Multi:
                Type: QB::Field::MultiselectText::Formula
                Id: 38
                Properties:
                  Label: Blank Multi
    """;

    private record ReportDto(Guid AppPublicId, string AppName, int FieldsCreated, List<SkippedDto> Skipped);
    private record SkippedDto(string LogicalRef, string Name, string Reason);
    private record BatchDto(List<ReportDto> Apps, List<FailureDto> Failed);
    private record FailureDto(string AppRef, string AppName, string Code, string Message);
    private record TableDto(Guid PublicId, string Name);
    private record FieldRow(Guid PublicId, string Name, string? Label, string TypeCode, string? Description);
    private record FieldDetail(Guid PublicId, string? Description, string? HelpText, string? Settings);
    private record NoteSummary(Guid FieldPublicId, int ActiveCount, int WarningCount, bool HasPlaceholder, string Severity);
    private record Note(Guid PublicId, string Severity, string Code, string Message, string? RecommendedAction, bool IsPlaceholder,
        string? OriginalDefinition, string? OriginalMetadata, string? SourceType, DateTime? ResolvedOn);

    private async Task<(string Token, Guid TableId, List<FieldRow> Fields, ReportDto Report)> ImportMixedAsync()
    {
        var (token, _) = await SignupAsync();
        var response = await PostFileAsync("/apps/import", MixedQbl(), token: token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var batch = await ReadData<BatchDto>(response);
        batch.Failed.Should().BeEmpty("one field the importer cannot create must never fail the app");
        var report = batch.Apps.Should().ContainSingle().Subject;

        var tables = await ReadListData<TableDto>(await GetAsync($"/apps/{report.AppPublicId}/tables", token));
        var tasks = tables.Should().ContainSingle(t => t.Name == "Tasks").Subject;
        var fields = await ReadListData<FieldRow>(await GetAsync($"/tables/{tasks.PublicId}/fields/all", token));
        return (token, tasks.PublicId, fields, report);
    }

    private record VersionRow(int Version, string ChangeType, string CommitMessage, string ChangedPropertiesSummary);

    [Fact]
    public async Task Import_MixedFields_EverySourceFieldExists_TypedAfterItsResult()
    {
        var (_, _, fields, report) = await ImportMixedAsync();

        string TypeOf(string label) => fields.Single(f => (f.Label ?? f.Name) == label).TypeCode;

        TypeOf("Title").Should().Be("Text");
        TypeOf("Hours").Should().Be("Number");
        TypeOf("Double Hours").Should().StartWith("Formula");           // a good formula stays a real formula
        TypeOf("Broken Total").Should().Be("Formula_Number");           // a formula that does not compile is STILL a formula field, as written
        TypeOf("Legacy Link").Should().Be("Text");                      // unsupported: plain Text
        TypeOf("Choice Calc").Should().Be("Text");                      // multiple-choice result
        TypeOf("Orphan Reference").Should().Be("Number");               // a reference holds a record number
        TypeOf("Dead Link").Should().Be("Text");
        TypeOf("Blank Duration").Should().Be("Duration");               // a typed stand-in with the real field-settings validators
        TypeOf("Blank Multi").Should().Be("Text");

        // Nothing silent: each stand-in is also in the report, and the broken formula says it was saved as a formula.
        foreach (var fieldRef in new[] { "$Field_Legacy", "$Field_Choice_Calc", "$Field_Orphan_Reference", "$Field_Dead_Link" })
            report.Skipped.Should().Contain(s => s.LogicalRef.EndsWith(fieldRef) && s.Reason.Contains("placeholder"), $"'{fieldRef}' must be reported");
        report.Skipped.Should().Contain(s => s.LogicalRef.EndsWith("$Field_Broken_Total") && s.Reason.Contains("could not be compiled") && !s.Reason.Contains("placeholder"));
    }

    [Fact]
    public async Task Import_BrokenFormula_IsSavedAsWritten_AndItsAuditHistoryExplainsWhy()
    {
        var (token, tableId, fields, _) = await ImportMixedAsync();
        var broken = fields.Single(f => (f.Label ?? f.Name) == "Broken Total");

        // The formula is stored exactly as the source wrote it (the field the formula names does not exist, so it stays as typed).
        var detail = await ReadData<FieldDetail>(await GetAsync($"/tables/{tableId}/fields/{broken.PublicId}", token));
        detail.Settings.Should().Contain("[Rate That Was Deleted]");
        detail.Settings!.Length.Should().BeGreaterThan(500, "the complete formula is kept");

        // The warning is in the field's audit history, in plain words - not in the field's own description or help.
        var versions = await ReadListData<VersionRow>(await GetAsync($"/tables/{tableId}/fields/{broken.PublicId}/versions", token));
        var entry = versions.Should().ContainSingle().Subject;
        entry.ChangeType.Should().Be("Import");
        entry.CommitMessage.Should().StartWith("Imported from Quickbase.").And.Contain("the field 'Rate That Was Deleted' does not exist on this table");
        entry.CommitMessage.Length.Should().BeLessThanOrEqualTo(500);
        entry.ChangedPropertiesSummary.Should().Contain("Original Quickbase definition");
        detail.Description.Should().BeNullOrEmpty();
        detail.HelpText.Should().BeNullOrEmpty();

        // The complete original formula is kept with the note.
        var notes = await ReadListData<Note>(await GetAsync($"/tables/{tableId}/fields/{broken.PublicId}/import-notes", token));
        var note = notes.Should().ContainSingle().Subject;
        note.IsPlaceholder.Should().BeFalse();
        note.Code.Should().Be("FORMULA_SAVED_BROKEN");
        note.OriginalDefinition.Should().Be(LongBrokenFormula, "never truncated");
    }

    [Fact]
    public async Task Import_StandInField_HasAnAuditHistoryEntry_ApartFromItsOwnDescriptionAndHelp()
    {
        var (token, tableId, fields, _) = await ImportMixedAsync();
        var legacy = fields.Single(f => (f.Label ?? f.Name) == "Legacy Link");

        var versions = await ReadListData<VersionRow>(await GetAsync($"/tables/{tableId}/fields/{legacy.PublicId}/versions", token));
        var entry = versions.Should().ContainSingle().Subject;
        entry.ChangeType.Should().Be("Import");
        entry.CommitMessage.Should().StartWith("Imported from Quickbase.").And.Contain("placeholder Text field");

        var notes = await ReadListData<Note>(await GetAsync($"/tables/{tableId}/fields/{legacy.PublicId}/import-notes", token));
        notes.Should().ContainSingle(n => n.IsPlaceholder).Which.SourceType.Should().Be("QB::Field::Unsupported");

        // The import warning is not an end-user hint.
        legacy.Description.Should().Be("Kept from Quickbase");
        var detail = await ReadData<FieldDetail>(await GetAsync($"/tables/{tableId}/fields/{legacy.PublicId}", token));
        detail.Description.Should().Be("Kept from Quickbase");
        detail.HelpText.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ImportNoteSummaries_MarkOnlyTheFieldsWithNotes_AndChangingTheFieldClearsTheIndicator()
    {
        var (token, tableId, fields, _) = await ImportMixedAsync();
        var title = fields.Single(f => (f.Label ?? f.Name) == "Title");
        var legacy = fields.Single(f => (f.Label ?? f.Name) == "Legacy Link");
        var brokenTotal = fields.Single(f => (f.Label ?? f.Name) == "Broken Total");

        var summaries = await ReadListData<NoteSummary>(await GetAsync($"/tables/{tableId}/fields/import-notes", token));

        summaries.Should().NotContain(s => s.FieldPublicId == title.PublicId, "an ordinary field carries no import note");
        var mark = summaries.Should().ContainSingle(s => s.FieldPublicId == legacy.PublicId).Subject;
        mark.HasPlaceholder.Should().BeTrue();
        mark.Severity.Should().Be("Warning");
        var brokenMark = summaries.Should().ContainSingle(s => s.FieldPublicId == brokenTotal.PublicId).Subject;
        brokenMark.HasPlaceholder.Should().BeFalse("it is a real formula field");
        brokenMark.Severity.Should().Be("Warning");
        summaries.Count(s => s.HasPlaceholder).Should().Be(6, "Legacy Link, Choice Calc, Orphan Reference, Dead Link, Blank Duration and Blank Multi");

        // Somebody changes the field: it is no longer flagged (its audit history has a newer entry than the import's).
        var update = await PatchAsync($"/tables/{tableId}/fields/{legacy.PublicId}", new
        {
            label = "Legacy Link", description = "Reviewed", isRequired = false, isSearchable = false, isSortable = true, isFilterable = true,
            isReportable = true, isAuditable = false, isUnique = false, isEncrypted = true, isAutoFill = false, commitMessage = "Reviewed after import",
        }, token);
        update.IsSuccessStatusCode.Should().BeTrue();

        var after = await ReadListData<NoteSummary>(await GetAsync($"/tables/{tableId}/fields/import-notes", token));
        after.Should().NotContain(s => s.FieldPublicId == legacy.PublicId);
        after.Should().Contain(s => s.FieldPublicId == brokenTotal.PublicId, "the other fields keep their indicator");

        // Marking notes resolved is still possible through the API; they stay as history.
        var resolve = await PostAsync($"/tables/{tableId}/fields/{brokenTotal.PublicId}/import-notes/resolve", new { }, token);
        resolve.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var history = await ReadListData<Note>(await GetAsync($"/tables/{tableId}/fields/{brokenTotal.PublicId}/import-notes", token));
        history.Should().OnlyContain(n => n.ResolvedOn != null);
    }

    [Fact]
    public async Task ImportNotes_OfAFieldFromAnotherTable_AreNotFound()
    {
        var (token, _, fields, _) = await ImportMixedAsync();
        var anotherTable = Guid.NewGuid();

        var response = await GetAsync($"/tables/{anotherTable}/fields/{fields[0].PublicId}/import-notes", token);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

}
