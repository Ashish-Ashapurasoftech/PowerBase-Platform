using PowerBase.Application.Reports;

namespace PowerBase.Application.Relationships.Commands.UpdateSummaryField;

/// <summary>
/// Edits an existing Summary field of a relationship: its label, calculation
/// (<paramref name="Function"/> / <paramref name="TargetFid"/>), matching criteria and Combined
/// Text options. Same rules as <see cref="AddSummaryField.AddSummaryFieldCommand"/>; a null or
/// empty <paramref name="MatchingCriteria"/> clears the criteria (every related record counts).
/// <paramref name="CommitMessage"/> is the reason recorded on the field's new version; a default
/// is used when blank.
/// </summary>
public record UpdateSummaryFieldCommand(
    Guid RelationshipPublicId,
    Guid FieldPublicId,
    string Label,
    string Function,
    int? TargetFid,
    FilterGroup? MatchingCriteria,
    CombinedTextOptions? CombinedText = null,
    string? CommitMessage = null);
