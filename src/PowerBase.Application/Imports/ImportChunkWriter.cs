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
/// <param name="Unchanged">A merge matched the record, but every value was already what the import would write, so it was left alone.</param>
/// <param name="Written">Every row that was written or left unchanged, for the details file (a write pass only).</param>
public sealed record ImportChunkOutcome(
    long Inserted, long Updated, long Skipped, long Errored, List<ImportFeedbackRow> Feedback, long Unchanged = 0,
    IReadOnlyList<ImportWrittenRow>? Written = null)
{
    public static readonly ImportChunkOutcome None = new(0, 0, 0, 0, []);
}

/// <summary>A source row that was imported, as the details file lists it: what happened to it, the record it became or matched, and the
/// values it carried.</summary>
/// <param name="Outcome">One of <see cref="ImportOutcome"/>: inserted, updated or unchanged.</param>
public sealed record ImportWrittenRow(long SourceRowRef, string Outcome, long RecordId, IReadOnlyList<string?> SourceValues, string? Note = null);

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
        /// <summary>Merge: the normalised value the row was matched on. The key is dropped from <see cref="Values"/> once the record is found, so the
        /// duplicate check (which comes last) reads it from here.</summary>
        public string? MatchKey { get; set; }
        /// <summary>The Record ID# the row was given when it was inserted.</summary>
        public long? InsertedId { get; set; }
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
        // Virtual columns were added to every row as it was read, so a formula can use them like any other column.
        if (plan.Virtuals.Count > 0)
        {
            var withVirtuals = new Dictionary<long, string>(_fidToColumn);
            foreach (var v in plan.Virtuals) withVirtuals[v.Field.Fid!.Value] = v.Column;
            _fidToColumn = withVirtuals;
        }
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
        foreach (var row in RemoveDuplicateRows(ApplyColumnRules(MapRows(chunk, discard), discard), discard))
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
            alive = await RejectDestinationConflictsAsync(alive, field, feedback, ct);

        // Rows now compete with each other, in source order: a value that is unique (or the merge key) may be carried by one row of the import, and
        // "Remove duplicates" leaves out later rows that repeat a value. These come last, so a row that fails any check above never uses up a
        // value and costs the row after it; and a row left out here, for either reason, uses up none of its values either.
        alive = ResolveCompetition(alive, feedback);

        var updates = alive.Where(r => r.MatchedId.HasValue).ToList();
        var inserts = alive.Where(r => !r.MatchedId.HasValue).ToList();
        List<Row> unchanged = [];
        List<Row> updatedRows, insertedRows;
        if (_mode == ImportPassMode.Write)
        {
            // A matched record whose values are already what the import would write is left alone: not written, and counted as unchanged.
            if (updates.Count > 0)
            {
                var current = stored ?? await _records.GetRowsByIdsAsync(_plan.Destination, MappedFields(), updates.Select(r => r.MatchedId!.Value).ToList(), ct);
                unchanged = updates.Where(r => IsUnchanged(r, current)).ToList();
                if (unchanged.Count > 0) updates = updates.Except(unchanged).ToList();
            }
            updatedRows = await WriteAsync(updates, async batch =>
            {
                await _store.UpdateAsync(_plan.Destination, _plan.DestinationFields,
                    batch.Select(r => new ImportUpdateRow(r.MatchedId!.Value, r.Values)).ToList(), _userId, ct);
                return null;
            }, feedback);
            insertedRows = await WriteAsync(inserts, async batch =>
            {
                var ids = await _store.InsertAsync(_plan.Destination, _plan.DestinationFields,
                    batch.Select(r => (IReadOnlyDictionary<long, object?>)r.Values).ToList(), _userId, ct);
                for (var i = 0; i < batch.Count && i < ids.Count; i++) batch[i].InsertedId = ids[i];
                return ids;
            }, feedback);
        }
        else
        {
            updatedRows = updates;
            insertedRows = inserts;
        }

        var errored = feedback.Count(f => f.Issue.Outcome == ImportOutcome.Errored);
        var written = _mode == ImportPassMode.Write ? WrittenRows(insertedRows, updatedRows, unchanged) : null;
        return new ImportChunkOutcome(insertedRows.Count, updatedRows.Count, feedback.Count - errored, errored, feedback, unchanged.Count, written);
    }

    /// <summary>The destination fields the import writes (and the Record ID#): all that is needed to see whether a matched record would change.</summary>
    private IReadOnlyList<AppField> MappedFields()
    {
        var mapped = _plan.Mappings.Select(m => m.Destination.Fid).ToHashSet();
        return _plan.DestinationFields.Where(f => f.Fid.HasValue && (mapped.Contains(f.Fid) || ImportTypeCompatibility.IsRecordId(f))).ToList();
    }

    /// <summary>True when the record the row matched already holds every value the row would write. An encrypted field cannot be compared
    /// (its stored value is ciphertext), and a value of a kind that is not plainly comparable counts as changed: a write that was not
    /// needed is harmless, a skipped write that was is not.</summary>
    private bool IsUnchanged(Row row, IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> current)
    {
        if (!current.TryGetValue(row.MatchedId!.Value, out var existing) || row.Values.Count == 0) return false;
        foreach (var (fid, value) in row.Values)
        {
            var field = _plan.DestinationFields.FirstOrDefault(f => f.Fid == fid);
            if (field is null || field.IsEncrypted) return false;
            if (!existing.TryGetValue(PhysicalNaming.GetPhysicalColumnName(field), out var stored) || !ImportValueEquality.Same(stored, value)) return false;
        }
        return true;
    }

    private List<ImportWrittenRow> WrittenRows(List<Row> inserted, List<Row> updated, List<Row> unchanged)
    {
        var rows = new List<ImportWrittenRow>(inserted.Count + updated.Count + unchanged.Count);
        foreach (var r in inserted) if (r.InsertedId is { } id) rows.Add(Written(r, ImportOutcome.Inserted, id));
        foreach (var r in updated) rows.Add(Written(r, ImportOutcome.Updated, r.MatchedId!.Value));
        foreach (var r in unchanged) rows.Add(Written(r, ImportOutcome.Unchanged, r.MatchedId!.Value));
        rows.Sort((a, b) => a.SourceRowRef.CompareTo(b.SourceRowRef));
        return rows;
    }

    private ImportWrittenRow Written(Row row, string outcome, long recordId)
    {
        var (rowRef, sheet) = RowRef(row);
        return new ImportWrittenRow(rowRef, outcome, recordId, Carried(row), sheet is null ? null : $"Sheet '{sheet}'");
    }

    /// <summary>The row's number in its file or sheet (a person is shown the row where they would look for it), and the sheet's name.</summary>
    private (long Row, string? Sheet) RowRef(Row row)
    {
        if (_plan.File?.Sheets is not { } sheets) return (row.SourceId, null);
        var (sheetIndex, rowNumber) = ImportFileSheets.Split(row.SourceId);
        return (rowNumber, sheetIndex < sheets.Count ? sheets[sheetIndex].Name : null);
    }

    /// <summary>The values a row carries, as text, one per mapping: the source value, the fixed value, or what the formula returned.</summary>
    private string?[] Carried(Row row) => _plan.Mappings.Select((m, i) => m.Kind switch
    {
        ImportMappingSource.Static => m.StaticText,
        ImportMappingSource.Formula => row.FormulaResults?[i],
        _ => ImportFeedbackWriter.Format(row.Source.GetValueOrDefault(m.SourceColumn!))
    }).ToArray();

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

    /// <summary>The per-column rules that look at a row on its own, evaluated independently for each column: Require field leaves the row out
    /// (skipped, with the reason) and Ignore blanks keeps the row but drops the blank value, so an existing value is not overwritten. They
    /// come first, because the matching and required checks that follow need to see the values as they will be written.
    /// "Remove duplicates" is not here: it compares rows with each other, so it runs last (<see cref="RemoveDuplicateRows"/>).</summary>
    private List<Row> ApplyColumnRules(List<Row> rows, List<ImportFeedbackRow> feedback)
    {
        if (_rules.All(r => !r.Rule.RequireField && !r.Rule.IgnoreBlanks)) return rows;
        var kept = new List<Row>(rows.Count);
        foreach (var row in rows)
        {
            var skipped = false;
            foreach (var (field, rule) in _rules)
            {
                var fid = field.Fid!.Value;
                if (!IsBlank(row.Values.GetValueOrDefault(fid))) continue;
                if (rule.RequireField)
                {
                    Report(feedback, row, fid, ImportOutcome.Skipped, ImportReason.RequiredBlank,
                        $"'{ImportTypeCompatibility.DisplayName(field)}' is blank, and this import requires a value.");
                    skipped = true;
                    break;
                }
                if (rule.IgnoreBlanks) row.Values.Remove(fid);
            }
            if (!skipped) kept.Add(row);
        }
        return kept;
    }

    /// <summary>The rows of a chunk compete, in source order, for the values only one row may have. A row is left out (and says why) when it
    /// repeats a value another row of this run already has: a unique field or the merge key (an error, as the import says no two rows may share
    /// it) or a column with "Remove duplicates" (skipped on purpose; for a Copy, so is a value that already exists in the destination). A row
    /// takes its values only if it passes all of these, so a row that is left out never causes a later row to be left out because of it. This
    /// is the last row check before the write, and "Remove duplicates" is the last of the rules: the first row that survives everything is the
    /// one kept.</summary>
    private List<Row> ResolveCompetition(List<Row> rows, List<ImportFeedbackRow> feedback)
    {
        var deduped = _rules.Where(r => r.Rule.RemoveDuplicates).ToList();
        if (_unique.Count == 0 && !_keyChecksDuplicates && deduped.Count == 0) return rows;
        var kept = new List<Row>(rows.Count);
        var claims = new List<(int Fid, string Key)>();
        foreach (var row in rows)
        {
            claims.Clear();
            if (RepeatsInRun(row, feedback, claims) || RepeatsForRule(row, deduped, feedback, claims)) continue;
            foreach (var (fid, key) in claims) _tracker.MarkSeen(fid, key);
            kept.Add(row);
        }
        return kept;
    }

    /// <summary>True (after reporting an error) when the row repeats a unique value or merge key already used in this run; otherwise adds what
    /// the row would use to <paramref name="claims"/>.</summary>
    private bool RepeatsInRun(Row row, List<ImportFeedbackRow> feedback, List<(int Fid, string Key)> claims)
    {
        if (_keyChecksDuplicates && row.MatchKey is { } matchKey)
        {
            if (RepeatedValueMessage(_key!, matchKey) is { } repeated)
            {
                Report(feedback, row, _key!.Fid!.Value, ImportOutcome.Errored, ImportReason.DuplicateInRun, repeated);
                return true;
            }
            claims.Add((_key!.Fid!.Value, matchKey));
        }
        foreach (var field in _unique)
        {
            var fid = field.Fid!.Value;
            if (ImportKey.Normalize(row.Values.GetValueOrDefault(fid)) is not { } key) continue;
            if (RepeatedValueMessage(field, key) is { } message)
            {
                Report(feedback, row, fid, ImportOutcome.Errored, ImportReason.DuplicateInRun, message);
                return true;
            }
            claims.Add((fid, key));
        }
        return false;
    }

    /// <summary>True (after reporting it as skipped) when the row repeats a value in a column with "Remove duplicates"; otherwise adds the
    /// values it would use to <paramref name="claims"/>.</summary>
    private bool RepeatsForRule(Row row, List<(AppField Field, ImportColumnRule Rule)> deduped, List<ImportFeedbackRow> feedback, List<(int Fid, string Key)> claims)
    {
        foreach (var (field, _) in deduped)
        {
            var fid = field.Fid!.Value;
            // A merge drops the key from the values once the record is found; the value it was matched on is kept for this check.
            var value = fid == _key?.Fid && row.MatchKey is not null ? row.MatchKey : row.Values.GetValueOrDefault(fid);
            if (IsBlank(value) || ImportKey.Normalize(value) is not { } key) continue;
            var name = ImportTypeCompatibility.DisplayName(field);

            if (!_isMerge && _tracker.ExistsInDestination(fid, key))
                Report(feedback, row, fid, ImportOutcome.Skipped, ImportReason.DuplicateInDestination, $"'{name}' value '{key}' already exists in {_plan.Destination.Name}.");
            else if (_tracker.WasSeen(fid, key))
                Report(feedback, row, fid, ImportOutcome.Skipped, ImportReason.DuplicateInRun, $"'{name}' value '{key}' appeared in an earlier row of this import.");
            else { claims.Add((fid, key)); continue; }
            return true;
        }
        return false;
    }

    /// <summary>"Remove duplicates" on its own, for the scan that finds duplicate groups: the rows that reach the constraint stage there are
    /// those the rule has not already left out.</summary>
    private List<Row> RemoveDuplicateRows(List<Row> rows, List<ImportFeedbackRow> feedback)
    {
        var deduped = _rules.Where(r => r.Rule.RemoveDuplicates).ToList();
        if (deduped.Count == 0) return rows;
        var kept = new List<Row>(rows.Count);
        var claims = new List<(int Fid, string Key)>();
        foreach (var row in rows)
        {
            claims.Clear();
            if (RepeatsForRule(row, deduped, feedback, claims)) continue;
            foreach (var (fid, key) in claims) _tracker.MarkSeen(fid, key);
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
            else keyed.Add((row, key));
        }
        if (keyed.Count == 0) return [];

        var matches = _encryptedKeyIndex ?? await _store.FindRecordIdsAsync(_plan.Destination, _key, keyed.DistinctBy(k => k.Key, ImportKey.Comparer).Select(k => k.Row.Values[keyFid]!).ToList(), ct);
        foreach (var (row, key) in keyed)
        {
            row.MatchKey = key;
            if (matches.TryGetValue(key, out var recordId)) row.MatchedId = recordId;
        }
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

    /// <summary>Reports rows whose unique value belongs to a different record in the destination. (A value that repeats within the import is
    /// decided later, when the rows compete, so that only rows that survive every check can use a value up.)</summary>
    private async Task<List<Row>> RejectDestinationConflictsAsync(List<Row> rows, AppField field, List<ImportFeedbackRow> feedback, CancellationToken ct)
    {
        var fid = field.Fid!.Value;
        // Encrypted values cannot be compared with what is stored, so the database's own unique index is the check.
        if (field.IsEncrypted) return rows;
        var keyed = rows.Select(r => (Row: r, Key: ImportKey.Normalize(r.Values.GetValueOrDefault(fid)))).ToList();
        if (keyed.All(k => k.Key is null)) return rows;

        var owners = await _store.FindRecordIdsAsync(_plan.Destination, field,
            keyed.Where(k => k.Key is not null).DistinctBy(k => k.Key, ImportKey.Comparer).Select(k => k.Row.Values[fid]!).ToList(), ct);
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

    /// <summary>Why a unique value cannot be used again: it is part of a duplicate group the policy excludes entirely, or it was already used
    /// by an earlier row of this run. Null when the value is free. It only looks: a row claims its values (see <see cref="ResolveCompetition"/>)
    /// once it has passed every check.</summary>
    private string? RepeatedValueMessage(AppField field, string key)
    {
        var fid = field.Fid!.Value;
        var name = ImportTypeCompatibility.DisplayName(field);
        if (_tracker.IsInDuplicateGroup(fid, key))
            return $"'{name}' value '{key}' appears more than once in the source, so none of those rows were imported.";
        return _tracker.WasSeen(fid, key) ? $"'{name}' value '{key}' appears more than once in this import." : null;
    }

    /// <summary>Writes the rows in one batch; if the database rejects it, halves the batch until the offending rows
    /// are isolated, so only those rows are reported and everything else still imports. Returns the rows that were written.</summary>
    private async Task<List<Row>> WriteAsync(List<Row> rows, Func<List<Row>, Task<IReadOnlyList<long>?>> write, List<ImportFeedbackRow> feedback)
    {
        if (rows.Count == 0) return [];
        try
        {
            await write(rows);
            return rows;
        }
        catch (ImportRowRejectedException ex)
        {
            if (rows.Count == 1)
            {
                Report(feedback, rows[0], null, ImportOutcome.Errored, ImportReason.WriteFailed, ex.Message);
                return [];
            }
            var half = rows.Count / 2;
            var first = await WriteAsync(rows.GetRange(0, half), write, feedback);
            first.AddRange(await WriteAsync(rows.GetRange(half, rows.Count - half), write, feedback));
            return first;
        }
    }

    /// <summary>A blank is no value or only whitespace. An unchecked Boolean or a zero is a real value, not a blank.</summary>
    private static bool IsBlank(object? value) => value is null || (value is string s && string.IsNullOrWhiteSpace(s));

    private void Report(List<ImportFeedbackRow> sink, Row row, int? fid, string outcome, string reason, string message, long? existingRecord = null)
    {
        // Reading every sheet numbers rows across sheets; a person is shown the row in its sheet, and the sheet's name.
        var (rowRef, sheetName) = RowRef(row);
        if (sheetName is not null) message = $"Sheet '{sheetName}': {message}";
        if (_tableLabel is not null) message = $"{_tableLabel}: {message}";
        var issue = new ImportRunIssue
        {
            SourceRowRef = rowRef, ColumnFid = fid, Outcome = outcome, ReasonCode = reason, ExistingRecordRef = existingRecord,
            Message = message.Length <= 500 ? message : message[..500]
        };
        // The carried values are read only now, for rejected rows, so a clean row costs nothing.
        var carried = Carried(row);
        // Several tables: the line also names the field it refers to (field ids repeat across tables) and the table it was meant for.
        string? columnLabel = null;
        if (_tableLabel is not null && fid is { } f && _plan.DestinationFields.FirstOrDefault(d => d.Fid == f) is { } field)
            columnLabel = ImportTypeCompatibility.DisplayName(field);
        sink.Add(new ImportFeedbackRow(issue, carried, _tableLabel, columnLabel, row.MatchedId));
    }
}
