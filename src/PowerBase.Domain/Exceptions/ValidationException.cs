namespace PowerBase.Domain.Exceptions;

public class ValidationException : DomainException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>Builds the top-level Message from the individual field violation messages
    /// (e.g. "'Email' must be unique — this value is already in use.") rather than a generic
    /// "One or more validation errors occurred." — callers (frontend save-error banners/toasts)
    /// display Message directly, so it needs to already be the readable, actionable text; the
    /// Errors dictionary remains available for callers that want to attribute a message to a
    /// specific field.</summary>
    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("VALIDATION_ERROR", BuildMessage(errors))
    {
        Errors = errors;
    }

    private static string BuildMessage(IReadOnlyDictionary<string, string[]> errors)
    {
        var messages = errors.Values.SelectMany(m => m).ToList();
        return messages.Count > 0 ? string.Join(" ", messages) : "One or more validation errors occurred.";
    }
}
