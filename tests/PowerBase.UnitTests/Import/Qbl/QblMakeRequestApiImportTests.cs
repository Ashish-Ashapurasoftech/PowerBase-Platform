using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Regression: a pipeline-only QBL whose Make Request steps call the Quickbase API (JSON v1 and XML) is imported with
/// every call pointed at the PowerBase Quickbase-compatible endpoint of the matched table: path kept, body kept, ids replaced,
/// Quickbase credentials dropped.</summary>
public class QblMakeRequestApiImportTests
{
    private static string Step(string id, string method, string url, string? body = null, string? headers = null)
    {
        var lines = new List<string>
        {
            "      - Type: QB::PipelineStep::Quickbase",
            "        Properties:",
            $"          Id: {id}",
            "          Type: pipe",
            "          Name: Make Request",
            "          SubType: action",
            "          PipelineChannelAccount: !Ref",
            "            PipelineChannelAccount: $PipelineChannelAccount_Quickbase",
            "          Channel: { Resource: quickbase_api, Id: quickbase, Selector: make }",
            "          Inputs:",
            "            Content_Type: application/json",
            $"            Method: {method}",
            $"            Url: '{url}'",
        };
        if (body is not null) lines.Add("            Body: '" + body.Replace("'", "''") + "'");
        if (headers is not null) lines.Add(headers.TrimEnd('\n'));
        return string.Join("\n", lines) + "\n";
    }

    private static string File(params string[] steps) =>
        "Version: 0.12\nResources:\n  $Pipeline_A:\n    Type: QB::Pipeline\n    Properties:\n      Name: Api Calls\n      Tags: []\n    Steps:\n"
        + string.Concat(steps)
        + "  $PipelineChannelAccount_Quickbase:\n    Type: QB::PipelineChannelAccount\n    Properties:\n      Channel: quickbase\n      AccountType: tokenless\n      Alias: realm-default-account\n";

    private static PipelineOnlyField F(int fid, string label, string type = "Text", bool system = false) => new(fid, label.Replace(" ", "_"), label, type, system);

    private static readonly PipelineOnlyField[] Sys =
    [
        F(1, "Date Created", "DateTime", true), F(2, "Date Modified", "DateTime", true), F(3, "Record ID#", "Number", true),
        F(4, "Record Owner", "User", true), F(5, "Last Modified By", "User", true),
    ];

    private static IReadOnlyList<PipelineOnlyApp> Apps() =>
    [
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, "YSL Dev Docs",
        [
            new(10, "Employee", null, [.. Sys, F(7, "Email", "Email"), F(20, "Name"), F(22, "Status")], "bwcrtu9k5"),
            new(11, "Orders", null, [.. Sys, F(6, "Total", "Number")], "bordersq1"),
        ], "bt2kqxkqk"),
    ];

    private static (PblPipeline Pipeline, PipelineOnlyBinding Binding) Import(string file)
    {
        var binding = QblPipelineOnlyBinder.Bind(file, Apps());
        return (QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Single(), binding);
    }

    private static string Render(object? value) => value switch
    {
        string s => s,
        PblPipelineTextValue t => string.Concat(t.Parts.Select(Render)),
        PblPipelineTableValue t => "{table:" + t.TableRef + "}",
        PblPipelineAppValue => "{app}",
        _ => "?" + value,
    };

    [Fact]
    public void TableUsedOnlyByMakeRequest_IsMatchedFromTheBodyFieldIds()
    {
        var (_, binding) = Import(File(Step("aa", "POST", "https://api.quickbase.com/v1/records/query",
            """{"from":"bwcrtu9k5","select":[3,7,20],"where":"{22.EX.'Active'}"}""")));

        binding.Matches.Should().ContainSingle(m => m.QbTableId == "bwcrtu9k5").Which.TableName.Should().Be("Employee");
    }

    [Fact]
    public void PipelineWithOnlyAQuickbaseApiCall_GoesIntoTheAppOfThatTable()
    {
        var (pipeline, binding) = Import(File(Step("aa", "POST", "https://api.quickbase.com/v1/records/query", """{"from":"bwcrtu9k5"}""")));

        pipeline.AppRef.Should().Be(binding.Apps.Single().AppRef);
        PowerBase.Application.Import.Services.PipelineOnlyImportService.SplitByApp([pipeline], [binding.Apps.Single().AppRef]).Kept.Should().ContainSingle();
    }

    [Fact]
    public void Query_KeepsPathAndBody_ReplacesTableId()
    {
        var (pipeline, _) = Import(File(Step("aa", "POST", "https://api.quickbase.com/v1/records/query",
            """{"from":"bwcrtu9k5","select":[3,7,20],"where":"{20.SW.'R'}"}""")));

        var step = pipeline.Steps.Single();
        step.Config["requestMode"].Should().Be("quickbase");
        step.Config["method"].Should().Be("POST");
        Render(step.Config["url"]).Should().Be("/v1/records/query");
        Render(step.Config["body"]).Should().StartWith("{\"from\":\"{table:").And.EndWith("}\",\"select\":[3,7,20],\"where\":\"{20.SW.'R'}\"}");
        step.IsComplete.Should().BeTrue();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_REQUEST_URL_MAPPED");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REQUEST_URL_QUICKBASE" || i.Code == "PIPELINE_REQUEST_BODY_FORMAT" || i.Code == "PIPELINE_REQUEST_NEEDS_REVIEW");
    }

    [Fact]
    public void Upsert_DropsQuickbaseAuthAndRealmHeaders_KeepsOthers()
    {
        // Quickbase writes headers as one "Name" and one "Value" text, a header per line.
        var headers = "            Headers:\n              Name: \"QB-Realm-Hostname\\nAuthorization\\nX-Trace\"\n              Value: \"ysl.quickbase.com\\nQB-USER-TOKEN abc\\nt1\"";
        var (pipeline, _) = Import(File(Step("aa", "POST", "https://api.quickbase.com/v1/records",
            """{"to":"bwcrtu9k5","data":[{"20":{"value":"x"},"7":{"value":"a@b.c"}}]}""", headers)));

        var step = pipeline.Steps.Single();
        Render(step.Config["url"]).Should().Be("/v1/records");
        var names = ((List<object?>)step.Config["headersList"]!).Cast<Dictionary<string, object?>>().Select(h => h["name"]?.ToString()).ToList();
        names.Should().Equal("X-Trace");
        Render(step.Config["body"]).Should().Contain("{table:");
    }

    [Fact]
    public void Delete_KeepsDeleteMethod()
    {
        var (pipeline, _) = Import(File(Step("aa", "DELETE", "https://api.quickbase.com/v1/records",
            """{"from":"bwcrtu9k5","where":"{20.EX.'x'}"}""")));

        pipeline.Steps.Single().Config["method"].Should().Be("DELETE");
        Render(pipeline.Steps.Single().Config["url"]).Should().Be("/v1/records");
    }

    [Fact]
    public void Fields_TableIdInQueryString_IsReplaced()
    {
        var (pipeline, _) = Import(File(
            Step("aa", "GET", "https://api.quickbase.com/v1/fields?tableId=bwcrtu9k5&includeFieldPerms=true"),
            Step("ab", "POST", "https://api.quickbase.com/v1/records/query", """{"from":"bwcrtu9k5","select":[7,20,22]}""")));

        Render(pipeline.Steps[0].Config["url"]).Should().MatchRegex(@"^/v1/fields\?tableId=\{table:.+\}&includeFieldPerms=true$");
    }

    [Fact]
    public void XmlApi_SameAction_CredentialsDropped()
    {
        var (pipeline, _) = Import(File(
            Step("aa", "GET", "https://ysl.quickbase.com/db/bwcrtu9k5?a=API_AddRecord&_fid_20=x&_fid_7=a@b.c&_fid_22=New&usertoken=secret")));

        var step = pipeline.Steps.Single();
        step.Config["method"].Should().Be("GET");
        var url = Render(step.Config["url"]);
        url.Should().MatchRegex(@"^/db/\{table:.+\}\?a=API_AddRecord&_fid_20=x&_fid_7=a%40b.c&_fid_22=New$");
        url.Should().NotContain("secret");
    }

    [Fact]
    public void UnknownTable_InBody_SkipsThePipeline()
    {
        var file = File(
            Step("aa", "POST", "https://api.quickbase.com/v1/records/query", """{"from":"bwcrtu9k5","select":[7,20,22]}"""),
            Step("ab", "POST", "https://api.quickbase.com/v1/records/query", """{"from":"zzzzzzzzz"}"""));
        var binding = QblPipelineOnlyBinder.Bind(file, Apps());

        // The Quickbase table the call reads was not imported: the PowerFlow could never work, so it is not imported at all.
        binding.SkippedPipelines.Should().ContainSingle().Which.Reason.Should().Contain("zzzzzzzzz");
        QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Should().BeEmpty();
    }

    [Fact]
    public void UnsupportedCall_UrlIsRemovedAndReported()
    {
        var (pipeline, _) = Import(File(
            Step("aa", "POST", "https://api.quickbase.com/v1/records/query", """{"from":"bwcrtu9k5","select":[7,20,22]}"""),
            Step("ab", "POST", "https://api.quickbase.com/v1/docTemplates/1/generate?tableId=bwcrtu9k5")));

        Render(pipeline.Steps[1].Config["url"]).Should().BeEmpty();
        pipeline.Issues.Should().Contain(i => i.Message.Contains("docTemplates") && i.Message.Contains("removed"));
        pipeline.Steps[1].IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ReportCall_IsMappedButFlaggedForTheReportId()
    {
        var (pipeline, _) = Import(File(
            Step("aa", "POST", "https://api.quickbase.com/v1/reports/1/run?tableId=bwcrtu9k5&top=5"),
            Step("ab", "POST", "https://api.quickbase.com/v1/records/query", """{"from":"bwcrtu9k5","select":[7,20,22]}""")));

        Render(pipeline.Steps[0].Config["url"]).Should().MatchRegex(@"^/v1/reports/1/run\?tableId=\{table:.+\}&top=5$");
        pipeline.Steps[0].IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_REQUEST_NEEDS_REVIEW" && i.Message.Contains("report number"));
    }

    [Theory]
    [InlineData("https://api.quickbase.com/v1/records/query", """{"from":"bwcrtu9k5","select":[3,6],"sortBy":[{"fieldId":7}],"where":"{8.EX.'a'}"}""", new[] { 3, 6, 7, 8 })]
    [InlineData("https://api.quickbase.com/v1/records", """{"to":"bwcrtu9k5","data":[{"6":{"value":1},"9":{"value":2}}],"mergeFieldId":10}""", new[] { 6, 9, 10 })]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_EditRecord&rid=1&_fid_12=x", null, new[] { 12 })]
    public void Evidence_ReadsTableAndFieldIds(string url, string? body, int[] fids)
    {
        var evidence = QuickbaseApiUrlMap.Evidence(url, body).Single();
        evidence.QbTableId.Should().Be("bwcrtu9k5");
        evidence.Fids.Should().Equal(fids);
    }

    [Fact]
    public void Evidence_NotQuickbase_IsEmpty() =>
        QuickbaseApiUrlMap.Evidence("https://httpbin.org/post", """{"from":"bwcrtu9k5"}""").Should().BeEmpty();
}
