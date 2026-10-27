using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>
/// Runs the converter and validator over a real Quickbase export when one is available on this machine: set
/// <c>POWERBASE_CARES_QBL</c> to its path (or leave a <c>cares.qbl</c> / <c>cares.qbl.txt</c> on the Desktop). Without a file
/// the tests do nothing, so CI is unaffected. The full database import is exercised through the API (see
/// docs/QBL_IMPORT_RERUN_REPORT.md); these tests guard the part that needs no database.
/// </summary>
public class CaresRealFileTests
{
    private static string? FindFile()
    {
        var fromEnv = Environment.GetEnvironmentVariable("POWERBASE_CARES_QBL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var candidate in new[] { "cares.qbl", "cares.qbl.txt" })
        {
            var path = Path.Combine(desktop, candidate);
            if (File.Exists(path)) return path;
        }

        // OneDrive-redirected desktops report a different folder than the user profile's Desktop.
        var oneDrive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Desktop", "cares.qbl.txt");
        return File.Exists(oneDrive) ? oneDrive : null;
    }

    [Fact]
    public void RealExport_ConvertsToAValidDocumentWithAllTablesAndRelationships()
    {
        var path = FindFile();
        if (path is null) return;

        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(File.ReadAllText(path)));
        var doc = result.Document;

        doc.Tables.Should().HaveCountGreaterThan(90);
        doc.Tables.Sum(t => t.Fields.Count).Should().BeGreaterThan(1000);
        doc.Relationships.Should().HaveCountGreaterThan(150);
        doc.Roles.Should().NotBeEmpty();
        doc.Forms.Should().NotBeEmpty();

        var validation = new PblValidator().Validate(doc);
        validation.Issues.Where(i => i.Severity == PblIssueSeverity.Error).Should().BeEmpty(
            string.Join("; ", validation.Issues.Where(i => i.Severity == PblIssueSeverity.Error).Take(5).Select(i => i.Code + " " + i.Message)));
    }

    [Fact]
    public void RealExport_EveryRoleRecordFilterPointsAtAFieldDeclaredInTheDocument()
    {
        var path = FindFile();
        if (path is null) return;

        var doc = QblToPblConverter.Convert(QblSerializer.Deserialize(File.ReadAllText(path))).Document;

        // Security-sensitive: a record filter must never refer to a field that is not in the document (it would be dropped
        // and the role could see more than it should). The converter must have removed or neutralised any such filter.
        foreach (var role in doc.Roles)
            foreach (var permission in role.TablePermissions.Where(p => p.RecordFilter is not null))
            {
                var table = doc.Tables.Single(t => t.LogicalRef == permission.TableRef);
                // Lookups live on the child table, summaries on the parent table, both declared by the relationship.
                var names = table.Fields.Select(f => f.Name)
                    .Concat(doc.Relationships.Where(r => r.ChildTableRef == table.LogicalRef).SelectMany(r => r.Lookups.Select(l => l.Name)))
                    .Concat(doc.Relationships.Where(r => r.ParentTableRef == table.LogicalRef).SelectMany(r => r.Summaries.Select(m => m.Name)))
                    .Concat(doc.Relationships.Where(r => r.ChildTableRef == table.LogicalRef).Select(r => r.ReferenceFieldName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                Conditions(permission.RecordFilter!).Should().OnlyContain(c => names.Contains(c.FieldName),
                    $"role '{role.Name}' on '{table.Name}' filters on fields that exist");
            }
    }

    private static IEnumerable<PblFilterCondition> Conditions(PblFilterGroup group) =>
        group.Conditions.Concat(group.Groups.SelectMany(Conditions));
}
