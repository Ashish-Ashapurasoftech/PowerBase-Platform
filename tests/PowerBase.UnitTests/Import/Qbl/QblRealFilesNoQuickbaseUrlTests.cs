using System.Text.RegularExpressions;
using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using Xunit;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Regression over the user's real QBL exports (Downloads / Desktop; skipped when a file is missing): after conversion
/// no PowerFlow step — config or note — holds a Quickbase address.</summary>
public class QblRealFilesNoQuickbaseUrlTests(ITestOutputHelper output)
{
    private static readonly Regex QuickbaseUrl = new(@"(?<![@\w.-])(?:https?://)?(?:[A-Za-z0-9-]+\.)*quickbase\.com(?![A-Za-z0-9.-])", RegexOptions.IgnoreCase);

    public static TheoryData<string> Files()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = new TheoryData<string>();
        foreach (var name in new[] { @"Downloads\PowerFlow100Pipeline.qbl", @"Downloads\pipeline.qbl", @"Downloads\Pipeline_New.qbl", @"Desktop\PipelineOnly.qbl", @"Downloads\QBL_PBL.qbl" })
            data.Add(Path.Combine(home, name));
        return data;
    }

    private static string Render(object? value) => value switch
    {
        null => "",
        string s => s,
        PblPipelineTextValue t => string.Concat(t.Parts.Select(Render)),
        Dictionary<string, object?> d => string.Join(" ", d.Values.Select(Render)),
        List<object?> l => string.Join(" ", l.Select(Render)),
        PblPipelineFieldMapValue m => string.Join(" ", m.Entries.Select(e => Render(e.Value))),
        _ => "",
    };

    private static IEnumerable<PblPipelineStep> Flatten(IEnumerable<PblPipelineStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(Flatten(s.Children)).Concat(Flatten(s.ElseChildren)).Concat(Flatten(s.SuccessChildren)).Concat(Flatten(s.ErrorChildren)));

    [Theory]
    [MemberData(nameof(Files))]
    public void RealExport_NoStepKeepsAQuickbaseAddress(string path)
    {
        if (!File.Exists(path)) return;
        var content = File.ReadAllText(path);
        IReadOnlyList<PblPipeline> pipelines;
        if (QblPipelineOnlyBinder.IsPipelinesOnly(content))
        {
            var binding = QblPipelineOnlyBinder.Bind(content, []);
            pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content);
        }
        else
            pipelines = ImportDocumentParser.ParseAll(content).SelectMany(a => a.Pipelines ?? []).ToList();

        var steps = pipelines.SelectMany(p => Flatten(p.Steps)).ToList();
        var offenders = steps.Select(s => (s.RefId, Text: Render(s.Config) + " " + s.Notes))
            .Where(x => QuickbaseUrl.IsMatch(x.Text)).ToList();
        output.WriteLine($"{Path.GetFileName(path)}: {pipelines.Count} pipelines, {steps.Count} steps, " +
                         $"{pipelines.SelectMany(p => p.Issues).Count(i => i.Code.Contains("QUICKBASE_URL") || i.Code == "PIPELINE_REQUEST_URL_MAPPED")} URL changes");
        foreach (var group in pipelines.SelectMany(p => p.Issues).Where(i => i.Code.Contains("QUICKBASE_URL") || i.Code.StartsWith("PIPELINE_REQUEST"))
                     .GroupBy(i => i.Code))
            output.WriteLine($"  {group.Key} x{group.Count()}: {group.First().Message}");
        offenders.Should().BeEmpty(string.Join("\n", offenders.Select(o => o.RefId + ": " + QuickbaseUrl.Match(o.Text).Value)));
    }
}
