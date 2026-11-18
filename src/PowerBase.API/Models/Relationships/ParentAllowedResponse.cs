namespace PowerBase.API.Models.Relationships;

public record ParentAllowedResponse(bool Allowed);

/// <summary>What a picked parent record fills into the form fields its Reference's dropdown depends on: Fid (as text) →
/// value. Empty when the Reference's "show all until chosen" is off.</summary>
public record ControllingFillResponse(IReadOnlyDictionary<string, string> Values);
