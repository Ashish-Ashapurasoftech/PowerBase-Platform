using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>A Quickbase file lists a table's report links as fields of their own, each with its own number. The converter keeps them
/// (and their numbers) so an import matches the source field list and field ids.</summary>
public class QblReportLinksTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private static string DepartmentsAndEmployees(string departmentExtraFields) => $"""
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Departments:
                Type: QB::Table
                Properties:
                  Name: Departments
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                  $Field_Dept_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Department Name
        {Indent(departmentExtraFields, 10)}
                Relationships:
                  $Relationship_to_Departments_1:
                    Type: QB::Relationship::Parent
              $Table_Employees:
                Type: QB::Table
                Properties:
                  Name: Employees
                Fields:
                  $Field_Employee_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Employee Name
                  $Field_Related_Department:
                    Type: QB::Field::Reference
                    Id: 7
                    Properties:
                      Label: Related Department
                      Reference:
                        Relationship: !Ref
                          Relationship: $Relationship_to_Departments
                Relationships:
                  $Relationship_to_Departments:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Departments
                        Relationship: $Relationship_to_Departments_1
        """;

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n").Trim('\n').Split('\n');
        var min = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Trim().Length == 0 ? "" : pad + l[min..]));
    }

    private const string RecordsLink = """
        $Field_Employee_records:
          Type: QB::Field::ReportLink
          Id: 9
          Properties:
            Label: Employee records
            LinkText: Employee records
            Comments: Lists the employees
            FieldRelationship:
              Source: !Ref
                Field: $Field_Record_ID
              Target: !Ref
                Table: $Table_Employees
                Field: $Field_Related_Department
        """;

    [Fact]
    public void Convert_ReportLinkThroughTheReferenceField_BecomesTheRelationshipsReportLinkOnItsSourceNumber()
    {
        var doc = Convert(DepartmentsAndEmployees(RecordsLink)).Document;

        var rel = doc.Relationships.Should().ContainSingle().Subject;
        rel.ExplicitLinks.Should().BeTrue();
        rel.ReportLink.Should().NotBeNull();
        rel.ReportLink!.SourceId.Should().Be(9);
        rel.ReportLink.Label.Should().Be("Employee records");
        rel.ReportLink.LinkText.Should().Be("Employee records");
        rel.ReportLink.Description.Should().Be("Lists the employees");
        doc.Tables.SelectMany(t => t.ReportLinks).Should().BeEmpty();
    }

    [Fact]
    public void Convert_ReportLinkMatchingOnTheKeyFieldInsteadOfRecordId_IsStillTheRelationshipsReportLink()
    {
        // A table with its own key field links on that field in Quickbase; a PowerBase reference always holds the parent's record id.
        var doc = Convert(DepartmentsAndEmployees(RecordsLink.Replace("Field: $Field_Record_ID", "Field: $Field_Dept_Name"))).Document;

        doc.Relationships.Single().ReportLink!.SourceId.Should().Be(9);
        doc.Tables.SelectMany(t => t.ReportLinks).Should().BeEmpty();
    }

    [Fact]
    public void Convert_RelationshipWithoutAReportLinkInTheFile_IsMarkedExplicitSoNoneIsGenerated()
    {
        var doc = Convert(DepartmentsAndEmployees("")).Document;

        var rel = doc.Relationships.Single();
        rel.ExplicitLinks.Should().BeTrue();
        rel.ReportLink.Should().BeNull();
    }

    [Fact]
    public void Convert_SecondReportLinkToTheSameChild_IsKeptAsAStandaloneLinkOnItsOwnNumber()
    {
        var second = """
            $Field_Employee_records_2:
              Type: QB::Field::ReportLink
              Id: 21
              Properties:
                Label: Employee records 2
                FieldRelationship:
                  Source: !Ref
                    Field: $Field_Record_ID
                  Target: !Ref
                    Table: $Table_Employees
                    Field: $Field_Related_Department
            """;
        var doc = Convert(DepartmentsAndEmployees(RecordsLink + "\n" + second)).Document;

        doc.Relationships.Single().ReportLink!.SourceId.Should().Be(9);
        var standalone = doc.Tables.Single(t => t.Name == "Departments").ReportLinks.Should().ContainSingle().Subject;
        standalone.SourceId.Should().Be(21);
        standalone.TargetTableRef.Should().Be("$Table_Employees");
        standalone.TargetFieldName.Should().Be("Related Department");
        standalone.SourceFieldName.Should().BeNull("it matches on Record ID#");
    }

    [Fact]
    public void Convert_ReportLinkOnAnOrdinaryFieldAndSourceField_IsStandaloneWithBothFieldNames()
    {
        var link = """
            $Field_Same_Name:
              Type: QB::Field::ReportLink
              Id: 30
              Properties:
                Label: People with the same name
                FieldRelationship:
                  Source: !Ref
                    Field: $Field_Dept_Name
                  Target: !Ref
                    Table: $Table_Employees
                    Field: $Field_Employee_Name
            """;
        var doc = Convert(DepartmentsAndEmployees(link)).Document;

        doc.Relationships.Single().ReportLink.Should().BeNull();
        var standalone = doc.Tables.Single(t => t.Name == "Departments").ReportLinks.Should().ContainSingle().Subject;
        standalone.SourceId.Should().Be(30);
        standalone.TargetFieldName.Should().Be("Employee Name");
        standalone.SourceFieldName.Should().Be("Department Name");
    }

    [Fact]
    public void Convert_ReportLinkWhoseTargetWasDeletedInTheSource_IsReportedNotGuessed()
    {
        var link = """
            $Field_Broken:
              Type: QB::Field::ReportLink
              Id: 40
              Properties:
                Label: Broken link
                FieldRelationship:
                  Source: !Ref
                    Field: $Field_Record_ID
                  Target: !BadRef "Referenced resource does not exist."
            """;
        var result = Convert(DepartmentsAndEmployees(link));

        result.Document.Relationships.Single().ReportLink.Should().BeNull();
        result.Document.Tables.SelectMany(t => t.ReportLinks).Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_REPORT_LINK_TARGET_UNAVAILABLE" && i.Message.Contains("no longer exists"));
    }
}
