using System.Text.Json;
using System.Text.Json.Serialization;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Imports;

public static class ImportTypes
{
    public const string Copy = "copy";
    public const string Merge = "merge";
}

public static class ImportRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Success = "success";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class ImportTrigger
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
    public const string Api = "api";
}

public static class ImportMappingSource
{
    public const string Dynamic = "dynamic";
    public const string Static = "static";
    public const string Formula = "formula";
}

/// <summary>What happens when rows break a unique constraint (a duplicate value in the file or in the destination).
/// Rows with other problems (bad value, missing required value) are always reported and never block the rest.</summary>
public static class ImportConstraintPolicy
{
    /// <summary>Import every row that is valid. Of rows sharing a unique value, the first is imported and the rest are reported.</summary>
    public const string ImportValid = "importValid";
    /// <summary>Import only rows without a duplicate problem: every row in a duplicate group is reported, none imported.</summary>
    public const string ExcludeDuplicateGroups = "excludeDuplicateGroups";
    /// <summary>Do not import anything if any row would be rejected; the rejected rows are reported.</summary>
    public const string AbortIfAnyIssue = "abortIfAnyIssue";

    public static bool IsValid(string? policy) => policy is ImportValid or ExcludeDuplicateGroups or AbortIfAnyIssue;
}

public static class ImportOutcome
{
    /// <summary>Left out on purpose by a column rule. Not a failure.</summary>
    public const string Skipped = "skipped";
    /// <summary>Could not be imported because of a problem with the data or a constraint.</summary>
    public const string Errored = "errored";
    // What happened to a row that was imported. Only the details file names these: nothing is recorded for them as an issue.
    public const string Inserted = "inserted";
    public const string Updated = "updated";
    /// <summary>A merge matched the record, but it already held every value.</summary>
    public const string Unchanged = "unchanged";
}

/// <summary>Stable reason codes written to run issues and the feedback file.</summary>
public static class ImportReason
{
    public const string RequiredMissing = "RequiredMissing";
    /// <summary>Skipped by a "Require field" rule: the mapped column is blank.</summary>
    public const string RequiredBlank = "RequiredBlank";
    public const string DuplicateInDestination = "DuplicateInDestination";
    public const string DuplicateInRun = "DuplicateInRun";
    public const string TypeMismatch = "TypeMismatch";
    /// <summary>The value breaks the field's own format rules (maximum length or pattern).</summary>
    public const string FormatViolation = "FormatViolation";
    /// <summary>A formula mapping could not be calculated for the row.</summary>
    public const string FormulaError = "FormulaError";
    public const string ConstraintViolation = "ConstraintViolation";
    public const string WriteFailed = "WriteFailed";
    public const string MergeKeyMissing = "MergeKeyMissing";
    /// <summary>A merge matched a record but every mapped value was a blank that "Ignore blanks" skips.</summary>
    public const string NoChanges = "NoChanges";
}

/// <summary>One destination field and where its value comes from. Fields are addressed by Fid so a rename
/// never breaks a saved import.</summary>
public sealed class ImportFieldMapping
{
    public int DestFid { get; set; }
    public string Source { get; set; } = ImportMappingSource.Dynamic;
    public int? SourceFid { get; set; }
    public string? StaticValue { get; set; }
    public string? Formula { get; set; }
    public bool DoNotImport { get; set; }
}

/// <summary>Data-quality rules for one mapped column, evaluated independently of every other column.</summary>
public sealed class ImportColumnRule
{
    public int DestFid { get; set; }
    /// <summary>Rows whose value repeats one already seen (in this run, and in the destination for a Copy) are left out.</summary>
    public bool RemoveDuplicates { get; set; }
    /// <summary>Rows with a blank value in this column are left out instead of being written with a blank.</summary>
    public bool RequireField { get; set; }
    /// <summary>A blank value is not written: an existing value is kept, and a new record gets the field's default.</summary>
    public bool IgnoreBlanks { get; set; }
}

/// <summary>One more table an import fills from the same source rows in the same run: its own import type, merge key, mappings and column
/// rules. The source, its conditions, the duplicate policy, the people to tell and the schedule are the import's, shared by every table.</summary>
public sealed class ImportTargetConfig
{
    public Guid DestinationTableId { get; set; }
    public string ImportType { get; set; } = ImportTypes.Copy;
    public int? MergeKeyFid { get; set; }
    // A client may send null for a list; it means "none", never a crash further on.
    private List<ImportFieldMapping> _mappings = new();
    private List<ImportColumnRule> _columnRules = new();
    public List<ImportFieldMapping> Mappings { get => _mappings; set => _mappings = value ?? new(); }
    public List<ImportColumnRule> ColumnRules { get => _columnRules; set => _columnRules = value ?? new(); }
    /// <summary>Only the source rows that match go into this table. A row must first pass the import's own conditions (which decide what is read
    /// at all); these then decide whether this table gets it. None means every row that was read.</summary>
    public FilterGroup? Conditions { get; set; }
}

/// <summary>The saved configuration of an import (everything except identity/audit columns). Also the shape
/// of the create/update request body, so the API carries no extra wrapper types.</summary>
public sealed class ImportDefinitionConfig
{
    public string Name { get; set; } = "";
    /// <summary>"table" (copy from another table) or "file" (read a CSV / Excel file). A file import has no source table.</summary>
    public string SourceKind { get; set; } = ImportSourceKinds.Table;
    public Guid SourceTableId { get; set; }
    /// <summary>How the file is read; present exactly when <see cref="SourceKind"/> is "file".</summary>
    public ImportFileOptions? File { get; set; }
    public string ImportType { get; set; } = ImportTypes.Copy;
    public int? MergeKeyFid { get; set; }
    public FilterGroup? Conditions { get; set; }
    public List<ImportFieldMapping> Mappings { get; set; } = new();
    public List<ImportColumnRule> ColumnRules { get; set; } = new();
    public string ConstraintPolicy { get; set; } = ImportConstraintPolicy.ImportValid;
    /// <summary>People to email when a run finishes, besides whoever started it.</summary>
    public List<string> NotifyEmails { get; set; } = new();
    /// <summary>When the import runs by itself; none means it only runs when someone starts it. Saved with the definition, not part of a run.</summary>
    public ImportSchedule? Schedule { get; set; }
    /// <summary>Other tables filled from the same rows in the same run, besides the import's own table (whose settings are the fields
    /// above). Empty for an import into one table, which is how every import saved before this existed reads.</summary>
    public List<ImportTargetConfig> AdditionalTargets { get => _additionalTargets; set => _additionalTargets = value ?? new(); }
    private List<ImportTargetConfig> _additionalTargets = new();
    /// <summary>Columns that exist only inside this import (fixed text or formulas). Any mapping, of any table the import fills, may use one
    /// as its source, and other virtual columns' formulas may refer to one by name. None for an import saved before they existed.</summary>
    public List<ImportVirtualColumn> VirtualColumns { get => _virtualColumns; set => _virtualColumns = value ?? new(); }
    private List<ImportVirtualColumn> _virtualColumns = new();
    /// <summary>The same as <see cref="ImportTargetConfig.Conditions"/>, for the import's own table (the one the fields above belong to).
    /// Only for an import that fills several tables: with one table, <see cref="Conditions"/> already says which rows go in.</summary>
    public FilterGroup? TableConditions { get; set; }
}

public static class ImportJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string? json) => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

// ---- Read models (only what the UI needs) ----

public sealed record ImportDefinitionListItem(
    Guid PublicId, string Name, string ImportType, Guid SourceTableId, string SourceTableName,
    int MappingCount, bool NeedsAttention, string? AttentionReason, string? LastRunStatus, DateTime? LastRunOn,
    IReadOnlyList<string> NotifyEmails, string? ScheduleSummary, DateTime? NextRunOn, string SourceKind = ImportSourceKinds.Table,
    int TableCount = 1, Guid DestinationTableId = default, string DestinationTableName = "");

public sealed record ImportDefinitionDetail(
    Guid PublicId, string Name, Guid DestinationTableId, Guid SourceTableId, string ImportType, int? MergeKeyFid,
    FilterGroup? Conditions, IReadOnlyList<ImportFieldMapping> Mappings, IReadOnlyList<ImportColumnRule> ColumnRules,
    string ConstraintPolicy, IReadOnlyList<string> NotifyEmails, bool NeedsAttention, string? AttentionReason,
    ImportSchedule? Schedule, DateTime? NextRunOn, string SourceKind = ImportSourceKinds.Table, ImportFileOptions? File = null,
    IReadOnlyList<ImportTargetConfig>? AdditionalTargets = null, IReadOnlyList<ImportVirtualColumn>? VirtualColumns = null, FilterGroup? TableConditions = null);

public sealed record ImportRunListItem(
    Guid PublicId, string TriggeredBy, string Status, byte Progress, long RowsRead, long Inserted, long Updated,
    long Skipped, long Errored, DateTime? StartedOn, DateTime? CompletedOn, long Unchanged = 0);

public sealed record ImportRunIssueItem(
    long? SourceRowRef, int? ColumnFid, string Outcome, string ReasonCode, string Message, long? ExistingRecordRef);

/// <summary>A run with its first issues. The feedback file's storage location is never sent to the client; it is
/// downloaded through an authorised endpoint, so only "there is one" is exposed.</summary>
/// <summary>A run as the "my imports" notice shows it: enough to tell the user it finished, and to link to it.</summary>
public sealed record ImportRunNotice(
    Guid RunId, string DefinitionName, Guid AppId, Guid TableId, string Status, byte Progress, long RowsRead, long Inserted,
    long Updated, long Skipped, long Errored, DateTime? CompletedOn);

/// <summary>The user's active runs, and those that finished since they last asked. <see cref="ServerTime"/> is the clock to
/// ask "since" with next time, so a skewed browser clock cannot miss or repeat a run.</summary>
public sealed record ImportRunNotices(DateTime ServerTime, IReadOnlyList<ImportRunNotice> Runs);

/// <summary>One table's share of a run that filled several.</summary>
public sealed record ImportRunTargetItem(Guid TableId, string TableName, long Inserted, long Updated, long Skipped, long Errored, long Unchanged = 0, bool HasDetails = false);

public sealed record ImportRunDetail(
    ImportRunListItem Run, string? ErrorDetail, bool HasFeedback, IReadOnlyList<ImportRunIssueItem> Issues, int IssueTotal,
    IReadOnlyList<ImportRunTargetItem>? Targets = null, string? SourceFileName = null, bool HasSourceFile = false, bool FilesExpired = false);
