using FluentAssertions;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Queries.ImportPreview;
using PowerBase.Application.Import.Reporting;

namespace PowerBase.UnitTests.Import.Reporting;

public class ImportIssueReportBuilderTests
{
    private static PblDocument DocumentWithOneField() => new()
    {
        Tables =
        [
            new PblTable
            {
                LogicalRef = "$Table_Activity",
                Name = "Activity",
                Fields = [new PblField { LogicalRef = "$Field_Activity_Backup", Name = "Activity Backup", TypeCode = "Formula" }],
            },
        ],
    };

    [Fact]
    public void BuildFromPreview_WarningOnAKnownField_ResolvesTableAndFieldAndClassifiesSeverity()
    {
        var warnings = new List<PblIssueDto>
        {
            new() { Code = "QBL_EMPTY_FORMULA", Message = "Formula field 'Activity Backup' has no expression in the source; imported with a blank result (\"\").", ElementRef = "$Field_Activity_Backup" },
        };

        var report = ImportIssueReportBuilder.BuildFromPreview("My App", errors: [], warnings, DocumentWithOneField());

        report.Summary.WarningCount.Should().Be(1);
        report.Summary.ErrorCount.Should().Be(0);
        var issue = report.Issues.Should().ContainSingle().Subject;
        issue.ApplicationName.Should().Be("My App");
        issue.TableName.Should().Be("Activity");
        issue.FieldName.Should().Be("Activity Backup");
        issue.ElementType.Should().Be("Field");
        issue.Severity.Should().Be(ImportIssueSeverity.Warning);
        issue.Code.Should().Be("QBL_EMPTY_FORMULA");
        issue.RecommendedAction.Should().Contain("formula");
        issue.TechnicalDetail.Should().Contain("QBL_EMPTY_FORMULA").And.Contain("$Field_Activity_Backup");
    }

    [Fact]
    public void BuildFromPreview_Error_IsNeverDowngraded()
    {
        var errors = new List<PblIssueDto> { new() { Code = "QBL_MISSING_APPLICATION", Message = "The file has no application." } };

        var report = ImportIssueReportBuilder.BuildFromPreview("My App", errors, warnings: [], document: null);

        report.Summary.ErrorCount.Should().Be(1);
        report.Issues.Single().Severity.Should().Be(ImportIssueSeverity.Error);
    }

    [Fact]
    public void BuildFromPreview_UnresolvableRef_FallsBackToTheQuotedNameInTheMessageInsteadOfDroppingTheIssue()
    {
        var warnings = new List<PblIssueDto>
        {
            new() { Code = "QBL_BAD_REF", Message = "Report 'All Clients' has a broken column reference.", ElementRef = "$Report_Unknown" },
        };

        var report = ImportIssueReportBuilder.BuildFromPreview("My App", errors: [], warnings, document: null);

        var issue = report.Issues.Should().ContainSingle().Subject;
        issue.TableName.Should().BeNull();
        issue.FieldName.Should().Be("All Clients");
        issue.ElementType.Should().Be("General");
    }

    [Fact]
    public void BuildFromImportReport_SkippedItemWithNoCode_IsClassifiedByKeywordAndKeepsItsName()
    {
        var report = new ImportReport
        {
            Skipped = [new ImportSkippedItem { LogicalRef = "$Field_Legacy", Name = "Legacy Score", Reason = "Unsupported field type 'SomeUnsupportedType' for this import phase." }],
        };

        var issueReport = ImportIssueReportBuilder.BuildFromImportReport("My App", report, document: null);

        var issue = issueReport.Issues.Should().ContainSingle().Subject;
        issue.FieldName.Should().Be("Legacy Score");
        issue.Severity.Should().Be(ImportIssueSeverity.Warning);
        issue.RecommendedAction.Should().Contain("Quickbase feature");
        issue.Code.Should().BeEmpty(); // no machine code on a free-text ImportSkippedItem.Reason
    }

    [Fact]
    public void BuildFromImportReport_CleanFormulaTranslation_IsNotReportedAsAnIssue()
    {
        var report = new ImportReport
        {
            FormulaTranslations = [new FormulaTranslationReportItem { LogicalRef = "$F1", Name = "Total", Status = "Clean" }],
        };

        var issueReport = ImportIssueReportBuilder.BuildFromImportReport("My App", report, document: null);

        issueReport.Issues.Should().BeEmpty();
    }

    [Fact]
    public void BuildFromImportReport_NeedsManualReviewFormula_IsReportedAsAWarningWithItsDiagnostics()
    {
        var report = new ImportReport
        {
            FormulaTranslations =
            [
                new FormulaTranslationReportItem { LogicalRef = "$F1", Name = "Total", Status = "NeedsManualReview", Diagnostics = ["Unknown function 'FOO'."] },
            ],
        };

        var issueReport = ImportIssueReportBuilder.BuildFromImportReport("My App", report, document: null);

        var issue = issueReport.Issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(ImportIssueSeverity.Warning);
        issue.Description.Should().Contain("Unknown function 'FOO'.");
    }

    [Fact]
    public void BuildFromImportReport_PipelineStepIssue_IsReportedUnderThePipelineWithItsStepNamePrefixed()
    {
        var report = new ImportReport
        {
            Pipelines =
            [
                new PipelineImportReportItem
                {
                    LogicalRef = "$Pipeline_1",
                    Name = "Notify",
                    Issues = [new PipelineImportIssueItem { Code = "QBL_UNSUPPORTED_PIPELINE_STEP", Message = "Step type not supported.", StepId = "aa", StepName = "Send Email" }],
                },
            ],
        };

        var issueReport = ImportIssueReportBuilder.BuildFromImportReport("My App", report, document: null);

        var issue = issueReport.Issues.Should().ContainSingle().Subject;
        issue.ElementType.Should().Be("Pipeline");
        issue.FieldName.Should().Be("Notify");
        issue.Description.Should().Contain("Send Email").And.Contain("Step type not supported.");
    }

    [Fact]
    public void Summary_CountsDistinctAffectedFieldsOnce()
    {
        var report = new ImportReport
        {
            Skipped =
            [
                new ImportSkippedItem { LogicalRef = "$Field_A", Name = "A", Reason = "Unsupported field type 'X' for this import phase." },
                new ImportSkippedItem { LogicalRef = "$Field_A", Name = "A", Reason = "Imported as 'A (2)': the label 'A' is already used by another field on 'T'." },
                new ImportSkippedItem { LogicalRef = "$Field_B", Name = "B", Reason = "Unsupported field type 'Y' for this import phase." },
            ],
        };

        var issueReport = ImportIssueReportBuilder.BuildFromImportReport("My App", report, document: null);

        issueReport.Summary.WarningCount.Should().Be(3);
        issueReport.Summary.AffectedFieldCount.Should().Be(2); // "A" (twice) and "B" (once) — two distinct fields
    }

    [Fact]
    public void BuildFromPreview_CosmeticCode_IsDowngradedToInformationAndExcludedFromAffectedFieldCount()
    {
        var warnings = new List<PblIssueDto>
        {
            new() { Code = "QBL_APP_SEARCHABLE_NOT_MAPPED", Message = "'Searchable' is not the same setting as PowerBase's 'hide from public search'; left at the default.", ElementRef = "$App_Test" },
        };

        var report = ImportIssueReportBuilder.BuildFromPreview("My App", errors: [], warnings, document: null);

        var issue = report.Issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(ImportIssueSeverity.Information);
        report.Summary.InformationCount.Should().Be(1);
        report.Summary.WarningCount.Should().Be(0);
        report.Summary.AffectedFieldCount.Should().Be(0);
    }
}
