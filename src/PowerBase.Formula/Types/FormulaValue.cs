namespace PowerBase.Formula.Types;

/// <summary>
/// An immutable, type-tagged formula value. Construct via the typed factories;
/// read via the typed accessors. Follows Quickbase null semantics: Text and Bool
/// are never null (empty text is <c>""</c>, empty checkbox is <c>false</c>), all
/// other types may be null and carry their would-be type so type-checking still
/// works (<see cref="IsNull"/>).
/// </summary>
public readonly struct FormulaValue
{
    private readonly object? _value;

    private FormulaValue(FormulaType type, bool isNull, object? value)
    {
        Type = type;
        IsNull = isNull;
        _value = value;
    }

    public FormulaType Type { get; }

    public bool IsNull { get; }

    /// <summary>The boxed underlying value (string, decimal, bool, DateOnly, …), or null.</summary>
    public object? Raw => _value;

    // ── Factories ───────────────────────────────────────────────────────────
    public static FormulaValue Text(string? value) => new(FormulaType.Text, false, value ?? string.Empty);
    public static FormulaValue Number(decimal value) => new(FormulaType.Number, false, value);
    public static FormulaValue Bool(bool value) => new(FormulaType.Bool, false, value);
    public static FormulaValue Date(DateOnly value) => new(FormulaType.Date, false, value);
    public static FormulaValue DateTime(DateTime value) => new(FormulaType.DateTime, false, value);
    public static FormulaValue Duration(TimeSpan value) => new(FormulaType.Duration, false, value);
    public static FormulaValue User(UserRef? value)
        => value is null ? Null(FormulaType.User) : new(FormulaType.User, false, value);
    public static FormulaValue UserList(IReadOnlyList<UserRef> value)
        => new(FormulaType.UserList, false, value ?? Array.Empty<UserRef>());

    /// <summary>A typed null of the given type. Text/Bool are normalised to ""/false (never null).</summary>
    public static FormulaValue Null(FormulaType type) => type switch
    {
        FormulaType.Text => Text(string.Empty),
        FormulaType.Bool => Bool(false),
        _ => new(type, true, null),
    };

    // ── Accessors ───────────────────────────────────────────────────────────
    public string AsText() => IsNull ? string.Empty : (string)_value!;
    public decimal AsNumber() => (decimal)_value!;
    public bool AsBool() => !IsNull && (bool)_value!;
    public DateOnly AsDate() => (DateOnly)_value!;
    public DateTime AsDateTime() => (DateTime)_value!;
    public TimeSpan AsDuration() => (TimeSpan)_value!;
    public UserRef AsUser() => (UserRef)_value!;
    public IReadOnlyList<UserRef> AsUserList() => (IReadOnlyList<UserRef>)_value!;

    public override string ToString() => IsNull ? $"null({Type})" : $"{Type}:{_value}";
}
