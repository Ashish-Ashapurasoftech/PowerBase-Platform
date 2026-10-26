namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>
/// Collects the choices for a field becoming a SingleSelect: every distinct converted value, in first-seen
/// order, capped at <see cref="MaxChoices"/>. Values past the cap are still stored on their records; the
/// preview flags them via <see cref="Overflowed"/>.
/// </summary>
public sealed class SelectChoiceBuilder
{
    public const int MaxChoices = 500;

    private readonly List<string> _choices = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public SelectChoiceBuilder(IEnumerable<string>? existingChoices = null)
    {
        foreach (var choice in existingChoices ?? []) Add(choice);
    }

    public IReadOnlyList<string> Choices => _choices;

    /// <summary>Distinct values that didn't fit under the cap.</summary>
    public int OverflowCount { get; private set; }

    public bool Overflowed => OverflowCount > 0;

    public void Add(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !_seen.Add(value)) return;
        if (_choices.Count >= MaxChoices)
        {
            OverflowCount++;
            return;
        }
        _choices.Add(value);
    }
}
