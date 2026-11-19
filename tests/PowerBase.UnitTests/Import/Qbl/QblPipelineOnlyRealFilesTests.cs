using System.Text;
using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using Xunit;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>
/// The customer's real pair of files (2026-11-18): an application export with 5 apps and a pipeline-only export of 1094
/// PowerFlows from ~45 Quickbase apps. The apps are built from the application export exactly as the import creates them
/// (Fid = Quickbase field id), then the pipeline file is bound to them. Skipped when the files are not on this machine.
/// </summary>
public class QblPipelineOnlyRealFilesTests(ITestOutputHelper output)
{
    private const string APP_FILE = @"C:\Users\Administrator\Downloads\QBL-0.12-ba97eac7-399f-4445-9dcd-faa6e0744bb5.yaml";
    private const string PIPELINE_FILE = @"C:\Users\Administrator\Downloads\AllActivePipelines.yaml";

    internal static IReadOnlyList<PipelineOnlyApp> AppsFromExport(string path)
    {
        var results = QblToPblConverter.ConvertAll(QblSerializer.Deserialize(System.IO.File.ReadAllText(path)));
        var apps = new List<PipelineOnlyApp>();
        long tableId = 1000;
        foreach (var (result, i) in results.Select((r, i) => (r, i)))
        {
            var tables = new List<PipelineOnlyTable>();
            foreach (var table in result.Document.Tables)
            {
                var fields = new List<PipelineOnlyField>
                {
                    new(1, "Date_Created", "Date Created", "DateTime", true), new(2, "Date_Modified", "Date Modified", "DateTime", true),
                    new(3, "Record_ID", "Record ID#", "Number", true), new(4, "Record_Owner", "Record Owner", "User", true),
                    new(5, "Last_Modified_By", "Last Modified By", "User", true),
                };
                fields.AddRange(table.Fields.Where(f => f.SourceId is > 5)
                    .Select(f => new PipelineOnlyField(f.SourceId!.Value, f.Name, f.Label ?? f.Name, f.TypeCode, false)));
                // Reference / Lookup fields live on the child, Summary and report-link fields on the parent (relationships).
                foreach (var rel in result.Document.Relationships)
                {
                    if (rel.ChildTableRef == table.LogicalRef)
                    {
                        if (rel.ReferenceFieldSourceId is { } rid) fields.Add(new(rid, rel.ReferenceFieldName, rel.ReferenceFieldLabel ?? rel.ReferenceFieldName, "Reference", false));
                        fields.AddRange(rel.Lookups.Where(l => l.SourceId is > 5).Select(l => new PipelineOnlyField(l.SourceId!.Value, l.Name, l.Label ?? l.Name, "Lookup", false)));
                    }
                    if (rel.ParentTableRef == table.LogicalRef)
                    {
                        fields.AddRange(rel.Summaries.Where(l => l.SourceId is > 5).Select(l => new PipelineOnlyField(l.SourceId!.Value, l.Name, l.Label ?? l.Name, "Summary", false)));
                        if (rel.ReportLink is { SourceId: > 5 } link) fields.Add(new(link.SourceId!.Value, link.Label ?? "", link.Label ?? "", "ReportLink", false));
                    }
                }
                tables.Add(new PipelineOnlyTable(tableId++, table.Name, null, fields.GroupBy(f => f.Fid).Select(g => g.First()).ToList()));
            }
            apps.Add(new PipelineOnlyApp(Guid.NewGuid(), i + 1, result.Document.App.Name, tables));
        }
        return apps;
    }

    [Fact]
    public void RealFiles_PipelinesGoOnlyToTheirOwnApps()
    {
        if (!System.IO.File.Exists(APP_FILE) || !System.IO.File.Exists(PIPELINE_FILE)) return;

        var apps = AppsFromExport(APP_FILE);
        apps.Should().HaveCount(5);
        var binding = QblPipelineOnlyBinder.Bind(System.IO.File.ReadAllText(PIPELINE_FILE), apps);
        var pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content, skipStepsWithUnmatchedTable: true);
        var (kept, notImported) = PipelineOnlyImportService.SplitByApp(pipelines, binding.Apps.Select(a => a.AppRef).ToList());

        var report = new StringBuilder();
        report.AppendLine($"pipelines={pipelines.Count} kept={kept.Count} notImported={notImported.Count}");
        report.AppendLine($"matchedTables={binding.Matches.Count} unmatchedTables={binding.UnmatchedTables.Count} unmatchedApps={binding.UnmatchedApps.Count}");
        foreach (var group in binding.Matches.GroupBy(m => m.AppName))
            report.AppendLine($"APP {group.Key}: {string.Join(", ", group.Select(m => $"{m.QbTableId}({m.QbAppId ?? "-"})→{m.TableName}"))}");
        foreach (var (appRef, app) in binding.Apps)
        {
            var mine = kept.Where(p => p.AppRef == appRef).ToList();
            report.AppendLine($"PIPELINES {app.Name}: {mine.Count}");
            foreach (var p in mine)
                report.AppendLine($"   {p.Name} | changes={p.ChangesExecution} steps={p.SourceStepCount}/skipped={p.SkippedStepCount} | {string.Join(",", p.Issues.Select(i => i.Code).Distinct())}");
        }
        report.AppendLine("ISSUES (kept): " + string.Join(", ", kept.SelectMany(p => p.Issues.Select(i => i.Code).Distinct())
            .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
        foreach (var issue in kept.SelectMany(p => p.Issues.Select(i => (p.Name, i)))
                     .Where(x => x.i.Code is "PIPELINE_FIELD_NOT_FOUND" or "PIPELINE_NO_VALID_START" or "PIPELINE_STEP_NOT_SAVED" or "PIPELINE_FILTER_OPERATOR" or "PIPELINE_STEP_UNSUPPORTED" or "PIPELINE_INPUT_MISSING"))
            report.AppendLine($"DETAIL {issue.Name} | {issue.i.Code} | {issue.i.StepName} | {issue.i.Message}");
        foreach (var (name, reason) in notImported) report.AppendLine($"NOTIMPORTED {name} | {reason}");
        foreach (var note in binding.Notes.Take(200)) report.AppendLine("NOTE " + note);
        System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "pipelineonly_realfiles_report.txt"), report.ToString());
        output.WriteLine(report.ToString());

        // No imported step may point at a step that was skipped (a missing step makes the editor refuse every step).
        foreach (var pipeline in kept)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(pipeline.Steps);
            var refIds = System.Text.RegularExpressions.Regex.Matches(json, "\"RefId\":\"(ref_[A-Za-z0-9_]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(json, @"steps\.(ref_[A-Za-z0-9_]+)\.count"))
                refIds.Should().Contain(m.Groups[1].Value, $"'{pipeline.Name}' uses the count of {m.Groups[1].Value}");
        }

        pipelines.Should().HaveCount(1094);
        (kept.Count + notImported.Count).Should().Be(1094);
    }

    // ---------------------------------------------------------------- many apps: nothing depends on how many apps there are

    private static (List<PblPipeline> Kept, List<(string Name, string Reason)> NotImported, PipelineOnlyBinding Binding) Run(IReadOnlyList<PipelineOnlyApp> apps)
    {
        var binding = QblPipelineOnlyBinder.Bind(System.IO.File.ReadAllText(PIPELINE_FILE), apps);
        var pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content, skipStepsWithUnmatchedTable: true);
        var (kept, notImported) = PipelineOnlyImportService.SplitByApp(pipelines, binding.Apps.Select(a => a.AppRef).ToList());
        return (kept, notImported, binding);
    }

    private static Dictionary<string, string> AppOf(List<PblPipeline> kept, PipelineOnlyBinding binding) =>
        kept.ToDictionary(p => p.LogicalRef, p => binding.Apps.Single(a => a.AppRef == p.AppRef).App.Name);

    /// <summary>Unrelated apps that share field ids with the real ones (every app has fields 6, 7, 8 …), like a "max coverage" test
    /// app with every field type: decoys with the same ids but other labels, and decoys with common labels.</summary>
    internal static List<PipelineOnlyApp> Decoys(IReadOnlyList<PipelineOnlyApp> real, int count)
    {
        var common = new[] { "Name", "Email", "Status", "Start Date", "End Date", "Notes", "Phone", "Address", "Related Client", "Related Status", "Active", "Type", "Description", "Date", "Amount" };
        var result = new List<PipelineOnlyApp>();
        long tableId = 900_000;
        for (var i = 0; i < count; i++)
        {
            var tables = new List<PipelineOnlyTable>();
            for (var t = 0; t < 6; t++)
            {
                var fields = new List<PipelineOnlyField>
                {
                    new(1, "Date_Created", "Date Created", "DateTime", true), new(2, "Date_Modified", "Date Modified", "DateTime", true),
                    new(3, "Record_ID", "Record ID#", "Number", true), new(4, "Record_Owner", "Record Owner", "User", true),
                    new(5, "Last_Modified_By", "Last Modified By", "User", true),
                };
                for (var fid = 6; fid < 6 + 40 + i % 50; fid++)
                    fields.Add(new(fid, $"F{fid}", fid - 6 < common.Length && (i + t) % 2 == 0 ? common[fid - 6] : $"Decoy {i}-{t} Field {fid}", "Text", false));
                tables.Add(new PipelineOnlyTable(tableId++, $"Decoy Table {t}", null, fields));
            }
            result.Add(new PipelineOnlyApp(Guid.NewGuid(), 10_000 + i, $"Decoy App {i}", tables));
        }
        return result;
    }

    [Fact]
    public void RealFiles_ManyApps_DecoysAndCopies_GetNothing_RealAppsKeepExactlyTheirPipelines()
    {
        if (!System.IO.File.Exists(APP_FILE) || !System.IO.File.Exists(PIPELINE_FILE)) return;
        var real = AppsFromExport(APP_FILE);
        var baseline = Run(real);
        var baselineApps = AppOf(baseline.Kept, baseline.Binding);

        // 5 real apps + 40 decoys = 45 apps.
        var many = real.Concat(Decoys(real, 40)).ToList();
        var withDecoys = Run(many);
        AppOf(withDecoys.Kept, withDecoys.Binding).Should().BeEquivalentTo(baselineApps,
            "adding unrelated apps must not move or add a single PowerFlow");
        withDecoys.Binding.Matches.Should().OnlyContain(m => real.Any(a => a.PublicId == m.AppPublicId));

        // Apps listed in another order give the same result.
        var shuffled = Run(many.OrderBy(a => a.Name.GetHashCode(StringComparison.Ordinal)).Reverse().ToList());
        AppOf(shuffled.Kept, shuffled.Binding).Should().BeEquivalentTo(baselineApps);
    }

    /// <summary>Partial copies of the real apps under other names (70% of each table's fields): look like the apps the clone
    /// Quickbase apps (DSS / DSP / Voyage…) were copied from, so they must get nothing.</summary>
    internal static List<PipelineOnlyApp> LookAlikes(IReadOnlyList<PipelineOnlyApp> real)
    {
        long tableId = 800_000;
        return real.Select((app, i) => new PipelineOnlyApp(Guid.NewGuid(), 20_000 + i, $"Look-alike {i}",
            app.Tables.Select(tb => new PipelineOnlyTable(tableId++, tb.Name, null,
                tb.Fields.Where(fl => fl.IsSystem || fl.Fid % 10 < 7).ToList())).ToList())).ToList();
    }

    [Fact]
    public void RealFiles_LookAlikeApps_GetNothing()
    {
        if (!System.IO.File.Exists(APP_FILE) || !System.IO.File.Exists(PIPELINE_FILE)) return;
        var real = AppsFromExport(APP_FILE);
        var baseline = AppOf(Run(real).Kept, Run(real).Binding);
        var run = Run(real.Concat(LookAlikes(real)).Concat(Decoys(real, 20)).ToList());
        AppOf(run.Kept, run.Binding).Should().BeEquivalentTo(baseline);
        run.Binding.Matches.Should().OnlyContain(m => real.Any(a => a.PublicId == m.AppPublicId));

        // Only the look-alikes (renamed, older copies of the real apps): a Quickbase app may only be matched to the copy of its
        // OWN app, never to the copy of another app (a clone Quickbase app must not take it).
        var own = run.Binding.Matches.Where(m => m.QbAppId is not null).GroupBy(m => m.QbAppId!).ToDictionary(g => g.Key, g => real.ToList().FindIndex(a => a.PublicId == g.First().AppPublicId));
        var lookAlikes = LookAlikes(real);
        var alone = Run(lookAlikes);
        foreach (var m in alone.Binding.Matches.Where(m => m.QbAppId is not null))
        {
            own.Should().ContainKey(m.QbAppId!, $"Quickbase app {m.QbAppId} is not one of the imported apps, so it may not take '{m.AppName}'");
            lookAlikes.FindIndex(a => a.PublicId == m.AppPublicId).Should().Be(own[m.QbAppId!]);
        }
    }

    [Fact]
    public void RealFiles_AnAppNotImported_ItsPipelinesGoNowhereElse()
    {
        if (!System.IO.File.Exists(APP_FILE) || !System.IO.File.Exists(PIPELINE_FILE)) return;
        var real = AppsFromExport(APP_FILE);
        var first = Run(real);
        var baselineMatches = first.Binding.Matches.ToDictionary(m => m.QbTableId, m => m.TableId);
        var baselinePipelines = first.Kept.Select(p => p.LogicalRef).ToHashSet();

        foreach (var missing in real)
        {
            var others = real.Where(a => a.Id != missing.Id).Concat(Decoys(real, 10)).ToList();
            var run = Run(others);
            // Every Quickbase table keeps exactly the table it had (or none): removing an app never re-maps a table elsewhere.
            run.Binding.Matches.Should().OnlyContain(m => baselineMatches.ContainsKey(m.QbTableId) && baselineMatches[m.QbTableId] == m.TableId,
                $"without '{missing.Name}' no Quickbase table may be matched to another table");
            // No PowerFlow appears that was not imported before; none goes to the missing app or a decoy.
            run.Kept.Select(p => p.LogicalRef).Should().OnlyContain(r => baselinePipelines.Contains(r));
            AppOf(run.Kept, run.Binding).Values.Should().NotContain(missing.Name).And.OnlyContain(n => real.Any(a => a.Name == n));
        }
    }

    [Fact]
    public void RealFiles_EveryImportedStepUsesOnlyTablesAndFieldsOfItsMatchedApps()
    {
        if (!System.IO.File.Exists(APP_FILE) || !System.IO.File.Exists(PIPELINE_FILE)) return;
        var real = AppsFromExport(APP_FILE);
        var run = Run(real.Concat(Decoys(real, 40)).ToList());
        var tableRefs = run.Binding.Apps.SelectMany(a => a.App.Tables.Select(t => (Ref: $"$Table_PB_{t.Id}", Table: t))).ToDictionary(x => x.Ref, x => x.Table);
        foreach (var pipeline in run.Kept)
            foreach (var step in Flatten(pipeline.Steps))
                foreach (var value in Values(step.Config))
                {
                    if (value is PblPipelineTableValue tv)
                        tableRefs.Should().ContainKey(tv.TableRef, $"'{pipeline.Name}' step '{step.Name}'");
                    if (value is PblPipelineFieldValue fv && fv.TableRef is { } fieldTable && fv.SourceId is { } fid && !fv.IsBuiltIn && tableRefs.TryGetValue(fieldTable, out var table))
                        table.Fields.Select(f => f.Fid).Concat(table.Fields.Where(f => f.TypeCode == "Address").SelectMany(f => Enumerable.Range(f.Fid + 1, 6)))
                            .Should().Contain(fid, $"'{pipeline.Name}' step '{step.Name}' field {fid} must be a field of '{table.Name}'");
                }
    }

    private static IEnumerable<PblPipelineStep> Flatten(IEnumerable<PblPipelineStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in Flatten(step.Children.Concat(step.ElseChildren).Concat(step.SuccessChildren).Concat(step.ErrorChildren))) yield return child;
        }
    }

    private static IEnumerable<object?> Values(object? node)
    {
        switch (node)
        {
            case PblPipelineTextValue text:
                foreach (var part in text.Parts) foreach (var v in Values(part)) yield return v;
                break;
            case System.Collections.IDictionary map:
                foreach (var v in map.Values) foreach (var x in Values(v)) yield return x;
                break;
            case string: yield return node; break;
            case System.Collections.IEnumerable list:
                foreach (var v in list) foreach (var x in Values(v)) yield return x;
                break;
            default: yield return node; break;
        }
    }
}
