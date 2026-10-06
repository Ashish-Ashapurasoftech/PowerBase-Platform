using System.Globalization;
using PowerBase.Application.Reports;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.Application.Imports;

public enum ImportPassMode
{
    /// <summary>Check and write.</summary>
    Write,
    /// <summary>Run every check exactly as a write would, but write nothing (the pre-check of "abort if any issue").</summary>
    Validate
}

/// <summary>What one chunk produced. Every source row in the chunk ends up in exactly one of the four counts, and every
/// row that was not imported has exactly one entry in <see cref="Feedback"/>.</summary>
public sealed record ImportChunkOutcome(long Inserted, long Updated, long Skipped, long Errored, List<ImportFeedbackRow> Feedback)
{
    public static readonly ImportChunkOutcome None = new(0, 0, 0, 0, []);
}

/// <summary>Turns source rows into destination writes for one pass over the source. A row goes through the same stages
/// in the same order every time, and the first stage that rejects it decides its single reason:
/// value conversion, column rules (Require / Ignore blanks / Remove duplicates), merge matching, required fields and the
/// table's data rule, unique constraints, then the write. Rules rejecting a row on purpose report it as skipped; data
/// and constraint problems report it as an error.</summary>
public sealed class ImportChunkWriter
{
    private sealed class Row(IReadOnlyDictionary<string, object?> source, long sourceId, Dictionary<long, object?> values)
    {
        public IReadOnlyDictionary<string, object?> Source { get; } = source;
        public long SourceId { get; } = sourceId;
        public Dictionary<long, object?> Values { get; } = values;
        /// <summary>Merge: the Record ID# of the destination record this row updates (null = insert a new record).</summary>
        public long? MatchedId { get; set; }
        /// <summary>What each formula mapping returned for this row, by mapping position, for the details file.</summary>
        public string?[]? FormulaResults { get; set; }
    }

    /// <summary>Merge on an encrypted key: the destination's key values, decrypted, with each record's Record ID#. A stored value is
    /// ciphertext and cannot be compared in SQL, so the table is read once (in Record ID# order, a chunk at a time) and matched here.</summary>
    private Dictionary<string, long>? _encryptedKeyIndex;

    /// <summary>Set when the import fills several tables: rows that are not imported are labelled with the table they were meant for.</summary>
    private readonly string? _tableLabel;

    private readonly ImportPlan _plan;
    private readonly IImportDataStore _store;
    private readonly IRecordRepository _records;
    private readonly ImportDataRuleGate? _gate;
    private readonly long _userId;
    private readonly ImportPassMode _mode;
    private readonly ImportDuplicateTracker _tracker;
    private readonly bool _isMerge;
    private readonly FormulaEngine _engine;
    private readonly IReadOnlyDictionary<long, string> _fidToColumn;
    private readonly AppField? _key;
    private readonly bool _keyChecksDuplicates;
    private readonly List<(AppField Field, ImportColumnRule Rule)> _rules;
    private readonly Dictionary<int, object?> _defaults;
    private readonly List<AppField> _required;
    private readonly List<AppField> _unique;
    private readonly Dictionary<int, ImportFieldFormat> _formats;

    public ImportChunkWriter(ImportPlan plan, IImportDataStore store, IRecordRepository records, ImportDataRuleGate? gate,
        FormulaEngine engine, long userId, ImportPassMode mode, ImportDuplicateTracker tracker, string? tableLabel = null)
    {
        _tableLabel = tableLabel;
        _plan = plan; _store = store; _records = records; _gate = gate; _engine = engine; _userId = userId; _mode = mode; _tracker = tracker;
        _isMerge = plan.ImportType == ImportTypes.Merge;
        _key = plan.MergeKey;
        // Formulas read a row through the column each source field is stored in (computed values were projected into the row).
        _fidToColumn = plan.SourceFields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value, PhysicalNaming.GetPhysicalColumnName);
        _rules = plan.Mappings.Where(m => plan.Rules.ContainsKey(m.Destination.Fid!.Value))
            .Select(m => (m.Destination, plan.Rules[m.Destination.Fid!.Value])).ToList();

        var writable = plan.DestinationFields.Where(f => !f.IsSystem && ImportTypeCompatibility.IsWritable(f)).ToList();
        _defaults = writable.Where(f => !string.IsNullOrWhiteSpace(f.DefaultValue)).ToDictionary(f => f.Fid!.Value,
            f => (object?)(f.TypeCode == "Boolean" ? f.DefaultValue!.Equals("true", StringComparison.OrdinalIgnoreCase) : f.DefaultValue));
        _required = writable.Where(f => f.IsRequired).ToList();
        _formats = plan.Mappings.Select(m => (Fid: m.Destination.Fid!.Value, Format: ImportFieldFormat.For(m.Destination)))
            .Where(x => x.Format is not null).ToDictionary(x => x.Fid, x => x.Format!);

        // A column with a "Remove duplicates" rule handles its own duplicates (skipping them); every other unique column
        // reports a duplicate as an error. The merge key is matched, so a value that already exists is an update.
        var dedupedByRule = _rules.Where(r => r.Rule.RemoveDuplicates).Select(r => r.Field.Fid!.Value).ToHashSet();
        _unique = writable.Where(f => f.IsUnique && f.Fid != _key?.Fid && !dedupedByRule.Contains(f.Fid!.Value)).ToList();
        _keyChecksDuplicates = _key is not null && !dedupedByRule.Contains(_key.Fid!.Value);
    }

    /// <summary>Loads what a Copy's "Remove duplicates" columns must be compared against: the values already in the
    /// destination, streamed once into compact hashed sets. Merges compare within the run only, because a value that
    /// already exists is simply the record being updated.</summary>
    public async Task PrepareAsync(CancellationToken ct)
    {
        if (_isMerge)
        {
            if (_key!.IsEncrypted) _encryptedKeyIndex = await BuildEncryptedKeyIndexAsync(ct);
            return;
        }
        foreach (var (field, rule) in _rules.Where(r => r.Rule.RemoveDuplicates))
        {
            // An encrypted column stores ciphertext, so its existing values are read decrypted through the record repository.
            if (field.IsEncrypted)
            {
                await foreach (var (_, value) in ReadDecryptedAsync(field, ct))
                    if (ImportKey.Normalize(value) is { } key) _tracker.AddExisting(field.Fid!.Value, key);
            }
            else
                await foreach (var value in _store.StreamColumnValuesAsync(_plan.Destination, field, ct))
                    if (ImportKey.Normalize(value) is { } key) _tracker.AddExisting(field.Fid!.Value, key);
        }
    }

    /// <summary>The destination's records as (Record ID#, decrypted value of <paramref name="field"/>), in Record ID# order, a chunk at a time.</summary>
    private async IAsyncEnumerable<(long Id, object? Value)> ReadDecryptedAsync(AppField field, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var fields = _plan.DestinationFields.Where(f => f.Fid == field.Fid || ImportTypeCompatibility.IsRecordId(f)).ToList();
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var filter = new FilterGroup();
            filter.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "gt", Value = after.ToString(CultureInfo.InvariantCulture) } });
            filter.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "lte", Value = long.MaxValue.ToString(CultureInfo.InvariantCulture) } });
            var rows = await _records.ListAsync(_plan.Destination, fields, 1, EncryptedIndexChunk, filter, null, null, ct);
            if (rows.Count == 0) yield break;
            foreach (var row in rows)
            {
                after = ImportSourceReader.RecordId(row);
                yield return (after, row.GetValueOrDefault(column));
            }
            if (rows.Count < EncryptedIndexChunk) yield break;
        }
    }

    private async Task<Dictionary<string, long>> BuildEncryptedKeyIndexAsync(CancellationToken ct)
    {
        var index = new Dictionary<string, long>(ImportKey.Comparer);
        await foreach (var (id, value) in ReadDecryptedAsync(_key!, ct))
            if (ImportKey.Normalize(value) is { } key) index[key] = id;
        return index;
    }

    private const int EncryptedIndexChunk = 5000;

    /// <summary>Scan pass of "exclude duplicate groups": notes every unique value that occurs more than once among the
    /// rows that reach the constraint stage, so the writing pass can leave out the whole group.</summary>
    public void CollectDuplicateGroups(IReadOnlyList<IReadOnlyDictionary<string, object?>> chunk)
    {
        var discard = new List<ImportFeedbackRow>();
        var constraintColumns = _unique.Select(f => f.Fid!.Value).Concat(_keyChecksDuplicates ? [_key!.Fid!.Value] : []);
        foreach (var row in ApplyColumnRules(MapRows(chunk, discard), discard))
            foreach (var fid in constraintColumns)
                if (ImportKey.Normalize(row.Values.GetValueOrDefault(fid)) is { } key && !_tracker.MarkSeen(fid, key))
                    _tracker.MarkDuplicateGroup(fid, key);
    }

    public async Task<ImportChunkOutcome> ProcessAsync(IReadOnlyList<IReadOnlyDictionary<string, object?>> chunk, CancellationToken ct)
    {
        var feedback = new List<ImportFeedbackRow>();
        var rows = ApplyColumnRules(MapRows(chunk, feedback), feedback);
        if (_key is not null) rows = await MatchExistingAsync(rows, feedback, ct);

        var matchedIds = rows.Where(r => r.MatchedId.HasValue).Select(r => r.MatchedId!.Value).ToList();
        var stored = _gate is not null && matchedIds.Count > 0
            ? await _records.GetRowsByIdsAsync(_plan.Destination, _plan.DestinationFields, matchedIds, ct)
            : null;
        var alive = new List<Row>(rows.Count);
        foreach (var row in rows)
            if (Finalize(row, stored, feedback)) alive.Add(row);

        foreach (var field in _unique)
            alive = await RejectConstraintDuplicatesAsync(alive, field, feedback, ct);

        var updates = alive.Where(r => r.MatchedId.HasValue).ToList();
        var inserts = alive.Where(r => !r.MatchedId.HasValue).ToList();
        long updated, inserted;
        if (_mode == ImportPassMode.Write)
        {
            updated = await WriteAsync(updates, batch => _store.UpdateAsync(_plan.Destination, _plan.DestinationFields,
                batch.Select(r => new ImportUpdateRow(r.MatchedId!.Value, r.Values)).ToList(), _userId, ct), feedback);
            inserted = await WriteAsync(inserts, batch => _store.InsertAsync(_plan.Destination, _plan.DestinationFields,
                batch.Select(r => (IReadOnlyDictionary<long, object?>)r.Values).ToList(), _userId, ct), feedback);
        }
        else
        {
            updated = updates.Count;
            inserted = inserts.Count;
        }

        var errored = feedback.Count(f => f.Issue.Outcome == ImportOutcome.Errored);
        return new ImportChunkOutcome(inserted, updated, feedback.Count - errored, errored, feedback);
    }

    /// <summary>Converts each source row into destination values; a value that cannot be converted safely rejects the row.</summary>
    private List<Row> MapRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> chunk, List<ImportFeedbackRow> feedback)
    {
        var rows = new List<Row>(chunk.Count);
        foreach (var source in chunk)
        {
            var id = ImportSourceReader.RecordId(source);
            var values = new Dictionary<long, object?>(_plan.Mappings.Count + _defaults.Count);
            var row = new Row(source, id, values);
            // The first problem found rejects the row. It is reported after every mapping has been walked, so the details file
            // can show what each formula returned even when an earlier column was the one that failed.
            (int Fid, string Reason, string Message)? failure = null;
            for (var i = 0; i < _plan.Mappings.Count; i++)
            {
                var map = _plan.Mappings[i];
                var fid = map.Destination.Fid!.Value;
                object? raw;
                if (failure is not null)
                {
                    // The row is already rejected: only calculate the remaining formulas, so the details file shows them.
                    if (map.Kind == ImportMappingSource.Formula && TryEvaluate(map, row, out raw, out _))
                        (row.FormulaResults ??= new string?[_plan.Mappings.Count])[i] = ImportFeedbackWriter.Format(raw);
                    continue;
                }
                switch (map.Kind)
                {
                    case ImportMappingSource.Static:
                        values[fid] = map.Constant; // parsed once when the run was planned
                        continue;
                    case ImportMappingSource.Formula:
                        if (!TryEvaluate(map, row, out raw, out var formulaError))
                        {
                            failure = (fid, ImportReason.FormulaError, formulaError!);
                            continue;
                        }
                        (row.FormulaResults ??= new string?[_plan.Mappings.Count])[i] = ImportFeedbackWriter.Format(raw);
                        break;
                    default:
                        source.TryGetValue(map.SourceColumn!, out raw);
                        break;
                }
                if (!ImportTypeCompatibility.TryConvert(raw, map.Destination, out var converted, out var error))
                    failure = (fid, ImportReason.TypeMismatch, error!);
                else if (_formats.TryGetValue(fid, out var format) && format.Check(converted) is { } formatError)
                    failure = (fid, ImportReason.FormatViolation, formatError);
                else values[fid] = converted;
            }
            if (failure is { } f) Report(feedback, row, f.Fid, ImportOutcome.Errored, f.Reason, f.Message);
            else rows.Add(row);
        }
        return rows;
    }

    /// <summary>Calculates a formula mapping for one row. The formula language yields a blank (not an error) for things like
    /// dividing by zero, so a blank is a normal result; "Require field" on the column leaves such rows out. A row the
    /// engine cannot calculate at all (for example a value of an unexpected type) is reported instead of being blanked.</summary>
    private bool TryEvaluate(ResolvedMapping map, Row row, out object? raw, out string? error)
    {
        raw = null;
        error = null;
        try
        {
            raw = FormulaRawValue.ToRaw(_engine.Evaluate(map.Formula!, new RowRecordContext(row.Source, _fidToColumn), _plan.FormulaOptions));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"The formula for '{ImportTypeCompatibility.DisplayName(map.Destination)}' could not be calculated: {ex.Message}";
            return false;
        }
    }

    /// <summary>The per-column rules, evaluated independently for each column. Require field and Remove duplicates leave
    /// the row out (skipped, with the reason); Ignore blanks keeps the row but drops the blank value, so an existing
    /// value is not overwritten. A value only counts as "seen" once its row has passed every rule, so a row rejected by
    /// one column never causes a later row to be rejected as its duplicate.</summary>
    private List<Row> ApplyColumnRules(List<Row> rows, List<ImportFeedbackRow> feedback)
    {
        if (_rules.Count == 0) return rows;
        var kept = new List<Row>(rows.Count);
        var pending = new List<(int Fid, string Key)>();
        foreach (var row in rows)
        {
            pending.Clear();
            var skipped = false;
            foreach (var (field, rule) in _rules)
            {
                var fid = field.Fid!.Value;
                var value = row.Values.GetValueOrDefault(fid);
                var name = ImportTypeCompatibility.DisplayName(field);
                if (IsBlank(value))
                {
                    if (rule.RequireField)
                    {
                        Report(feedback, row, fid, ImportOutcome.Skipped, ImportReason.RequiredBlank, $"'{name}' is blank, and this import requires a value.");
                        skipped = true;
                        break;
                    }
                    if (rule.IgnoreBlanks) row.Values.Remove(fid);
                    continue;
                }
                if (!rule.RemoveDuplicates || ImportKey.Normalize(value) is not { } key) continue;

                if (!_isMerge && _tracker.ExistsInDestination(fid, key))
                    Report(feedback, row, fid, ImportOutcome.Skipped, ImportReason.DuplicateInDestination, $"'{name}' value '{key}' already exists in {_plan.Destination.Name}.");
                else if (_tracker.WasSeen(fid, key))
                    Report(feedback, row, fid, ImportOutcome.Skipped, ImportReason.DuplicateInRun, $"'{name}' value '{key}' appeared in an earlier row of this import.");
                else { pending.Add((fid, key)); continue; }
                skipped = true;
                break;
            }
            if (skipped) continue;
            foreach (var (fid, key) in pending) _tracker.MarkSeen(fid, key);
            kept.Add(row);
        }
        return kept;
    }

    /// <summary>Merge: finds the destination record each row's key points at. A row without a key, or whose key repeats,
    /// is reported; the others become updates (match) or inserts (no match).</summary>
    private async Task<List<Row>> MatchExistingAsync(List<Row> rows, List<ImportFeedbackRow> feedback, CancellationToken ct)
    {
        var keyFid = _key!.Fid!.Value;
        var keyed = new List<(Row Row, string Key)>(rows.Count);
        foreach (var row in rows)
        {
            var key = ImportKey.Normalize(row.Values.GetValueOrDefault(keyFid));
            if (key is null)
                Report(feedback, row, keyFid, ImportOutcome.Errored, ImportReason.MergeKeyMissing,
                    $"'{ImportTypeCompatibility.DisplayName(_key)}' is blank, so the record to update cannot be found.");
            else if (_keyChecksDuplicates && RepeatedValueMessage(_key, key) is { } repeated)
                Report(feedback, row, keyFid, ImportOutcome.Errored, ImportReason.DuplicateInRun, repeated);
            else keyed.Add((row, key));
        }
        if (keyed.Count == 0) return [];

        var matches = _encryptedKeyIndex ?? await _store.FindRecordIdsAsync(_plan.Destination, _key, keyed.Select(k => k.Row.Values[keyFid]!).ToList(), ct);
        foreach (var (row, key) in keyed)
            if (matches.TryGetValue(key, out var recordId)) row.MatchedId = recordId;
        return keyed.Select(k => k.Row).ToList();
    }

    /// <summary>Applies defaults, required checks, key handling and the table's data rule. Updates are checked only
    /// on the fields they change: a field the import does not set keeps its stored value.</summary>
    private bool Finalize(Row row, IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>? stored, List<ImportFeedbackRow> feedback)
    {
        var isUpdate = row.MatchedId.HasValue;
        // A new record gets the field's default wherever the import leaves a field unset: unmapped, or a blank it ignores.
        if (!isUpdate)
            foreach (var (fid, value) in _defaults)
                if (!row.Values.ContainsKey(fid)) row.Values[fid] = value;
        // The Record ID# is only ever a match key. An updated record keeps its key value, so it is not rewritten.
        if (_key is not null && (isUpdate || ImportTypeCompatibility.IsRecordId(_key))) row.Values.Remove(_key.Fid!.Value);

        if (isUpdate && row.Values.Count == 0)
        {
            Report(feedback, row, null, ImportOutcome.Skipped, ImportReason.NoChanges, "Every mapped value was blank and ignored, so there was nothing to update.");
            return false;
        }

        var missing = _required.FirstOrDefault(f => (!isUpdate || row.Values.ContainsKey(f.Fid!.Value))
                                                   && PhysicalNaming.IsRequiredMissing(f.TypeCode, row.Values.GetValueOrDefault(f.Fid!.Value)));
        if (missing is not null)
        {
            Report(feedback, row, missing.Fid, ImportOutcome.Errored, ImportReason.RequiredMissing,
                $"'{ImportTypeCompatibility.DisplayName(missing)}' is required.");
            return false;
        }

        var violation = _gate?.Check(isUpdate ? EffectiveValues(row, stored) : row.Values);
        if (violation is not null)
        {
            Report(feedback, row, null, ImportOutcome.Errored, ImportReason.ConstraintViolation, violation);
            return false;
        }
        return true;
    }

    /// <summary>What an updated record will look like after the write: stored values with the import's changes on top.</summary>
    private IReadOnlyDictionary<long, object?> EffectiveValues(Row row, IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>? stored)
    {
        var current = stored is not null && stored.TryGetValue(row.MatchedId!.Value, out var existing) ? existing : null;
        var effective = new Dictionary<long, object?>();
        foreach (var f in _plan.DestinationFields.Where(f => f.Fid.HasValue && !f.IsSystem && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
            if (current is not null && current.TryGetValue(PhysicalNaming.ColumnName(f.Fid!.Value), out var value))
                effective[f.Fid.Value] = value;
        foreach (var (fid, value) in row.Values) effective[fid] = value;
        return effective;
    }

    /// <summary>Reports rows whose unique value repeats in this import (per the constraint policy) or belongs to a
    /// different record in the destination.</summary>
    private async Task<List<Row>> RejectConstraintDuplicatesAsync(List<Row> rows, AppField field, List<ImportFeedbackRow> feedback, CancellationToken ct)
    {
        var fid = field.Fid!.Value;
        var keyed = new List<(Row Row, string? Key)>(rows.Count);
        foreach (var row in rows)
        {
            var key = ImportKey.Normalize(row.Values.GetValueOrDefault(fid));
            if (key is not null && RepeatedValueMessage(field, key) is { } repeated)
            {
                Report(feedback, row, fid, ImportOutcome.Errored, ImportReason.DuplicateInRun, repeated);
                continue;
            }
            keyed.Add((row, key));
        }
        // Encrypted values cannot be compared with what is stored, so the database's own unique index is the check.
        if (field.IsEncrypted || keyed.All(k => k.Key is null)) return keyed.Select(k => k.Row).ToList();

        var owners = await _store.FindRecordIdsAsync(_plan.Destination, field,
            keyed.Where(k => k.Key is not null).Select(k => k.Row.Values[fid]!).ToList(), ct);
        var kept = new List<Row>(keyed.Count);
        foreach (var (row, key) in keyed)
        {
            // An updated record may keep its own value; only a value held by another record is a conflict.
            if (key is not null && owners.TryGetValue(key, out var owner) && owner != row.MatchedId)
                Report(feedback, row, fid, ImportOutcome.Errored, ImportReason.DuplicateInDestination,
                    $"'{ImportTypeCompatibility.DisplayName(field)}' value '{key}' already exists in {_plan.Destination.Name}.", existingRecord: owner);
            else kept.Add(row);
        }
        return kept;
    }

    /// <summary>Why a unique value cannot be used again: it is part of a duplicate group the policy excludes entirely, or
    /// it was already used earlier in this run. Null when the value is free (and now taken).</summary>
    private string? RepeatedValueMessage(AppField field, string key)
    {
        var fid = field.Fid!.Value;
        var name = ImportTypeCompatibility.DisplayName(field);
        if (_tracker.IsInDuplicateGroup(fid, key))
            return $"'{name}' value '{key}' appears more than once in the source, so none of those rows were imported.";
        return _tracker.MarkSeen(fid, key) ? null : $"'{name}' value '{key}' appears more than once in this import.";
    }

    /// <summary>Writes the rows in one batch; if the database rejects it, halves the batch until the offending rows
    /// are isolated, so only those rows are reported and everything else still imports.</summary>
    private async Task<long> WriteAsync(List<Row> rows, Func<List<Row>, Task> write, List<ImportFeedbackRow> feedback)
    {
        if (rows.Count == 0) return 0;
        try
        {
            await write(rows);
            return rows.Count;
        }
        catch (ImportRowRejectedException ex)
        {
            if (rows.Count == 1)
            {
                Report(feedback, rows[0], null, ImportOutcome.Errored, ImportReason.WriteFailed, ex.Message);
                return 0;
            }
            var half = rows.Count / 2;
            return await WriteAsync(rows.GetRange(0, half), write, feedback) + await WriteAsync(rows.GetRange(half, rows.Count - half), write, feedback);
        }
    }

    /// <summary>A blank is no value or only whitespace. An unchecked Boolean or a zero is a real value, not a blank.</summary>
    private static bool IsBlank(object? value) => value is null || (value is string s && string.IsNullOrWhiteSpace(s));

    private void Report(List<ImportFeedbackRow> sink, Row row, int? fid, string outcome, string reason, string message, long? existingRecord = null)
    {
        // Reading every sheet numbers rows across sheets; a person is shown the row in its sheet, and the sheet's name.
        var rowRef = row.SourceId;
        if (_plan.File?.Sheets is { } sheets)
        {
            var (sheetIndex, rowNumber) = ImportFileSheets.Split(row.SourceId);
            rowRef = rowNumber;
            if (sheetIndex < sheets.Count) message = $"Sheet '{sheets[sheetIndex].Name}': {message}";
        }
        if (_tableLabel is not null) message = $"{_tableLabel}: {message}";
        var issue = new ImportRunIssue
        {
            SourceRowRef = rowRef, ColumnFid = fid, Outcome = outcome, ReasonCode = reason, ExistingRecordRef = existingRecord,
            Message = message.Length <= 500 ? message : message[..500]
        };
        // The carried values are read only now, for rejected rows, so a clean row costs nothing.
        var carried = _plan.Mappings.Select((m, i) => m.Kind switch
        {
            ImportMappingSource.Static => m.StaticText,
            ImportMappingSource.Formula => row.FormulaResults?[i],
            _ => ImportFeedbackWriter.Format(row.Source.GetValueOrDefault(m.SourceColumn!))
        }).ToArray();
        if (_tableLabel is not null)
        {
            // Several tables share one details file: a line says which table it was for, names its column, and lists the values it carried.
            var columnLabel = fid is { } f ? _plan.DestinationFields.FirstOrDefault(d => d.Fid == f) is { } field ? ImportTypeCompatibility.DisplayName(field) : null : null;
            var values = string.Join("; ", _plan.Mappings.Select((m, i) => $"{ImportTypeCompatibility.DisplayName(m.Destination)}={carried[i]}"));
            sink.Add(new ImportFeedbackRow(issue, [values], _tableLabel, columnLabel));
            return;
        }
        sink.Add(new ImportFeedbackRow(issue, carried));
    }
}
