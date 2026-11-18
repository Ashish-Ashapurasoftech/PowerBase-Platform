using FluentAssertions;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>A source field may carry the label of one of PowerBase's system fields (e.g. "Date Created") while the source renames its own system field
/// ("DateCreated"). PowerBase's seeded system field still holds the default label when the table is created, so such a field has to be created under a
/// temporary label; these tests cover which labels count.</summary>
public class SystemLabelCollisionTests
{
    [Fact]
    public void SystemLabelCollisions_SourceRenamedItsOwnDateCreated_FlagsTheDefaultLabel()
    {
        var table = new PblTable { Name = "File Logs", SystemFieldLabels = new Dictionary<string, string> { ["Date Created"] = "DateCreated" } };

        ImportAppFromPblCommandHandler.SystemLabelCollisions(table).Should().BeEquivalentTo("Date Created");
        ImportAppFromPblCommandHandler.SystemLabelCollisions(table).Contains("date created").Should().BeTrue(); // case-insensitive, like labels
    }

    [Fact]
    public void SystemLabelCollisions_NothingRenamed_FlagsNothing()
    {
        ImportAppFromPblCommandHandler.SystemLabelCollisions(new PblTable { Name = "T" }).Should().BeEmpty();
    }

    [Fact]
    public void SystemLabelCollisions_RenamedToTheSameLabel_FlagsNothing()
    {
        var table = new PblTable { Name = "T", SystemFieldLabels = new Dictionary<string, string> { ["Date Created"] = "Date Created" } };

        ImportAppFromPblCommandHandler.SystemLabelCollisions(table).Should().BeEmpty();
    }

    [Fact]
    public void SystemLabelCollisions_OnlyDefaultSystemLabelsCount()
    {
        var table = new PblTable { Name = "T", SystemFieldLabels = new Dictionary<string, string> { ["Date Modified"] = "ModifiedOn", ["Record ID#"] = "Id" } };

        ImportAppFromPblCommandHandler.SystemLabelCollisions(table).Should().BeEquivalentTo("Date Modified", "Record ID#");
    }
}
