using System.Text;
using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using Xunit;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>
/// Strict pipeline import over the customer's real files (2026-11-19): 16 application exports (88 apps) and the pipeline-only
/// export AllActivePipelines.yaml. A pipeline is imported only when all of its steps convert exactly; every other one is
/// reported with its reasons. Skipped when the files are not on this machine.
/// </summary>
public class QblPipelineStrictRealFilesTests(ITestOutputHelper output)
{
    private const string FOLDER = @"C:\Users\Administrator\Downloads\drive-download-20261119T051341Z-1-001";
    private static string PipelineFile => Path.Combine(FOLDER, "AllActivePipelines.yaml");

    private static IReadOnlyList<PipelineOnlyApp> AllApps()
    {
        var apps = new List<PipelineOnlyApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(FOLDER, "QBL-*.yaml").OrderBy(f => f.Contains("ba97eac7") ? 1 : 0).ThenBy(f => f))
            foreach (var app in QblPipelineOnlyRealFilesTests.AppsFromExport(file))
                if (seen.Add(app.Name))
                    apps.Add(app with { Id = apps.Count + 1, Tables = app.Tables.Select(t => t with { Id = 1000 + apps.Count * 1000 + (t.Id % 1000) }).ToList() });
        return apps;
    }

    [Fact]
    public void RealFiles_Strict_EveryKeptPipelineIsComplete_EveryOtherHasAReason()
    {
        if (!Directory.Exists(FOLDER) || !File.Exists(PipelineFile)) return;

        var apps = AllApps();
        var trace = new StringBuilder();
        QblPipelineOnlyBinder.Trace = l => trace.AppendLine(l);
        PipelineOnlyBinding binding;
        try { binding = QblPipelineOnlyBinder.Bind(File.ReadAllText(PipelineFile), apps); }
        finally { QblPipelineOnlyBinder.Trace = null; }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pipeline_strict_trace.txt"), trace.ToString() + string.Join(Environment.NewLine, binding.Notes.Select(n => "NOTE " + n)));
        var pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content, skipStepsWithUnmatchedTable: true);
        var (kept, notImported) = PipelineOnlyImportService.SplitByApp(pipelines, binding.Apps.Select(a => a.AppRef).ToList());

        var report = new StringBuilder();
        report.AppendLine($"apps={apps.Count} matchedApps={binding.Apps.Count} pipelines={pipelines.Count} kept={kept.Count} notImported={notImported.Count}");
        foreach (var (appRef, app) in binding.Apps)
            report.AppendLine($"APP {app.Name}: kept={kept.Count(p => p.AppRef == appRef)}");
        foreach (var p in kept)
            report.AppendLine($"KEPT {binding.Apps.Single(a => a.AppRef == p.AppRef).App.Name} | {p.Name} | steps={p.SourceStepCount}");
        foreach (var (name, reason) in notImported)
            report.AppendLine($"NOTIMPORTED {name} | {reason}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pipeline_strict_report.txt"), report.ToString());
        // Expected steps of every kept pipeline (for the real-import comparison) and every QB step of every pipeline.
        static IEnumerable<object> Flat(IEnumerable<PblPipelineStep> steps, string branch) => steps.SelectMany(st =>
            new object[] { new { st.RefId, st.Name, st.Type, st.Subtype, st.Location, Branch = branch, st.IsComplete } }
                .Concat(Flat(st.Children, "children")).Concat(Flat(st.ElseChildren, "else")).Concat(Flat(st.SuccessChildren, "success")).Concat(Flat(st.ErrorChildren, "error")));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pipeline_strict_expected.json"), System.Text.Json.JsonSerializer.Serialize(
            pipelines.Select(p => new
            {
                p.Name, p.LogicalRef, p.SourceStepCount, p.Crontab, Kept = kept.Contains(p),
                App = p.AppRef is null ? null : binding.Apps.FirstOrDefault(a => a.AppRef == p.AppRef).App?.Name,
                Steps = Flat(p.Steps, "root").ToList(),
                Issues = p.Issues.Select(i => new { i.Code, i.Message, i.StepId, i.StepName, i.IsSkippedStep, Skip = PipelineCompleteness.IsSkipReason(i) }).ToList(),
                Reason = notImported.FirstOrDefault(n => n.Name == p.Name && !kept.Contains(p)).Reason,
            })));
        output.WriteLine(report.ToString().Split('\n').Take(120).Aggregate(new StringBuilder(), (b, l) => b.AppendLine(l)).ToString());

        foreach (var p in kept)
        {
            PipelineCompleteness.Reason(p).Should().BeNull($"'{p.Name}' was kept");
            p.SkippedStepCount.Should().Be(0);
        }
        notImported.Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n.Reason));
        (kept.Count + notImported.Count).Should().Be(pipelines.Count);
    }
}

public class PipelineCompletenessTests
{
    private static PblPipeline With(params PblPipelineIssue[] issues) => new() { Name = "P", SourceStepCount = 1, Steps = [new PblPipelineStep { Name = "s" }], Issues = issues.ToList() };

    [Fact]
    public void FieldNotFound_IsOnlyAWarning() =>
        PipelineCompleteness.Reason(With(new PblPipelineIssue { Code = "PIPELINE_FIELD_NOT_FOUND", Message = "Field 'x' not found", ChangesExecution = true })).Should().BeNull();

    [Fact]
    public void QuickbaseUrlReview_IsOnlyAWarning() =>
        PipelineCompleteness.Reason(With(new PblPipelineIssue { Code = "PIPELINE_QUICKBASE_URL_REVIEW", Message = "review", ChangesExecution = true })).Should().BeNull();

    [Fact]
    public void ReferenceToAFieldCleared_IsOnlyAWarning() =>
        PipelineCompleteness.Reason(With(new PblPipelineIssue { Code = "PIPELINE_REFERENCE_CLEARED", Message = "Value 'x' was cleared: field 'y' of step 'A' (a) was not found.", ChangesExecution = true })).Should().BeNull();

    [Theory]
    [InlineData("PIPELINE_STEP_TABLE_NOT_MATCHED")]
    [InlineData("PIPELINE_STEP_UNSUPPORTED")]
    [InlineData("PIPELINE_TABLE_MISSING")]
    [InlineData("PIPELINE_NO_VALID_START")]
    public void MissingStepAppOrTable_SkipsThePipeline(string code) =>
        PipelineCompleteness.Reason(With(new PblPipelineIssue { Code = code, Message = "m", StepId = "aa", StepName = "Step" })).Should().Contain(code).And.Contain("Step 'Step' (aa)");

    [Fact]
    public void ReferenceToASkippedStep_SkipsThePipeline() =>
        PipelineCompleteness.Reason(With(new PblPipelineIssue { Code = "PIPELINE_REFERENCE_CLEARED", Message = "Value '{{a.id}}' was cleared: step 'On New Event' (a) was not imported.", ChangesExecution = true })).Should().NotBeNull();

    [Fact]
    public void SkippedStep_SkipsThePipeline() =>
        PipelineCompleteness.Reason(With(new PblPipelineIssue { Code = "ANY", Message = "m", IsSkippedStep = true })).Should().NotBeNull();
}
