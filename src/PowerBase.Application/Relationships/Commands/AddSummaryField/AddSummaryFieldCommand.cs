using PowerBase.Application.Reports;

namespace PowerBase.Application.Relationships.Commands.AddSummaryField;

/// <summary>
/// Adds a Summary field to an existing relationship's parent table. <paramref name="Function"/>
/// is one of <see cref="Domain.FieldSettings.SummaryFunctions"/> (Count/Exists/Sum/Avg/Min/Max);
/// <paramref name="TargetFid"/> is required for Sum/Avg/Min/Max and null for Count/Exists.
/// <paramref name="MatchingCriteria"/> optionally restricts which child records are summarized.
/// </summary>
public record AddSummaryFieldCommand(
    Guid RelationshipPublicId,
    string Name,
    string? Label,
    string Function,
    int? TargetFid,
    FilterGroup? MatchingCriteria);
