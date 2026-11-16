using FluentAssertions;
using PowerBase.Application.Import.Reporting;

namespace PowerBase.UnitTests.Import.Reporting;

public class ImportIssueClassifierTests
{
    [Fact]
    public void Classify_ErrorSeverity_IsNeverDowngraded_EvenForACosmeticCode()
    {
        var (severity, _) = ImportIssueClassifier.Classify("QBL_APP_SEARCHABLE_NOT_MAPPED", "anything", ImportIssueSeverity.Error);
        severity.Should().Be(ImportIssueSeverity.Error);
    }

    [Fact]
    public void Classify_KnownCosmeticCode_DowngradesWarningToInformation()
    {
        var (severity, action) = ImportIssueClassifier.Classify("QBL_FIELD_SETTING_NOT_CARRIED", "A setting was left at the default.", ImportIssueSeverity.Warning);
        severity.Should().Be(ImportIssueSeverity.Information);
        action.Should().BeNull(); // "left at the default" needs no action
    }

    [Fact]
    public void Classify_UnknownCode_KeepsTheSourceSeverity()
    {
        var (severity, _) = ImportIssueClassifier.Classify("SOME_OTHER_CODE", "Something happened.", ImportIssueSeverity.Warning);
        severity.Should().Be(ImportIssueSeverity.Warning);
    }

    [Theory]
    [InlineData("FORMULA_NeedsManualReview", "Unknown function 'FOO'.", "formula")]
    [InlineData("QBL_LOOKUP_MISSING_RELATIONSHIP", "Lookup's relationship was not found.", "re-import")]
    [InlineData("QBL_DUPLICATE_FIELD_NAME", "Two fields share a name.", "Rename")]
    [InlineData("QBL_UNSUPPORTED_FIELD_TYPE", "Field type is not supported.", "Quickbase feature")]
    public void RecommendedAction_MatchesTheRightCategory(string code, string message, string expectedSubstring)
    {
        var (_, action) = ImportIssueClassifier.Classify(code, message, ImportIssueSeverity.Warning);
        action.Should().NotBeNull();
        action!.Should().Contain(expectedSubstring);
    }

    [Fact]
    public void RecommendedAction_NoKeywordMatch_StillGivesAGenericNextStepRatherThanNull()
    {
        var (_, action) = ImportIssueClassifier.Classify("SOME_UNRECOGNIZED_CODE", "Something unusual happened.", ImportIssueSeverity.Warning);
        action.Should().Be("Review this item after import; no automatic fix is available for it.");
    }

    [Theory]
    [InlineData("Formula field 'Activity Backup' has no expression in the source.", "Activity Backup")]
    [InlineData("No quotes in this message at all.", null)]
    public void ExtractQuotedName_PullsTheFirstSingleQuotedSegment(string message, string? expected)
    {
        ImportIssueClassifier.ExtractQuotedName(message).Should().Be(expected);
    }
}
