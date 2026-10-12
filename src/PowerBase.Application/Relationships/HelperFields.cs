using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Helper fields (the "(original)" Text field a relationship conversion creates) exist so data is never lost, but are not
/// something a user should see or pick: they are marked in their Settings and left out of every field list the UI is
/// given. Internal reads (<c>IAppFieldRepository.ListByTableAsync</c>) still return them — the grid fallback, decryption
/// and relationship delete need the column.
/// </summary>
public static class HelperFields
{
    /// <summary>The Settings property that marks a helper field (also matched by the Fields-list SQL in AppFieldRepository).</summary>
    public const string HiddenMarkerProperty = "hiddenFromFieldList";

    public static bool IsHidden(AppField field) =>
        field.Settings is { Length: > 0 } s && s.Contains($"\"{HiddenMarkerProperty}\":true", StringComparison.Ordinal);

    /// <summary>The fields to show a user: <paramref name="fields"/> without the helper fields (same instance when there are none).</summary>
    public static IReadOnlyList<AppField> Visible(IReadOnlyList<AppField> fields) =>
        fields.Any(IsHidden) ? fields.Where(f => !IsHidden(f)).ToList() : fields;
}
