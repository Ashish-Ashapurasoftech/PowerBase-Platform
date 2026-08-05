using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

public class QblYamlTagTests
{
    [Fact]
    public void Deserialize_RefTag_ResolvesToScopeBag()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $Field_Add_File:
            Type: QB::Field::URL::Formula
            Properties:
              FieldRelationship:
                Source: !Ref
                  Field: $Field_Record_ID
                Target: !Ref
                  Table: $Table_Files
                  Field: $Field_Related_Folder
        """;

        var document = QblSerializer.Deserialize(yaml);

        var node = document.Resources["$Field_Add_File"];
        var relationship = (Dictionary<string, object?>)node.Properties["FieldRelationship"]!;

        var source = (QblRef)relationship["Source"]!;
        source["Field"].Should().Be("$Field_Record_ID");

        var target = (QblRef)relationship["Target"]!;
        target["Table"].Should().Be("$Table_Files");
        target["Field"].Should().Be("$Field_Related_Folder");
    }

    [Fact]
    public void Deserialize_BadRefTag_ResolvesToMessageMarker()
    {
        // Confirmed real shape: !BadRef is a scalar string, not a mapping.
        const string yaml = """
        Version: '0.12'
        Resources:
          $FormElement_6:
            Type: QB::Form::Element::Field
            Properties:
              Field: !BadRef "Referenced resource does not exist."
        """;

        var document = QblSerializer.Deserialize(yaml);

        var node = document.Resources["$FormElement_6"];
        var field = (QblBadRef)node.Properties["Field"]!;

        field.Message.Should().Be("Referenced resource does not exist.");
    }

    [Fact]
    public void Deserialize_VarTag_ResolvesAgainstParameterDefinitions()
    {
        // Confirmed real shape: !Var {Name: <key>} resolves against a top-level
        // ParameterDefinitions map, undocumented publicly but present in real exports.
        const string yaml = """
        Version: '0.12'
        ParameterDefinitions:
          App_Name_1:
            Type: String
            Description: Parameter for QB::Application-Name
            Value: YSL Dev Docs
        Resources:
          $App_YSL_Dev_Docs:
            Type: QB::Application
            Properties:
              Name: !Var
                Name: App_Name_1
        """;

        var document = QblSerializer.Deserialize(yaml);

        var node = document.Resources["$App_YSL_Dev_Docs"];
        node.Properties["Name"].Should().Be("YSL Dev Docs");
    }

    [Fact]
    public void Deserialize_NestedTypedChildren_BecomeResourceNodes()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Name
        """;

        var document = QblSerializer.Deserialize(yaml);

        var app = document.Resources["$App_Test"];
        var tables = app.ChildMap("Tables");
        tables.Should().ContainKey("$Table_Clients");

        var table = tables["$Table_Clients"];
        table.Type.Should().Be("QB::Table");
        var fields = table.ChildMap("Fields");
        fields["$Field_Name"].Type.Should().Be("QB::Field::Text");
        fields["$Field_Name"].Properties["Label"].Should().Be("Name");
    }

    [Fact]
    public void Deserialize_CorruptedCodePageBlock_RecoversByStrippingPages()
    {
        // Confirmed real corruption: a QB::CodePage's RawCode literal block can contain a line
        // with less indentation than the block requires (raw minified JS, not YAML structure),
        // which breaks strict parsing with "did not find expected key". CodePages have no
        // PowerBase equivalent regardless, so QblSerializer recovers by stripping the whole
        // Pages block on a parse failure - this reproduces that shape at minimal scale and
        // confirms the Table before it and the Variables after it both survive intact.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
            Pages:
              Properties:
                RoleDefaults: {}
              Resources:
                $Page_broken:
                  Type: QB::CodePage
                  Properties:
                    RawCode: |-
                      some.minified.js(function(a){return a}
        ","'":"'"},brokenContinuationLine();
                    Name: broken.js
            Variables:
              $Variable_1:
                Type: QB::Application::Variable
                Properties:
                  Name: 1
                  Value: hello
        """;

        var document = QblSerializer.Deserialize(yaml);

        var app = document.Resources["$App_Test"];
        var tables = app.ChildMap("Tables");
        tables["$Table_Clients"].Properties["Name"].Should().Be("Clients");

        var variables = app.ChildMap("Variables");
        variables["$Variable_1"].Properties["Value"].Should().Be("hello");

        // The corrupted Pages block itself is gone - not partially recovered.
        app.Children.Should().NotContainKey("Pages");
    }
}
