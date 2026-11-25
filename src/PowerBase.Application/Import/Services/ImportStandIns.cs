using PowerBase.Domain.Entities;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Decides whether an import still has to create a stand-in (placeholder) field for something it logged a warning about.
///
/// A note or warning about a summary, lookup or formula ("Quickbase shows this summary as a link to the child records") does not mean the field
/// failed: the real field is usually there. Creating a stand-in anyway left a second field next to every such summary or lookup, with the same
/// label plus " (2)" and the next free field number, and anything bound by name (a rule, a form element, a report column or filter) could land
/// on that empty copy.
/// </summary>
public static class ImportStandIns
{
    /// <summary>True when no real field stands for the wanted one yet, so a stand-in must be created.</summary>
    /// <param name="code">The stand-in's reason code (REFERENCE_NOT_CREATED, LOOKUP_NOT_CREATED, SUMMARY_NOT_CREATED, FORMULA_NEEDS_MANUAL_REVIEW...).</param>
    /// <param name="name">The field Name the source gave it.</param>
    /// <param name="label">The label wanted for it (null = the name).</param>
    /// <param name="fid">The field number the source gave it, if kept.</param>
    /// <param name="existing">The fields the table already holds.</param>
    public static bool IsNeeded(string code, string name, string? label, int? fid, IReadOnlyCollection<AppField> existing)
    {
        // The field exists already under the source's own name.
        if (existing.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;

        var wantedLabel = (label ?? name).Trim();
        var sameLabel = wantedLabel.Length > 0
            && existing.Any(f => string.Equals((f.Label ?? "").Trim(), wantedLabel, StringComparison.OrdinalIgnoreCase));

        switch (code)
        {
            // A reference field whose relationship was created after all (only part of it failed).
            case "REFERENCE_NOT_CREATED":
                return !sameLabel;

            // A lookup or summary is created with a generated field Name, so the Name check above never matches it; it is found by the field number
            // the source gave it (kept on the real field) or by its label (labels are unique on a table).
            case "LOOKUP_NOT_CREATED":
            case "SUMMARY_NOT_CREATED":
                return !(sameLabel || (fid is { } wanted && existing.Any(f => f.Fid == wanted)));

            default:
                return true;
        }
    }
}
