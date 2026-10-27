using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records;

/// <summary>
/// Snapshot fields: a field whose value is copied once from another field of the same record when the record
/// is created (<see cref="AppField.SnapshotSourceFid"/>), and is read-only afterwards unless
/// <see cref="AppField.SnapshotEditable"/> is on. This service works out the value to capture; the record
/// writers call it so the UI, the API, Pipelines and Copy Records behave identically.
/// </summary>
public interface ISnapshotCaptureService
{
    /// <summary>Fills the snapshot fields of a record that is about to be created, in <paramref name="values"/> (Fid → value).
    /// A non-editable snapshot field is always filled from its source; an editable one keeps a value that was supplied. A blank
    /// source writes a blank — the capture never waits or retries.</summary>
    Task ApplyOnCreateAsync(AppTable table, IReadOnlyList<AppField> fields, IDictionary<long, object?> values, CancellationToken ct = default);

    /// <summary>Same as the single-record overload for many new records at once (one projection for all of them) — used by imports.</summary>
    Task ApplyOnCreateAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IDictionary<long, object?>> rowValues, CancellationToken ct = default);

    /// <summary>The value each of <paramref name="rows"/> (stored rows, keyed by physical column) would capture for
    /// <paramref name="snapshotField"/> right now — used to (re)initialize existing records.</summary>
    Task<IReadOnlyList<object?>> CaptureForRowsAsync(
        AppTable table, IReadOnlyList<AppField> fields, AppField snapshotField,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct = default);
}

public sealed class SnapshotCaptureService : ISnapshotCaptureService
{
    private readonly IRelationalProjector _relational;

    public SnapshotCaptureService(IRelationalProjector relational) => _relational = relational;

    public Task ApplyOnCreateAsync(AppTable table, IReadOnlyList<AppField> fields, IDictionary<long, object?> values, CancellationToken ct = default) =>
        ApplyOnCreateAsync(table, fields, [values], ct);

    public async Task ApplyOnCreateAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IDictionary<long, object?>> rowValues, CancellationToken ct = default)
    {
        var snapshots = SnapshotFields(fields);
        if (snapshots.Count == 0 || rowValues.Count == 0) return;

        // The records do not exist yet, so synthetic stored rows are built from what is about to be written; a lookup
        // then reads through the reference value in each, exactly as it would after the insert.
        var rows = new List<IReadOnlyDictionary<string, object?>>(rowValues.Count);
        foreach (var values in rowValues)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields)
            {
                if (!f.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(f.TypeCode)) continue;
                if (!values.TryGetValue(f.Fid.Value, out var v)) continue;
                row[PhysicalNaming.GetPhysicalColumnName(f)] = v;
                row[PhysicalNaming.ColumnName(f.Fid.Value)] = v;
            }
            rows.Add(row);
        }

        var projected = await ProjectIfNeededAsync(table, fields, snapshots, rows, ct);
        for (var i = 0; i < rowValues.Count; i++)
        {
            foreach (var snapshot in snapshots)
            {
                var key = (long)snapshot.Fid!.Value;
                if (snapshot.SnapshotEditable && rowValues[i].TryGetValue(key, out var supplied) && !IsBlank(supplied)) continue;
                rowValues[i][key] = ValueFor(snapshot, fields, rows[i], projected, i);
            }
        }
    }

    public async Task<IReadOnlyList<object?>> CaptureForRowsAsync(
        AppTable table, IReadOnlyList<AppField> fields, AppField snapshotField,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return [];
        var projected = await ProjectIfNeededAsync(table, fields, [snapshotField], rows, ct);
        var result = new object?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            result[i] = ValueFor(snapshotField, fields, rows[i], projected, i);
        return result;
    }

    private static List<AppField> SnapshotFields(IReadOnlyList<AppField> fields) =>
        fields.Where(f => f.SnapshotSourceFid.HasValue && f.Fid.HasValue && !f.IsDeleted).ToList();

    /// <summary>Lookup values have no column: they are projected from the rows, only when some snapshot reads one.</summary>
    private async Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>?> ProjectIfNeededAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<AppField> snapshots,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct)
    {
        var readsComputed = snapshots.Any(s =>
            fields.FirstOrDefault(f => f.Fid == s.SnapshotSourceFid) is { } src && PhysicalNaming.IsComputedTypeCode(src.TypeCode));
        return readsComputed ? await _relational.ProjectAsync(table, fields, rows, ct) : null;
    }

    private static object? ValueFor(
        AppField snapshot, IReadOnlyList<AppField> fields, IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<IReadOnlyDictionary<long, object?>>? projected, int index)
    {
        var source = fields.FirstOrDefault(f => f.Fid == snapshot.SnapshotSourceFid && !f.IsDeleted);
        if (source is null) return null;   // the source field is gone: nothing to capture

        object? value;
        if (PhysicalNaming.IsComputedTypeCode(source.TypeCode))
            value = projected is not null && projected[index].TryGetValue(source.Fid!.Value, out var p) ? p : null;
        else if (!row.TryGetValue(PhysicalNaming.GetPhysicalColumnName(source), out value))
            row.TryGetValue(PhysicalNaming.ColumnName(source.Fid!.Value), out value);

        value = Unwrap(value);
        if (value is null or DBNull) return null;

        // The same type: copied as it is. A compatible one (a number into a text field, …) goes through the import conversion.
        if (string.Equals(FieldValueTypes.Of(source), snapshot.TypeCode, StringComparison.OrdinalIgnoreCase)) return value;
        return ImportTypeCompatibility.TryConvert(value, snapshot, out var converted, out _) ? converted : null;
    }

    private static object? Unwrap(object? value) => value is JsonElement e
        ? e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : e.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        }
        : value;

    private static bool IsBlank(object? v) => Unwrap(v) is null or DBNull or "";
}

/// <summary>Server-side read-only enforcement for Snapshot fields: every write path calls this, so a UI edit, an API call,
/// an Import, a Pipeline or a Copy Records run is blocked the same way unless the field is editable after the snapshot.</summary>
public static class SnapshotWriteGuard
{
    /// <summary>For an update: rejects any submitted value that would change a read-only snapshot field. A value equal to
    /// what is already stored is allowed, so a form that re-sends the whole record is not refused.
    /// <paramref name="isUnchanged"/> receives (field, submitted value) and answers whether it equals the stored one.</summary>
    public static void EnsureUnchanged(
        IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> submitted, Func<AppField, object?, bool> isUnchanged)
    {
        var blocked = ReadOnlySnapshots(fields)
            .Where(f => submitted.TryGetValue(f.Fid!.Value, out var v) && !isUnchanged(f, v))
            .ToList();
        Throw(blocked);
    }

    /// <summary>For a create: rejects a non-blank value for a read-only snapshot field (the system fills it); a blank is
    /// dropped from <paramref name="values"/> so the capture can fill it.</summary>
    public static void EnsureNotSuppliedOnCreate(IReadOnlyList<AppField> fields, IDictionary<long, object?> values)
    {
        var blocked = new List<AppField>();
        foreach (var f in ReadOnlySnapshots(fields))
        {
            if (!values.TryGetValue(f.Fid!.Value, out var v)) continue;
            if (IsBlank(v)) values.Remove(f.Fid.Value);
            else blocked.Add(f);
        }
        Throw(blocked);
    }

    /// <summary>For a write that sets <paramref name="fids"/> to a value on existing records (mass update, import): rejects any read-only snapshot field among them.</summary>
    public static void EnsureNotWritten(IReadOnlyList<AppField> fields, IEnumerable<long> fids)
    {
        var set = fids.ToHashSet();
        Throw(ReadOnlySnapshots(fields).Where(f => set.Contains(f.Fid!.Value)).ToList());
    }

    public static bool IsReadOnlySnapshot(AppField f) => f.SnapshotSourceFid.HasValue && !f.SnapshotEditable;

    private static IEnumerable<AppField> ReadOnlySnapshots(IReadOnlyList<AppField> fields) =>
        fields.Where(f => f.Fid.HasValue && !f.IsDeleted && IsReadOnlySnapshot(f));

    private static bool IsBlank(object? v) => v is null or DBNull or "" || (v is JsonElement e && e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);

    private static void Throw(List<AppField> blocked)
    {
        if (blocked.Count == 0) return;
        throw new ValidationException(new Dictionary<string, string[]>
        {
            ["fields"] = [$"Snapshot fields are read-only and cannot be set: {string.Join(", ", blocked.Select(f => f.Label ?? f.Name))}"],
        });
    }
}
