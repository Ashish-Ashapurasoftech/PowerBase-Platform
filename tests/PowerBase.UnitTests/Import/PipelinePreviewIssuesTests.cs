using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Queries.ImportPreview;
using PowerBase.Application.Import.Reporting;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>The import wizard's per-PowerFlow status and "what to do" for a pipelines-only file.</summary>
public class PipelinePreviewIssuesTests
{
    private static ImportIssue Issue(string code) => new()
    {
        Severity = PipelinePreviewIssues.IsInformation(code) ? ImportIssueSeverity.Information : ImportIssueSeverity.Warning,
        Code = code,
        Description = code,
        ElementType = "Pipeline",
    };

    [Fact]
    public void Status_WithAWarning_IsAPartialImportCountingTheItemsToChange()
    {
        var status = PipelinePreviewIssues.Status("App", "Flow", [Issue("PIPELINE_FIELD_NOT_FOUND"), Issue("PIPELINE_REFERENCE_CLEARED"), Issue("PIPELINE_TAGS_NOT_IMPORTED")], 5, 0)!;

        status.Severity.Should().Be(ImportIssueSeverity.Warning);
        status.Code.Should().Be("PIPELINE_PARTIAL_IMPORT");
        status.Description.Should().StartWith("Partial import").And.Contain("2 item(s)");
        status.TableName.Should().Be("PowerFlow: Flow");
        status.FieldName.Should().Be(PipelinePreviewIssues.StatusField);
    }

    [Fact]
    public void Status_WithOnlyInformation_IsImportedAsInQuickbase()
    {
        var status = PipelinePreviewIssues.Status("App", "Flow", [Issue("PIPELINE_TAGS_NOT_IMPORTED"), Issue("PIPELINE_HTTP_ACCOUNT_CREATED")], 3, 0)!;

        status.Severity.Should().Be(ImportIssueSeverity.Information);
        status.Code.Should().Be("PIPELINE_IMPORTED_AS_IN_QUICKBASE");
    }

    [Fact]
    public void Status_WithSkippedSteps_IsPartialAndSaysHowMany()
    {
        var status = PipelinePreviewIssues.Status("App", "Flow", [], 7, 2)!;

        status.Severity.Should().Be(ImportIssueSeverity.Warning);
        status.Description.Should().Contain("2 of 7 Quickbase step(s) were not imported");
    }

    [Fact]
    public void Status_WithoutNotes_IsNone() =>
        PipelinePreviewIssues.Status("App", "Flow", [], 3, 0).Should().BeNull();

    [Theory]
    [InlineData("PIPELINE_REFERENCE_CLEARED", "enter this value again")]
    [InlineData("PIPELINE_FIELD_NOT_FOUND", "Add the missing field")]
    [InlineData("PIPELINE_EMAIL_PLATFORM_SMTP", "Outlook")]
    [InlineData("PIPELINE_QUICKBASE_URL_REVIEW", "Replace the Quickbase address")]
    [InlineData("PIPELINE_REQUEST_NEEDS_REVIEW", "saved import")]
    [InlineData("PIPELINE_WEBHOOK_NEW_URL", "new URL")]
    [InlineData("PIPELINE_TAGS_NOT_IMPORTED", "No action needed.")]
    [InlineData("PIPELINE_SOMETHING_NEW", "Check this step")]
    public void Action_SaysWhatToDo(string code, string expected) =>
        PipelinePreviewIssues.Action(code).Should().Contain(expected);

    [Fact]
    public void NotImportedAction_ForAMissingQuickbaseId_SaysToImportThatAppFirst()
    {
        PipelinePreviewIssues.NotImportedAction("PowerFlow 'X' is not imported: Not found among the imported apps: Quickbase table id b1")
            .Should().StartWith("Import the Quickbase application");
        PipelinePreviewIssues.NotImportedAction("PowerFlow 'X' is not imported: step 'Fetch JSON' has no PowerFlows equivalent")
            .Should().Contain("build it in PowerFlows by hand");
    }

    [Fact]
    public void UsesSite_FindsAnApiLinkInANestedStep()
    {
        var pipeline = new PblPipeline
        {
            Steps =
            [
                new PblPipelineStep
                {
                    Config = new Dictionary<string, object?>(),
                    Children = [new PblPipelineStep { Config = new Dictionary<string, object?> { ["url"] = new PblPipelineTextValue([new PblPipelineSiteValue(true), "/v1/records"]) } }],
                },
            ],
        };

        PipelinePreviewIssues.UsesSite(pipeline, api: true).Should().BeTrue();
        PipelinePreviewIssues.UsesSite(pipeline, api: false).Should().BeFalse();
    }
}
