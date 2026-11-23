namespace PowerBase.Domain.Constants;

/// <summary>meta.AppField.MaterializeMode: whether a computed field keeps a stored cache column c_{fid}.</summary>
public static class MaterializeModes
{
    public const byte Off = 0;
    /// <summary>The field is stored in c_{fid} (when its <see cref="MaterializeStates"/> is Ready).</summary>
    public const byte On = 1;
    /// <summary>The field can never be stored (Today()/User() formulas, cycles, encrypted sources, ...): it stays on today's dynamic path.</summary>
    public const byte NotAllowed = 2;
    /// <summary>
    /// An administrator switched the field off on purpose. Decision Q3 stores every Lookup / Summary / Formula field automatically
    /// (<see cref="Off"/> = not decided yet is switched on by the worker); this one is never switched on again until switched on by hand.
    /// </summary>
    public const byte Excluded = 3;
}

/// <summary>meta.AppField.MaterializeState: where the stored column is in its lifecycle.</summary>
public static class MaterializeStates
{
    public const byte None = 0;
    public const byte Building = 1;
    public const byte Ready = 2;
    public const byte Failed = 3;
}
