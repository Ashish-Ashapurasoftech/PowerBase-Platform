using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Helper fields (the "(original)" Text field a relationship conversion creates) exist so data is never lost, but are not
/// something a user should see or pick: they carry <see cref="AppField.IsHidden"/> and are left out of every field list the
/// UI is given. Internal reads (<c>IAppFieldRepository.ListByTableAsync</c>) still return them — the grid fallback,
/// decryption and relationship delete need the column.
/// </summary>
public static class HelperFields
{
    public static bool IsHidden(AppField field) => field.IsHidden;

    /// <summary>The fields to show a user: <paramref name="fields"/> without the helper fields (same instance when there are none).</summary>
    public static IReadOnlyList<AppField> Visible(IReadOnlyList<AppField> fields) =>
        fields.Any(IsHidden) ? fields.Where(f => !IsHidden(f)).ToList() : fields;
}
