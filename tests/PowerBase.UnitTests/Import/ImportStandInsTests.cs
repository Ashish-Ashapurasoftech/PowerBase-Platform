using FluentAssertions;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Import;

public class ImportStandInsTests
{
    private static AppField Field(string name, string label, int? fid) => new() { Name = name, Label = label, Fid = fid };

    [Fact]
    public void RealSummaryWithAGeneratedName_FoundByItsFieldNumber_NeedsNoStandIn()
    {
        // The real summary is named "hasFileUrl" by PowerBase, the source calls it "HasFileURL": the Name check cannot match, the Fid does.
        var existing = new[] { Field("hasFileUrl", "HasFileURL", 64) };

        ImportStandIns.IsNeeded("SUMMARY_NOT_CREATED", "HasFileURL", "HasFileURL", 64, existing).Should().BeFalse();
    }

    [Fact]
    public void RealLookup_FoundByItsLabel_NeedsNoStandIn()
    {
        var existing = new[] { Field("careManagerName", "Care Manager Name", null) };

        ImportStandIns.IsNeeded("LOOKUP_NOT_CREATED", "Care Manager Name", "Care Manager Name", 12, existing).Should().BeFalse();
    }

    [Fact]
    public void SummaryThatWasReallyNotCreated_StillGetsItsStandIn()
    {
        var existing = new[] { Field("title", "Title", 6) };

        ImportStandIns.IsNeeded("SUMMARY_NOT_CREATED", "Total Hours", "Total Hours", 40, existing).Should().BeTrue();
    }

    [Fact]
    public void FieldAlreadyThereUnderTheSourceName_NeedsNoStandIn()
    {
        var existing = new[] { Field("Legacy Score", "Legacy Score", 9) };

        ImportStandIns.IsNeeded("FORMULA_NEEDS_MANUAL_REVIEW", "Legacy Score", "Legacy Score", 9, existing).Should().BeFalse();
    }

    [Fact]
    public void ReferenceWhoseRelationshipWasCreatedAfterAll_NeedsNoStandIn_ButAMissingOneDoes()
    {
        var existing = new[] { Field("relatedClient", "Related Client", 20) };

        ImportStandIns.IsNeeded("REFERENCE_NOT_CREATED", "Related Client", "Related Client", 20, existing).Should().BeFalse();
        ImportStandIns.IsNeeded("REFERENCE_NOT_CREATED", "Related DSP", "Related DSP", 21, existing).Should().BeTrue();
    }

    [Fact]
    public void FieldNumberAloneDoesNotHideAFormulaStandIn()
    {
        // Only summaries and lookups are recognised by number; a formula placeholder is judged by its name as before.
        var existing = new[] { Field("somethingElse", "Something Else", 77) };

        ImportStandIns.IsNeeded("FORMULA_NEEDS_MANUAL_REVIEW", "Broken Formula", "Broken Formula", 77, existing).Should().BeTrue();
    }
}
