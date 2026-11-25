using System.Text;
using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using Xunit;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>
/// The customer's real pipeline-only export (AllActivePipelines.yaml, 2026-11-19) bound to a snapshot of the imported apps
/// (meta.App / meta.AppTable / meta.AppField of the local tenant on 2026-11-24, with the Quickbase ids the app import stored).
/// A pipeline goes in only when every Quickbase app / table id it uses is stored on an imported app / table; nothing is ever
/// matched by name or fields. Skipped when the files are not on this machine.
/// </summary>
public class QblPipelineOnlyRealFilesTests(ITestOutputHelper output)
{
    internal const string SNAPSHOT = @"C:\ASP\test-data\qbl\pb_apps_snapshot_20261124.json";
    internal const string PIPELINE_FILE = @"C:\Users\Administrator\Downloads\drive-download-20261119T051341Z-1-001\AllActivePipelines.yaml";

    private static bool Available => File.Exists(SNAPSHOT) && File.Exists(PIPELINE_FILE);

    private sealed record SnapField(int Fid, string Name, string Label, string TypeCode, bool IsSystem, string? ResultTypeCode);
    private sealed record SnapTable(long Id, string Name, string? QbTableId, int? KeyFid, List<SnapField>? Fields);
    private sealed record SnapApp(long Id, Guid PublicId, string Name, string? QbAppId, List<SnapTable>? Tables);

    /// <summary>The imported apps as the pipeline-only import reads them (only apps with a stored Quickbase app id, like
    /// PipelineOnlyImportService.CandidatesAsync).</summary>
    internal static IReadOnlyList<PipelineOnlyApp> SnapshotApps() =>
        JsonSerializer.Deserialize<List<SnapApp>>(File.ReadAllText(SNAPSHOT))!
            .Where(a => !string.IsNullOrWhiteSpace(a.QbAppId))
            .Select(a => new PipelineOnlyApp(a.PublicId, a.Id, a.Name,
                (a.Tables ?? []).Select(t => new PipelineOnlyTable(t.Id, t.Name, t.KeyFid,
                    (t.Fields ?? []).Select(f => new PipelineOnlyField(f.Fid, f.Name, f.Label, f.TypeCode, f.IsSystem, f.ResultTypeCode)).ToList(),
                    t.QbTableId)).ToList(),
                a.QbAppId))
            .ToList();

    private static int PipelineCount(string content) =>
        System.Text.RegularExpressions.Regex.Matches(content, @"^  \S[^\r\n]*:\r?\n    Type: QB::Pipeline\r?$", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

    private static string Signature(PipelineOnlyBinding b) =>
        string.Join("\n", b.Matches.OrderBy(m => m.QbTableId).Select(m => $"{m.QbTableId}>{m.TableId}"))
        + "\n#" + string.Join("\n", b.SkippedPipelines.Select(s => s.Name).Order());

    [Fact]
    public void RealFile_EveryMatchIsTheStoredId_EveryPipelineIsKeptOrReported()
    {
        if (!Available) return;
        var content = File.ReadAllText(PIPELINE_FILE);
        var apps = SnapshotApps();
        var trace = new StringBuilder();
        QblPipelineOnlyBinder.Trace = l => trace.AppendLine(l);
        PipelineOnlyBinding binding;
        try { binding = QblPipelineOnlyBinder.Bind(content, apps); }
        finally { QblPipelineOnlyBinder.Trace = null; }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pipeline_ids_trace.txt"), trace.ToString());

        var tableById = apps.SelectMany(a => a.Tables.Select(t => (App: a, Table: t))).ToDictionary(x => x.Table.Id);
        // Exact: every match is the table whose stored QBTableID is that Quickbase id, inside the app of its stored QBAppID.
        foreach (var m in binding.Matches)
        {
            var (app, table) = tableById[m.TableId];
            table.QbTableId.Should().BeEquivalentTo(m.QbTableId);
            m.AppPublicId.Should().Be(app.PublicId);
        }
        // Nothing lost: every pipeline of the file is either still in the rewritten file or reported with its reason.
        var total = PipelineCount(content);
        var kept = PipelineCount(binding.Content);
        (kept + binding.SkippedPipelines.Count).Should().Be(total);
        binding.SkippedPipelines.Should().OnlyContain(s => s.Reason.Length > 0);
        // A skipped pipeline names an id that really is not stored.
        var storedTables = apps.SelectMany(a => a.Tables).Select(t => t.QbTableId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var storedApps = apps.Select(a => a.QbAppId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        binding.UnmatchedTables.Should().OnlyContain(t => !storedTables.Contains(t));
        binding.UnmatchedApps.Should().OnlyContain(a => !storedApps.Contains(a));

        // Converted: every kept pipeline lands in a matched app.
        var pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content, skipStepsWithUnmatchedTable: true);
        var (keptPipelines, notImported) = PipelineOnlyImportService.SplitByApp(pipelines, binding.Apps.Select(a => a.AppRef).ToList());
        pipelines.Should().HaveCount(kept);
        pipelines.SelectMany(p => p.Issues).Should().NotContain(i => i.Code == "PIPELINE_STEP_TABLE_NOT_MATCHED");
        output.WriteLine($"file={total} idsFound={kept} idsMissing={binding.SkippedPipelines.Count} imported={keptPipelines.Count} notConvertible={notImported.Count} apps={binding.Apps.Count}");
        foreach (var (name, reason) in notImported) output.WriteLine($"  NOT CONVERTIBLE {name}: {reason}");
    }

    [Fact]
    public void RealFile_SameResult_WhateverTheOrderOfTheApps()
    {
        if (!Available) return;
        var content = File.ReadAllText(PIPELINE_FILE);
        var apps = SnapshotApps();
        var expected = Signature(QblPipelineOnlyBinder.Bind(content, apps));
        var shuffled = apps.OrderBy(a => a.Name.GetHashCode(StringComparison.Ordinal) ^ 0x5bd1e995).Reverse().ToList();
        Signature(QblPipelineOnlyBinder.Bind(content, shuffled)).Should().Be(expected);
    }

    [Fact]
    public void RealFile_LookAlikeAppsWithoutIds_GetNothing()
    {
        if (!Available) return;
        var content = File.ReadAllText(PIPELINE_FILE);
        var apps = SnapshotApps();
        // Newer exact copies of every app (same names, tables, fields) that were not imported from Quickbase: no stored ids.
        var copies = apps.Select((a, i) => a with
        {
            Id = 100000 + i, PublicId = Guid.NewGuid(), QbAppId = null,
            Tables = a.Tables.Select(t => t with { Id = 1000000 + t.Id, QbTableId = null }).ToList(),
        });
        var binding = QblPipelineOnlyBinder.Bind(content, apps.Concat(copies).ToList());

        binding.Matches.Should().OnlyContain(m => m.TableId < 1000000);
        Signature(binding).Should().Be(Signature(QblPipelineOnlyBinder.Bind(content, apps)));
    }

    [Fact]
    public void RealFile_AnAppNotImported_OnlyItsPipelinesAreSkipped()
    {
        if (!Available) return;
        var content = File.ReadAllText(PIPELINE_FILE);
        var apps = SnapshotApps();
        var full = QblPipelineOnlyBinder.Bind(content, apps);
        // The app most kept pipelines use.
        var busiest = full.Matches.GroupBy(m => m.AppPublicId).OrderByDescending(g => g.Count()).First().Key;
        var removed = apps.Single(a => a.PublicId == busiest);
        var without = QblPipelineOnlyBinder.Bind(content, apps.Where(a => a.PublicId != busiest).ToList());

        without.Matches.Should().NotContain(m => m.AppPublicId == busiest);
        without.SkippedPipelines.Count.Should().BeGreaterThan(full.SkippedPipelines.Count);
        // Everything skipped before is still skipped; what is newly skipped names one of the removed app's ids.
        without.SkippedPipelines.Select(s => s.Name).Should().Contain(full.SkippedPipelines.Select(s => s.Name));
        var removedIds = removed.Tables.Select(t => t.QbTableId!).Append(removed.QbAppId!).ToList();
        var newly = without.SkippedPipelines.Where(s => !full.SkippedPipelines.Contains(s) && !full.SkippedPipelines.Any(f => f.Name == s.Name)).ToList();
        newly.Should().NotBeEmpty();
        newly.Should().OnlyContain(s => removedIds.Any(id => s.Reason.Contains(id, StringComparison.OrdinalIgnoreCase)));
        output.WriteLine($"removed '{removed.Name}': +{newly.Count} skipped");
    }
}
