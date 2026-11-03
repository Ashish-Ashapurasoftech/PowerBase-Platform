using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>
/// Runs the pipeline conversion over a real Quickbase export with a pipeline when one is available on this machine: set
/// <c>POWERBASE_PIPELINE_QBL</c> to its path (or leave <c>QBL_PBL.qbl</c> in Downloads). Without a file the tests do nothing,
/// so CI is unaffected.
/// </summary>
public class QblPipelineRealFileTests
{
    private static string? FindFile()
    {
        var fromEnv = Environment.GetEnvironmentVariable("POWERBASE_PIPELINE_QBL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "QBL_PBL.qbl");
        return File.Exists(downloads) ? downloads : null;
    }

    [Fact]
    public void RealExport_PipelineConvertsEveryStepAndTheAppIsUnchanged()
    {
        var path = FindFile();
        if (path is null) return;
        var content = File.ReadAllText(path);

        var apps = ImportDocumentParser.ParseAll(content);

        var app = apps.Should().ContainSingle().Subject;
        app.Issues.Should().NotContain(i => i.Code.StartsWith("PIPELINE_"));
        var pipeline = app.Pipelines.Should().ContainSingle().Subject;
        pipeline.Name.Should().Be("All Step");
        pipeline.AppRef.Should().Be(app.AppRef);
        pipeline.SourceStepCount.Should().Be(18);
        pipeline.SkippedStepCount.Should().Be(0);
        pipeline.Steps.Select(s => s.Subtype).Should().Equal(
            "new-event", "create-record", "update-record", "delete-record", "search-records", "for-each",
            "look-up-record", "if-else", "copy-records", "pause", "prepare-bulk-upsert", "add-bulk-upsert-row", "commit-upsert", "if-else");

        // Only the step whose value has no PowerFlows equivalent ({{al.$namespace}}) is a draft.
        Flatten(pipeline.Steps).Where(s => !s.IsComplete).Select(s => s.RefId).Should().Equal("ref_am");
    }

    [Fact]
    public void RealExport_AppConversionIsTheSameWithOrWithoutItsPipeline()
    {
        var path = FindFile();
        if (path is null) return;
        var content = File.ReadAllText(path);

        var withPipeline = QblToPblConverter.Convert(QblSerializer.Deserialize(content));
        var parsed = ImportDocumentParser.ParseAll(content).Single();

        System.Text.Json.JsonSerializer.Serialize(parsed.Document).Should().Be(System.Text.Json.JsonSerializer.Serialize(withPipeline.Document));
        parsed.Issues.Select(i => i.Code + i.Message).Should().Equal(withPipeline.Issues.Select(i => i.Code + i.Message));
    }

    /// <summary>The second export (<c>PowerFlows.qbl</c> in Downloads): three pipelines with every approved step kind.</summary>
    [Fact]
    public void SecondRealExport_EveryStepOfEveryPipelineIsImported()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PowerFlows.qbl");
        if (!File.Exists(path)) return;

        var app = ImportDocumentParser.ParseAll(File.ReadAllText(path)).Should().ContainSingle().Subject;

        app.Pipelines.Select(p => (p.Name, p.SourceStepCount, Imported: Flatten(p.Steps).Count()))
            .Should().Equal(("All Step", 18, 18), ("PowerFlow All Step", 25, 25), ("PowerFlow All Step Call", 2, 2));
        app.Pipelines.Should().OnlyContain(p => p.SkippedStepCount == 0);
        var all = app.Pipelines.Single(p => p.Name == "PowerFlow All Step");
        Flatten(all.Steps).Select(s => s.Subtype).Should().Contain(["handle-errors", "upload-file", "send-email-outlook", "call-another-pipeline", "import-with-csv"]);
        all.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" || i.Code == "PIPELINE_SETTING_NOT_IMPORTED");
        // Drafts only where PowerFlows needs a choice the file cannot give: the upload's file field, Copy Records filling one
        // destination field from two columns, and the CSV template. Address parts are imported as parts.
        Flatten(all.Steps).Where(s => !s.IsComplete).Select(s => s.Subtype).Should().Equal("upload-file", "copy-records", "import-with-csv");
        app.Pipelines.Single(p => p.Name == "PowerFlow All Step Call").Steps[0].Subtype.Should().Be("pipeline-called");
    }

    /// <summary>The third export (<c>PoweflowAllStep.qbl</c> in Downloads): seven pipelines, every one of the 24 approved steps.</summary>
    [Fact]
    public void ThirdRealExport_EveryApprovedStepIsImported()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PoweflowAllStep.qbl");
        if (!File.Exists(path)) return;

        var app = ImportDocumentParser.ParseAll(File.ReadAllText(path)).Should().ContainSingle().Subject;

        app.Pipelines.Should().HaveCount(7).And.OnlyContain(p => p.SkippedStepCount == 0 && p.Steps.Count > 0);
        app.Pipelines.Sum(p => Flatten(p.Steps).Count()).Should().Be(app.Pipelines.Sum(p => p.SourceStepCount));
        var subtypes = app.Pipelines.SelectMany(p => Flatten(p.Steps)).Select(s => s.Subtype).ToHashSet();
        subtypes.Should().Contain([
            "new-event", "new-bulk-event", "search-records", "look-up-record", "create-record", "update-record", "delete-record",
            "upload-file", "make-request", "prepare-bulk-upsert", "add-bulk-upsert-row", "commit-upsert", "import-with-csv", "copy-records",
            "if-else", "for-each", "handle-errors", "pause", "webhook", "send-email-outlook", "stop", "pipeline-called", "call-another-pipeline"]);
        var requests = app.Pipelines.SelectMany(p => Flatten(p.Steps)).Where(s => s.Subtype == "make-request").ToList();
        requests.Select(s => s.Config["requestMode"]).Should().Contain(["http", "quickbase"]);
        app.Pipelines.SelectMany(p => p.Issues).Should().NotContain(i => i.Code == "PIPELINE_SETTING_NOT_IMPORTED" || i.Code == "PIPELINE_FILTER_NOT_VERIFIED");
    }

    private static IEnumerable<PblPipelineStep> Flatten(IEnumerable<PblPipelineStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(Flatten(s.Children)).Concat(Flatten(s.ElseChildren))
            .Concat(Flatten(s.SuccessChildren)).Concat(Flatten(s.ErrorChildren)));
}
