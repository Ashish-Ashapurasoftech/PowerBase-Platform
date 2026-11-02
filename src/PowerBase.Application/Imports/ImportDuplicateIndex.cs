using System.Runtime.InteropServices;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>For the "does not have a duplicate" filter operator: how many rows of the source carry each value of a field, and which row carried it first.
/// It is filled by a pass that reads the source before the real passes (the filter counts the rows that pass its other conditions), then only read.
/// Values are kept as 64-bit hashes of their normalised text (trimmed, case ignored, numbers and dates in one form), 24 bytes per distinct value however
/// long the text is, so a million distinct values take about 30 MB. A collision would need two different values to hash alike: below one in ten million
/// for a million values.</summary>
public sealed class ImportDuplicateIndex
{
    /// <summary>The most distinct values one field may have; beyond it the run says so instead of using the memory.</summary>
    public const int MaxDistinctValues = 5_000_000;

    private struct Entry
    {
        public int Count;
        public long FirstId;
    }

    private readonly Dictionary<long, Dictionary<ulong, Entry>> _byField = new();
    private int _distinct;

    /// <summary>Counts one row's value. A blank is never a duplicate, so it is not counted.</summary>
    public void Add(long fid, object? value, long rowId)
    {
        var key = ImportKey.Normalize(value);
        if (key is null) return;
        if (!_byField.TryGetValue(fid, out var values)) _byField[fid] = values = new Dictionary<ulong, Entry>();
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(values, Hash(key), out var existed);
        if (!existed)
        {
            if (++_distinct > MaxDistinctValues)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["Import"] = [$"A filter that excludes duplicates cannot look at more than {MaxDistinctValues:N0} different values. Use a field with fewer different values, or split the file."]
                });
            entry.FirstId = rowId;
        }
        entry.Count++;
    }

    /// <summary>Whether a row passes "does not have a duplicate" on a field: its value is blank, or no other row has it, or (with <paramref name="keepFirst"/>)
    /// it is the first row that has it. A value nothing was counted for (the counting pass did not run) passes.</summary>
    public bool IsKept(long fid, object? value, long rowId, bool keepFirst)
    {
        var key = ImportKey.Normalize(value);
        if (key is null) return true;
        if (!_byField.TryGetValue(fid, out var values) || !values.TryGetValue(Hash(key), out var entry)) return true;
        return entry.Count <= 1 || (keepFirst && entry.FirstId == rowId);
    }

    /// <summary>FNV-1a over the lower-cased characters, so "ABC" and "abc" are the same value, as in the database.</summary>
    private static ulong Hash(string key)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in key)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
