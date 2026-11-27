using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Review notes of the real import that the converter now handles exactly: Jinja if-blocks, Quickbase's numbering of
/// duplicate field keys, filters on a person detail / file name, and conditions on a field's previous value.</summary>
public class QblPipelineReviewFixesTests
{
    private const string App = """
        Version: 0.12
        Resources:
          $App_Docs:
            Type: QB::Application
            Properties: { Name: Docs }
            Tables:
              $Table_T:
                Type: QB::Table
                Properties: { Name: T }
                Fields:
                  $F3: { Type: QB::Field::RecordID, Id: 3, Properties: { Label: Record ID# } }
                  $F1: { Type: QB::Field::DateCreated, Id: 1, Properties: { Label: Date Created } }
                  $F6: { Type: QB::Field::Text, Id: 6, Properties: { Label: Name } }
                  $F7: { Type: QB::Field::Checkbox, Id: 7, Properties: { Label: Flag } }
                  $F8: { Type: QB::Field::User, Id: 8, Properties: { Label: Owner User } }
                  $F9: { Type: QB::Field::FileAttachment, Id: 9, Properties: { Label: Doc } }
                  $F10: { Type: QB::Field::Text, Id: 10, Properties: { Label: Status } }
                  $F12: { Type: QB::Field::Text, Id: 12, Properties: { Label: Note } }
                  $F15: { Type: QB::Field::Text, Id: 15, Properties: { Label: "@Note" } }
                  $F20: { Type: QB::Field::Text, Id: 20, Properties: { Label: Result } }
                  $F25: { Type: QB::Field::Text, Id: 25, Properties: { Label: DSSWeek } }
                  $F26: { Type: QB::Field::Text, Id: 26, Properties: { Label: DSS Week } }
                  $F27: { Type: QB::Field::Text::Formula, Id: 27, Properties: { Label: ClientDay } }
                  $F28: { Type: QB::Field::Text, Id: 28, Properties: { Label: Client Day } }
          $PipelineChannelAccount_Quickbase:
            Type: QB::PipelineChannelAccount
            Properties: { Channel: quickbase, AccountType: tokenless }
        """;

    private static string Trigger(string extra = "") => $$"""
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: a
                  Name: On New Event
                  PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                  Channel: { Resource: record, Id: quickbase, Selector: on_new_event }
        {{extra}}
                InputsMeta:
                  $Meta:
                    Type: QB::PipelineStep::InputsMeta
                    Properties: { On_Add_Record: true, On_Modify_Record: true }
                    PipelineStep_DataSources:
                      $Ds: { Type: QB::PipelineStep::DataSource::Table, Properties: { Table: !Ref { App: $App_Docs, Table: $Table_T }, Name: table } }
        """;

    private static string Create(string inputs) => $$"""
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: b
                  Name: Create Record
                  PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                  Channel: { Resource: record, Id: quickbase, Selector: create }
                  Inputs: {{inputs}}
                InputsMeta:
                  $Meta:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $Ds: { Type: QB::PipelineStep::DataSource::Table, Properties: { Table: !Ref { App: $App_Docs, Table: $Table_T }, Name: table } }
        """;

    private static PblPipeline Convert(params string[] steps)
    {
        var content = App.Replace("\r", "") + "\n  $Pipeline_P:\n    Type: QB::Pipeline\n    Properties: { Name: P }\n    Steps:\n"
                      + string.Join("\n", steps).Replace("\r", "");
        return QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(content), content).Single();
    }

    private static string Render(object? v) => v switch
    {
        PblPipelineTextValue t => string.Concat(t.Parts.Select(Render)),
        PblPipelineFieldValue f => $"fid_{f.SourceId}" + (f.Part is null ? "" : "." + f.Part),
        null => "",
        _ => v.ToString()!,
    };

    private static string Mapping(PblPipelineStep step, int fid) =>
        Render(((List<object?>)step.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>()
            .Single(m => ((PblPipelineFieldValue)m["field"]!).SourceId == fid)["value"]);

    [Fact]
    public void JinjaIfElifElse_BecomesAnIfFormula()
    {
        var p = Convert(Trigger(), Create("""{ Result: "{% if a.flag %}Yes {{a.name}}{% elif a.status == 'Open' %}Open{% else %}No{% endif %}" }"""));

        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
        var value = Mapping(p.Steps[1], 20);
        value.Should().StartWith("@formula|");
        value.Should().Contain("If(").And.Contain("Open").And.Contain("Yes ");
    }

    [Fact]
    public void JinjaIfWithoutElse_PrintsNothingOtherwise()
    {
        var p = Convert(Trigger(), Create("""{ Result: "{% if a.name %} {{a.status}} {% endif %}" }"""));

        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
        Mapping(p.Steps[1], 20).Should().Contain("If(");
    }

    [Fact]
    public void JinjaSetOrFor_IsStillReported()
    {
        var p = Convert(Trigger(), Create("""{ Result: "{% set x = a.name %}{{x}}" }"""));

        p.Issues.Should().Contain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.Message.Contains("set"));
    }

    [Fact]
    public void DuplicateLabelKeys_LowestFieldIdGetsThePlainKey_NextGetsKey0()
    {
        // "Note" (12) and "@Note" (15) both give the key "note": Quickbase calls the first note and the second note_0.
        var p = Convert(Trigger(), Create("""{ Name: "{{a.note}}|{{a.note_0}}" }"""));

        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
        Mapping(p.Steps[1], 6).Should().Be("{{steps.ref_a.fid_12}}|{{steps.ref_a.fid_15}}");
    }

    [Fact]
    public void TriggerQuerySetting_InQuickbaseQuerySyntax_BecomesTheFilter()
    {
        var trigger = Trigger().Replace("Properties: { On_Add_Record: true, On_Modify_Record: true }",
            "Properties: { On_Add_Record: true, On_Modify_Record: true, Query: \"{7.EX.1}AND{'10'.CT.'Open'}AND{12.EX.''}\" }");
        var p = Convert(trigger);

        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_SETTING_NOT_IMPORTED");
        var json = System.Text.Json.JsonSerializer.Serialize(p.Steps[0].Config["filterGroups"]);
        json.Should().Contain("is_true").And.Contain("contains").And.Contain("Open").And.Contain("is-empty");
    }

    [Fact]
    public void TriggerQuerySetting_WithAnOperatorWithoutEquivalent_IsStillReported()
    {
        var trigger = Trigger().Replace("Properties: { On_Add_Record: true, On_Modify_Record: true }",
            "Properties: { On_Add_Record: true, On_Modify_Record: true, Query: \"{10.TV.'x'}\" }");
        Convert(trigger).Issues.Should().Contain(i => i.Code == "PIPELINE_SETTING_NOT_IMPORTED" && i.Message.Contains("Query"));
    }

    [Fact]
    public void SetFieldInput_SkipsACalculatedFieldWithTheSameKey()
    {
        // "ClientDay" (27, formula) and "Client Day" (28, text): a step that sets fields lists only writable ones, so Client_Day is 28;
        // a token that reads the trigger record still numbers every field (client_day = 27).
        var p = Convert(Trigger(), Create("""{ Client_Day: "{{a.client_day}}" }"""));

        Mapping(p.Steps[1], 28).Should().Be("{{steps.ref_a.fid_27}}");
        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_COMPUTED_FIELD_WRITE");
    }

    [Fact]
    public void CamelCaseAndSpacedLabels_ShareAKey_InFieldIdOrder()
    {
        // "DSSWeek" (25) and "DSS Week" (26) are both dss_week in Quickbase: dss_week is 25, dss_week_0 is 26.
        var p = Convert(Trigger(), Create("""{ Name: "{{a.dss_week}}|{{a.dss_week_0}}" }"""));

        Mapping(p.Steps[1], 6).Should().Be("{{steps.ref_a.fid_25}}|{{steps.ref_a.fid_26}}");
    }

    [Fact]
    public void Filter_OnAPersonDetailOfAUserField_AndOnAFileName()
    {
        var filters = """
                  Filters:
                    Type: composite-expression
                    Notation: postfix
                    Expression:
                      - { Type: relational-expression, Field: owner_user.email, Operator: equals, Value: 'a@ysl.com' }
                      - { Type: relational-expression, Field: doc.file_name, Operator: equals, Value: 'x.pdf' }
                      - { Type: logical-operator, Operator: AND }
        """;
        var p = Convert(Trigger(filters));

        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_FIELD_NOT_FOUND");
        var json = System.Text.Json.JsonSerializer.Serialize(p.Steps[0].Config) + string.Join("|", p.Issues.Select(i => i.Code + ":" + i.Message));
        json.Should().Contain("\"Part\":\"email\"").And.Contain("\"SourceId\":9");
    }

    [Fact]
    public void Condition_OnThePreviousValue_UsesTheMetadataToken()
    {
        var condition = """
              - Type: QB::PipelineStep::Condition
                Properties:
                  Type: branch
                  Name: Condition
                  Filters:
                    Type: composite-expression
                    Notation: postfix
                    Expression:
                      - { Type: relational-expression, Target: a, Field: metadata.a.record.previous.status, Operator: equals, Value: Open }
                Then:
                  - Type: QB::PipelineStep::Stop
                    Properties: { Type: stop, Name: Stop }
        """;
        var p = Convert(Trigger(), condition);

        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
        var rules = System.Text.Json.JsonSerializer.Serialize(p.Steps[1].Config);
        rules.Should().Contain("_metadata.record.previous.");
    }

    [Fact]
    public void CamelCaseCallArgument_IsReadWithItsSnakeCaseKey()
    {
        var p = Convert("""
              - Type: QB::PipelineStep::CloudpipesMeta
                Properties:
                  Id: a
                  Name: Pipeline Called
                  Channel: { Resource: pipeline, Id: cloudpipes-meta, Selector: on_called }
                InputsMeta:
                  $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Call_Definition: 'push(deploymentId)' } }
        """, Create("""{ Name: "{{a.deployment_id}}" }"""));

        p.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
        Mapping(p.Steps[1], 6).Should().Be("{{steps.ref_a.deploymentId}}");
    }
}
