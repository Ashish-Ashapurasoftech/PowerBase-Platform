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

/// <summary>
/// meta.AppField.SearchIndexState: whether a stored field's values are fully in the Azure AI Search index. Separate from
/// <see cref="MaterializeStates"/>: Ready there means c_{fid} may be read in SQL, whatever the index holds. A field may be read from the
/// index only when this is <see cref="Indexed"/> AND AppField.SearchIndexVersion equals its MaterializeVersion (a rebuild bumps the version).
/// </summary>
public static class SearchIndexStates
{
    public const byte None = 0;
    /// <summary>A field job carrying this version's values was sent and has not finished.</summary>
    public const byte Indexing = 1;
    /// <summary>The field job for SearchIndexVersion finished: every record's value is in the index.</summary>
    public const byte Indexed = 2;
    /// <summary>The field job gave up; reads stay on SQL until it is retried.</summary>
    public const byte Failed = 3;
}

/// <summary>
/// Decision Q4 (2026-11-26): every Lookup / Summary / Formula field goes to the search index, so it is always Searchable AND Filterable.
/// Locked: an admin unticking either is ignored (create, update, restore, import and type change all apply this). Hidden helper fields
/// stay out of every user-facing list and keep their flags. Migration 102 applied the rule to the fields that existed before.
/// </summary>
public static class ComputedFieldSearch
{
    /// <summary>True when the field's Searchable and Filterable flags are forced on.</summary>
    public static bool IsLocked(string? typeCode, bool isHidden = false, bool isSystem = false) =>
        !isHidden && !isSystem && typeCode is not null && PhysicalNaming.IsMaterializableTypeCode(typeCode);

    /// <summary>The flag as it is stored: forced on for a locked field, otherwise as asked.</summary>
    public static bool Apply(bool requested, string? typeCode, bool isHidden = false, bool isSystem = false) =>
        requested || IsLocked(typeCode, isHidden, isSystem);
}
