namespace PowerBase.Application.QbMigration;

/// <summary>Everything the migration report shows for one QuickBase app migrated into one PowerBase app (all runs of it).
/// Collected read-only from the qbm state, the live PowerBase data and QuickBase; written by <see cref="QbmReportWriter"/>.</summary>
public sealed record QbmReport
{
    public required string AppName { get; init; }
    public required string QbRealm { get; init; }
    public required string QbAppId { get; init; }
    public required long PbAppId { get; init; }
    public required long TenantId { get; init; }
    public required DateTime GeneratedOn { get; init; }
    /// <summary>Lists longer than this are cut in the workbook (totals stay exact).</summary>
    public int RowCap { get; init; } = 200_000;

    public IReadOnlyList<Run> Runs { get; init; } = [];
    public IReadOnlyList<Table> Tables { get; init; } = [];
    public IReadOnlyList<Field> Fields { get; init; } = [];
    public IReadOnlyList<Chunk> Chunks { get; init; } = [];
    public IReadOnlyList<Issue> Issues { get; init; } = [];
    public long IssueTotal { get; init; }
    /// <summary>Issues of the codes that mean a value was not migrated (listed separately so the cap on Issues can't hide them).</summary>
    public IReadOnlyList<Issue> ValueIssues { get; init; } = [];
    public long ValueIssueTotal { get; init; }
    public IReadOnlyList<BrokenLink> BrokenLinks { get; init; } = [];
    public long BrokenLinkTotal { get; init; }
    public IReadOnlyList<Attachment> Attachments { get; init; } = [];
    public IReadOnlyList<User> Users { get; init; } = [];
    public IReadOnlyList<DeadLetter> DeadLetters { get; init; } = [];
    /// <summary>QuickBase tables with no PowerBase table (QBM_TABLE_NOT_FOUND).</summary>
    public IReadOnlyList<string> TablesNotFound { get; init; } = [];

    public sealed record Run(long Id, string Status, string? Control, DateTime CreatedOn, DateTime? ModifiedOn);

    /// <summary>One QuickBase table, from the run that owns it (or the latest run when none loaded it).</summary>
    public sealed record Table(long TableMapId, long RunId, string QbTableId, string Name, long PbTableId, int Tier, string Status,
        long? QbRecords, long? QbMinId, long? QbMaxId, long PbRecords, long? PbMinId, long? PbMaxId,
        DateTime? ValidatedOn, long? ComparedRecords, int? Gate3Differences, int? Gate7NotEncrypted, long BrokenLinks)
    {
        public bool Loaded => Status is "loaded" or "validated";
        public bool Gate1 => PbRecords == (QbRecords ?? 0);
        public bool Gate2 => (QbRecords ?? 0) == 0 || (PbMinId == QbMinId && PbMaxId == QbMaxId);
    }

    public sealed record Field(long TableMapId, string QbTableId, string Table, int QbFid, string Label, string QbType, string Action,
        int? PbFid, string? PbType, string? Reason);

    public sealed record Chunk(long Id, long RunId, string QbTableId, string Table, long FromRecordId, long ToRecordId, string Status,
        int? RowsRead, int? RowsWritten, int Attempts, DateTime? StartedOn, DateTime? FinishedOn, string? LastError)
    {
        public double? Seconds => StartedOn is { } s && FinishedOn is { } f && f >= s ? (f - s).TotalSeconds : null;
    }

    public sealed record Issue(long Id, long RunId, long? TableMapId, string? QbTableId, string? Table, long? ChunkId, long? RecordId,
        int? QbFid, string Severity, string Code, string Message, DateTime CreatedOn);

    public sealed record BrokenLink(string Table, string QbTableId, int LinkFid, string ParentTable, long RecordId, long PointsTo);

    public sealed record Attachment(string QbTableId, string Table, int QbFid, string Field, long RecordsWithFile);

    public sealed record User(long UserId, string? Email, string? Name, string? Role, string? AppStatus, bool? Active,
        DateTime? AccountCreated, DateTime? AddedToApp);

    public sealed record DeadLetter(long RunId, long ChunkId, string? Table, string? Reason, string? Description, DateTime EnqueuedOn);
}

/// <summary>The report's judgements, kept apart from the workbook so they can be tested.</summary>
public static class QbmReportRules
{
    public const string Current = "Current", Superseded = "Superseded", Resolved = "Resolved", Info = "Info";

    /// <summary>Codes whose issue means a value of a record was not carried over as it is in QuickBase.</summary>
    public static readonly IReadOnlySet<string> ValueCodes = new HashSet<string>(
        ["QBM_USER_NOT_MAPPED", "QBM_SYSTEM_USER_NOT_MAPPED", "QBM_REFERENCE_NOT_FOUND", "QBM_VALUE_NOT_CONVERTED"]);

    private static readonly IReadOnlySet<string> GateCodes = new HashSet<string>(
        ["QBM_GATE3_MISMATCH", "QBM_GATE3_MISSING_RECORD", "QBM_GATE3_EXTRA_RECORD", "QBM_GATE4_ORPHAN_REFERENCE", "QBM_GATE7_NOT_ENCRYPTED", "QBM_GATE_TOO_MANY"]);

    /// <summary>Whether an issue still describes the data. Validations append their findings, so only those of a table's latest
    /// validation are current; a field plan belongs to the run that loaded the table.</summary>
    public static (string State, string Note) StateOf(QbmReport.Issue issue, IReadOnlyDictionary<long, QbmReport.Table> tablesByMap,
        IReadOnlySet<string> loadedQbTables)
    {
        var owned = issue.TableMapId is { } id && tablesByMap.ContainsKey(id);
        var table = owned ? tablesByMap[issue.TableMapId!.Value] : null;
        if (issue.Code == "QBM_TABLE_IN_OTHER_RUN")
            return (Info, "This run left the table to the run that already had it.");
        if (GateCodes.Contains(issue.Code))
        {
            if (issue.Code == "QBM_GATE4_ORPHAN_REFERENCE")
                return (Superseded, "Gate 4 lists at most 50 per field and repeats on each validation; the exact current list is on Broken Links.");
            if (table?.ValidatedOn is { } validated && issue.CreatedOn < validated)
                return (Superseded, "From an earlier validation; the latest one is on the Tables sheet.");
            return (Current, "");
        }
        if (!owned)
        {
            if (issue.Code == "QBM_FIELD_BLOCK" && issue.QbTableId is { } qbTable && loadedQbTables.Contains(qbTable))
                return (Resolved, "The table was loaded by a later run.");
            if (issue.Code.StartsWith("QBM_FIELD_", StringComparison.Ordinal))
                return (Superseded, "Plan of a run that did not load this table.");
        }
        return (Current, ValueCodes.Contains(issue.Code) ? "Listed on Not Migrated." : "");
    }

    public sealed record NotMigratedItem(string Category, string? Table, string? QbTableId, long? RecordId, int? QbFid, string? Field,
        long? RecordsAffected, string What, string Why, string NextStep);

    /// <summary>Every table, field, file and value that was not carried over, with the reason and what to do.</summary>
    public static IReadOnlyList<NotMigratedItem> NotMigrated(QbmReport report)
    {
        var items = new List<NotMigratedItem>();
        foreach (var name in report.TablesNotFound)
            items.Add(new("Table not created", name, null, null, null, null, null, "The whole table was not migrated",
                "The QBL import didn't create this table in PowerBase.", "Import the table, then run the migration for it"));
        foreach (var t in report.Tables.Where(t => t.Status == "blocked"))
            items.Add(new("Table not loaded", t.Name, t.QbTableId, null, null, null, t.QbRecords,
                "No records of this table were loaded", "Blocked by its field mapping; see Fields (action 'block') and Issues.",
                "Fix the blocked fields, then run the migration again"));

        var byTable = report.Tables.ToDictionary(t => t.TableMapId);
        var attachments = report.Attachments.ToDictionary(a => (a.QbTableId, a.QbFid));
        foreach (var f in report.Fields.Where(f => f.Action is "skip" or "block"))
        {
            if (f.Action == "block" && byTable.TryGetValue(f.TableMapId, out var bt) && bt.Status == "blocked") continue; // covered by the table row
            if (f.QbType.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var files = attachments.TryGetValue((f.QbTableId, f.QbFid), out var a) ? a.RecordsWithFile : (long?)null;
                items.Add(new("File attachment", f.Table, f.QbTableId, null, f.QbFid, f.Label, files,
                    files switch { null => "Files in this field were not copied (count not available)", 0 => "Field not created; no record holds a file", _ => $"{files:N0} record(s) hold a file; the files were not copied" },
                    "Attachments move in a later stage, after the blob-storage privacy fix.", files == 0 ? "None (no data)" : "Copy the files in the attachments stage"));
                continue;
            }
            var empty = f.Reason?.Contains("empty in every QuickBase record", StringComparison.Ordinal) == true;
            items.Add(new(empty ? "Field not created" : "Field not migrated", f.Table, f.QbTableId, null, f.QbFid, f.Label, empty ? 0 : null,
                $"QuickBase {f.QbType} field {(f.PbFid is null ? "has no PowerBase field" : "was not written")}", f.Reason ?? "",
                empty ? "None (no data lost)" : "Review the field mapping"));
        }

        foreach (var i in report.ValueIssues)
        {
            var field = Quoted(i.Message);
            var oncePerChunk = i.Message.Contains("first record in this chunk", StringComparison.Ordinal);
            items.Add(i.Code switch
            {
                "QBM_USER_NOT_MAPPED" => new("User value left empty", i.Table, i.QbTableId, i.RecordId, i.QbFid, field, oncePerChunk ? null : 1,
                    oncePerChunk ? "The user was stored empty in this record and possibly others in the same chunk" : "The user in this field was stored empty",
                    UserReason(i.Message), "Create or link the user (users command), then reload; or set it by hand"),
                "QBM_SYSTEM_USER_NOT_MAPPED" => new("Owner replaced", i.Table, i.QbTableId, i.RecordId, i.QbFid, $"System field {i.QbFid}", oncePerChunk ? null : 1,
                    "Record Owner / Last Modified By was set to the migration user", UserReason(i.Message), "Create or link the user (users command), then reload"),
                "QBM_REFERENCE_NOT_FOUND" => new("Link value left empty", i.Table, i.QbTableId, i.RecordId, i.QbFid, field, 1,
                    $"Link to parent key \"{Key(i.Message)}\" was stored empty", $"No parent record has the key \"{Key(i.Message)}\".",
                    "Create the parent record or fix the link by hand"),
                _ => new("Value left empty", i.Table, i.QbTableId, i.RecordId, i.QbFid, field, 1, "The value couldn't be converted and was stored empty",
                    i.Message, "Fix the value by hand"),
            });
        }
        return items;
    }

    private static string? Quoted(string message)
    {
        var start = message.IndexOf('\'');
        var end = start < 0 ? -1 : message.IndexOf('\'', start + 1);
        return end > start ? message[(start + 1)..end] : null;
    }

    private static string Key(string message)
    {
        const string marker = "key '";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        start += marker.Length;
        var end = message.IndexOf("'; stored", start, StringComparison.Ordinal);
        return (end > start ? message[start..end] : message[start..]).Trim('"');
    }

    private static string UserReason(string message)
    {
        const string marker = "no PowerBase user for ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return message;
        start += marker.Length;
        var end = message.IndexOfAny([';', ','], start);
        var who = (end > start ? message[start..end] : "").Trim();
        return who.Length == 0
            ? "The QuickBase user value has no email address, so it can't be matched to a PowerBase user."
            : $"{who} has no PowerBase user.";
    }
}
