using System.Text.RegularExpressions;
using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Regression: after import no PowerFlow step holds a Quickbase address anywhere in its configuration.</summary>
public class QblNoQuickbaseUrlImportTests
{
    private const string Trigger = """
      - Type: QB::PipelineStep::Quickbase
        Properties:
          Id: aa
          Type: pipe
          Name: On New Event
          SubType: trigger
          PipelineChannelAccount: !Ref
            PipelineChannelAccount: $PipelineChannelAccount_Quickbase
          Channel: { Resource: record, Id: quickbase, Selector: on_new_event }
        InputsMeta:
          $PipelineStep_InputsMeta_aa:
            Type: QB::PipelineStep::InputsMeta
            Properties: { On_Add_Record: true, On_Modify_Record: false }
            PipelineStep_DataSources:
              $PipelineStep_DataSource_1:
                Type: QB::PipelineStep::DataSource::App
                Properties: { App: bt2kqxkqk, Name: app }
              $PipelineStep_DataSource_2:
                Type: QB::PipelineStep::DataSource::Fields
                Properties:
                  Fields: [7, 20, 22]
                  Name: export_fields
                  Source: !Ref
                    PipelineStep_DataSource: $PipelineStep_DataSource_3
              $PipelineStep_DataSource_3:
                Type: QB::PipelineStep::DataSource::Table
                Properties:
                  Table: bwcrtu9k5
                  Name: table
                  Source: !Ref
                    PipelineStep_DataSource: $PipelineStep_DataSource_1

""";

    private static string File(string steps) =>
        "Version: 0.12\nResources:\n  $Pipeline_A:\n    Type: QB::Pipeline\n    Properties:\n      Name: Links\n      Tags: []\n    Steps:\n"
        + Trigger + steps
        + "  $PipelineChannelAccount_Quickbase:\n    Type: QB::PipelineChannelAccount\n    Properties:\n      Channel: quickbase\n      AccountType: tokenless\n      Alias: realm-default-account\n"
        + "  $PipelineChannelAccount_Outlook:\n    Type: QB::PipelineChannelAccount\n    Properties:\n      Channel: microsoft-outlook\n      Alias: me\n";

    private static PipelineOnlyField F(int fid, string label, string type = "Text", bool system = false) => new(fid, label.Replace(" ", "_"), label, type, system);

    private static IReadOnlyList<PipelineOnlyApp> Apps() =>
    [
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, "YSL Dev Docs",
        [
            new(10, "Employee", null, [F(1, "Date Created", "DateTime", true), F(2, "Date Modified", "DateTime", true), F(3, "Record ID#", "Number", true),
                F(4, "Record Owner", "User", true), F(5, "Last Modified By", "User", true), F(7, "Email", "Email"), F(20, "Name"), F(22, "Status")], "bwcrtu9k5"),
        ], "bt2kqxkqk"),
    ];

    private static PblPipeline Import(string steps)
    {
        var file = File(steps);
        var binding = QblPipelineOnlyBinder.Bind(file, Apps());
        binding.Matches.Should().ContainSingle(m => m.QbTableId == "bwcrtu9k5");
        return QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Single();
    }

    private static string Render(object? value) => value switch
    {
        null => "",
        string s => s,
        PblPipelineTextValue t => string.Concat(t.Parts.Select(Render)),
        PblPipelineTableValue => "{table}",
        PblPipelineAppValue => "{app}",
        PblPipelineSiteValue s => s.Api ? "{api}" : "{site}",
        PblPipelineFieldValue f => "fid_" + f.SourceId,
        PblPipelineConnectionValue => "{connection}",
        Dictionary<string, object?> d => "{" + string.Join(",", d.Select(kv => kv.Key + ":" + Render(kv.Value))) + "}",
        List<object?> l => "[" + string.Join(",", l.Select(Render)) + "]",
        _ => value.ToString() ?? "",
    };

    private static void NoQuickbaseAnywhere(PblPipeline pipeline)
    {
        foreach (var step in pipeline.Steps)
            Render(step.Config).Should().NotMatchRegex("(?i)quickbase\\.com(?!.*@)", $"step {step.RefId} must not keep a Quickbase address");
    }

    private static string Email(string id, string body) => $"""
      - Type: QB::PipelineStep::MicrosoftOutlook
        Properties:
          Id: {id}
          Type: pipe
          Name: Send an Email
          SubType: action
          PipelineChannelAccount: !Ref
            PipelineChannelAccount: $PipelineChannelAccount_Outlook
          Channel: {"{"} Resource: email, Id: microsoft-outlook, Selector: send_email {"}"}
          Inputs:
            To_Addresses: '{"{{"}aa.email{"}}"}'
            Subject: New record
            Content_Type: HTML
            Body: '{body.Replace("'", "''")}'

""";

    private static string Request(string id, string method, string url, string channel = "quickbase") => $"""
      - Type: QB::PipelineStep::{(channel == "quickbase" ? "Quickbase" : "Webhooks")}
        Properties:
          Id: {id}
          Type: pipe
          Name: Make Request
          SubType: action
{(channel == "quickbase" ? "          PipelineChannelAccount: !Ref\n            PipelineChannelAccount: $PipelineChannelAccount_Quickbase\n" : "")}          Channel: {"{"} Resource: {(channel == "quickbase" ? "quickbase_api" : "request")}, Id: {(channel == "quickbase" ? "quickbase" : "webhooks")}, Selector: make {"}"}
          Inputs:
            Method: {method}
            Url: '{url}'

""";

    [Fact]
    public void EmailBody_RecordLink_BecomesThePowerBaseRecordPage()
    {
        var pipeline = Import(Email("ab", "Open <a href=\"https://ysl.quickbase.com/db/bwcrtu9k5?a=dr&rid={{aa.id}}\">{{aa.name}}</a>"));

        var body = Render(pipeline.Steps.Single(s => s.RefId == "ref_ab").Config["body"]);
        body.Should().Contain("{site}/app/{app}/tables/{table}/records/{{trigger.RecordPublicId}}/view");
        NoQuickbaseAnywhere(pipeline);
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_QUICKBASE_URL_REPLACED");
    }

    [Fact]
    public void MakeRequest_ComputedXmlUrl_CallsPowerBase_AndIsFlagged()
    {
        // The pipeline form of the formula "https://ysl.quickbase.com/db/" & [Related App] & "?a=API_EditRecord&rid=" & … .
        var pipeline = Import(Request("ab", "GET", "https://ysl.quickbase.com/db/{{aa.status}}?a=API_EditRecord&rid={{aa.id}}&_fid_20={{aa.name}}&usertoken=abc"));

        var step = pipeline.Steps.Single(s => s.RefId == "ref_ab");
        step.Config["requestMode"].Should().Be("quickbase");
        Render(step.Config["url"]).Should().Be("/db/{{steps.trigger.fid_22}}?a=API_EditRecord&rid={{steps.trigger.fid_3}}&_fid_20={{steps.trigger.fid_20}}".Replace("steps.trigger", Regex.Match(Render(step.Config["url"]), @"\{\{(.*?)\.fid_22\}\}").Groups[1].Value));
        Render(step.Config["url"]).Should().NotContain("usertoken");
        step.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_QUICKBASE_URL_REVIEW" && i.Message.Contains("dbid at run time"));
        NoQuickbaseAnywhere(pipeline);
    }

    [Fact]
    public void MakeRequest_StaticXmlUrl_IsComplete()
    {
        var pipeline = Import(Request("ab", "POST", "https://ysl.quickbase.com/db/bwcrtu9k5?a=API_EditRecord&rid={{aa.id}}&_fid_20=Done"));

        var step = pipeline.Steps.Single(s => s.RefId == "ref_ab");
        Render(step.Config["url"]).Should().StartWith("/db/{table}?a=API_EditRecord&rid=");
        step.IsComplete.Should().BeTrue();
        NoQuickbaseAnywhere(pipeline);
    }

    [Fact]
    public void WebhooksChannel_QuickbaseUrl_NeverBecomesAnHttpCallToQuickbase()
    {
        var pipeline = Import(Request("ab", "POST", "https://ysl.quickbase.com/db/bwcrtu9k5?a=API_AddRecord&_fid_20={{aa.name}}", channel: "webhooks"));

        var step = pipeline.Steps.Single(s => s.RefId == "ref_ab");
        step.Config["requestMode"].Should().Be("quickbase");
        Render(step.Config["url"]).Should().StartWith("/db/{table}?a=API_AddRecord");
        NoQuickbaseAnywhere(pipeline);
    }

    [Fact]
    public void MakeRequest_NoEquivalent_UrlIsEmptiedAndReported()
    {
        var pipeline = Import(Request("ab", "POST", "https://api.quickbase.com/v1/docTemplates/3/generate?tableId=bwcrtu9k5&recordId={{aa.id}}"));

        var step = pipeline.Steps.Single(s => s.RefId == "ref_ab");
        Render(step.Config["url"]).Should().BeEmpty();
        step.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Message.Contains("docTemplates"));
        NoQuickbaseAnywhere(pipeline);
    }

    [Fact]
    public void EmailBody_UnknownPage_IsRemovedButTheRestOfTheTextStays()
    {
        var pipeline = Import(Email("ab", "Hello {{aa.name}}. Page: https://ysl.quickbase.com/db/bwcrtu9k5?a=dbpage&pageID=3 Bye"));

        var step = pipeline.Steps.Single(s => s.RefId == "ref_ab");
        var body = Render(step.Config["body"]);
        body.Should().StartWith("Hello ").And.EndWith(" Bye").And.Contain("Page:  Bye");
        step.IsComplete.Should().BeFalse();
        NoQuickbaseAnywhere(pipeline);
    }

    [Fact]
    public void EmailBody_WithJinjaExpression_LinkStillRewritten()
    {
        var pipeline = Import(Email("ab", "{{aa.name|upper}} https://ysl.quickbase.com/db/bwcrtu9k5?a=dr&rid={{aa.id}}"));

        Render(pipeline.Steps.Single(s => s.RefId == "ref_ab").Config["body"]).Should().Contain("{site}/app/{app}/tables/{table}/records/");
        NoQuickbaseAnywhere(pipeline);
    }
}
