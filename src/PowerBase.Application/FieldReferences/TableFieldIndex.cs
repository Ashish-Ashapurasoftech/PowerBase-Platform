using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.Application.FieldReferences;

/// <summary>One table's fields, indexed for the lookups reference extraction keeps doing: a
/// report/settings <c>Fid</c> (the per-table field number) → the field's <c>meta.AppField.Id</c>,
/// and a formula expression → the fields it reads.</summary>
public sealed class TableFieldIndex
{
    private readonly Dictionary<long, long> _idByFid = [];
    private readonly AppFieldSchema _schema;

    public long TableId { get; }
    public IReadOnlyList<AppField> Fields { get; }

    public TableFieldIndex(long tableId, IReadOnlyList<AppField> fields)
    {
        TableId = tableId;
        Fields = fields;
        foreach (var f in fields)
            if (f.Fid is { } fid) _idByFid[fid] = f.Id;
        _schema = new AppFieldSchema(fields);
    }

    /// <summary>The AppField.Id for a Fid on this table, or null when there is no such live field.</summary>
    public long? IdOfFid(long? fid) => fid is { } f && _idByFid.TryGetValue(f, out var id) ? id : null;

    /// <summary>The AppField.Ids a formula expression reads. Compiles even when the formula has
    /// errors — the fields it did manage to bind are still references.</summary>
    public IReadOnlyList<long> FieldsReadBy(FormulaEngine engine, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];
        var compiled = engine.Compile(expression, _schema);
        return compiled.ReferencedFieldIds
            .Select(fid => IdOfFid(fid))
            .OfType<long>()
            .ToList();
    }
}
