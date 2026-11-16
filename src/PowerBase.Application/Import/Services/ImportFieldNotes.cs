using System.Text.Json;
using System.Text.Json.Nodes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Reporting;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Import.Services;

/// <summary>One field-level note an import wants to store, before the field's database id is known. The field is found again by
/// the name the import created it with (then its label, then its source number).</summary>
public sealed class ImportFieldNoteDraft
{
    public string TableName { get; init; } = string.Empty;
    public string FieldName { get; init; } = string.Empty;
    public string? FieldLabel { get; init; }
    public int? SourceFid { get; init; }

    public string Severity { get; init; } = ImportNoteSeverities.Warning;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? RecommendedAction { get; init; }
    public string? SourceLogicalRef { get; init; }
    public string? SourceType { get; init; }
    public bool IsPlaceholder { get; init; }
    public string? OriginalDefinition { get; init; }
    public string? OriginalMetadata { get; init; }
}

/// <summary>Turns what an import found into <see cref="ImportFieldNoteDraft"/>s: one per stand-in field (with the complete original
/// definition), one per formula that had to be rewritten, and one per other field-level issue the import reported.</summary>
public static class ImportFieldNoteBuilder
{
    /// <summary>The code of a note about a formula that did not compile and was saved as a formula field, as written.</summary>
    public const string BROKEN_FORMULA_CODE = "FORMULA_SAVED_BROKEN";

    private static readonly System.Text.RegularExpressions.Regex UnknownField =
        new(@"^Unknown field \[(?<name>.+)\]\.?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The compile diagnostics of a formula, in plain words: "Unknown field [X]." becomes "the field 'X' does not exist on this table".</summary>
    public static string FriendlyDiagnostics(IEnumerable<string> diagnostics)
    {
        var parts = diagnostics
            .Select(d => d.Trim())
            .Where(d => d.Length > 0)
            .Select(d => UnknownField.Match(d) is { Success: true } m ? $"the field '{m.Groups["name"].Value}' does not exist on this table" : d.TrimEnd('.'))
            .Distinct()
            .ToList();
        return parts.Count == 0 ? "the formula has an error" : string.Join("; ", parts);
    }

    /// <summary>The audit-history commit message for a field's import notes: what the import could not carry over, in plain words
    /// (the worst note first, stand-ins before cosmetic notes), and how many more there are. At most 500 characters.</summary>
    public static string CommitMessage(IReadOnlyList<ImportFieldNoteDraft> drafts)
    {
        var ordered = drafts
            .OrderBy(d => d.Severity == ImportNoteSeverities.Information ? 1 : 0)
            .ThenBy(d => d.IsPlaceholder || d.Code == BROKEN_FORMULA_CODE ? 0 : 1)
            .ToList();
        var first = ordered[0].Message.Trim();
        if (first.Length > 0 && first[^1] is not ('.' or '!' or '?')) first += ".";

        var message = $"Imported from Quickbase. {first}";
        if (ordered.Count > 1) message += $" (+{ordered.Count - 1} more - see the change details.)";
        return message.Length > FieldVersionService.MAX_COMMIT_MESSAGE ? message[..(FieldVersionService.MAX_COMMIT_MESSAGE - 1)] + "…" : message;
    }

    /// <summary>The change rows of a field's Import version: every note, what to do about it, and the complete original definition.</summary>
    public static IReadOnlyList<FieldChangeEntry> VersionChanges(IReadOnlyList<ImportFieldNoteDraft> drafts)
    {
        var rows = new List<FieldChangeEntry>();
        foreach (var d in drafts)
        {
            rows.Add(new FieldChangeEntry(d.Severity == ImportNoteSeverities.Information ? "Import note" : "Import warning", null, d.Message));
            if (!string.IsNullOrWhiteSpace(d.RecommendedAction)) rows.Add(new FieldChangeEntry("What to do", null, d.RecommendedAction));
            if (!string.IsNullOrWhiteSpace(d.SourceType)) rows.Add(new FieldChangeEntry("Quickbase field type", null, d.SourceType));
            if (!string.IsNullOrEmpty(d.OriginalDefinition)) rows.Add(new FieldChangeEntry("Original Quickbase definition", null, d.OriginalDefinition));
            if (!string.IsNullOrEmpty(d.OriginalMetadata)) rows.Add(new FieldChangeEntry("Original Quickbase field details", null, d.OriginalMetadata));
        }
        return rows;
    }

    public const string PLACEHOLDER_ACTION =
        "This field is a stand-in. Open it, re-create it as the original field type using the original definition kept in this note, then resolve the note.";

    public const string FORMULA_PLACEHOLDER_ACTION =
        "This field is a stand-in for a formula that could not be compiled. Change it to a formula field, paste the original formula kept in this note, fix any field names that no longer exist, then resolve the note.";

    private static readonly HashSet<string> FieldElementTypes = new(StringComparer.Ordinal)
    {
        "Field", "Lookup Field", "Summary Field", "Relationship", "Report Link",
    };

    /// <param name="lateDrafts">Notes the importer collected while it ran: stand-ins it had to create late and rewritten formulas.</param>
    public static List<ImportFieldNoteDraft> Build(PblDocument document, IReadOnlyList<ImportSkippedItem> skipped, IEnumerable<ImportFieldNoteDraft> lateDrafts)
    {
        var drafts = new List<ImportFieldNoteDraft>(lateDrafts);
        var standIns = new HashSet<(string Table, string Field)>(drafts.Where(d => d.IsPlaceholder || d.Code == BROKEN_FORMULA_CODE).Select(d => (d.TableName.ToLowerInvariant(), d.FieldName.ToLowerInvariant())));

        foreach (var table in document.Tables ?? [])
            foreach (var field in (table.Fields ?? []).Where(f => f.Placeholder is not null))
            {
                var p = field.Placeholder!;
                standIns.Add((table.Name.ToLowerInvariant(), field.Name.ToLowerInvariant()));
                drafts.Add(new ImportFieldNoteDraft
                {
                    TableName = table.Name,
                    FieldName = field.Name,
                    FieldLabel = field.Label,
                    SourceFid = field.SourceId,
                    Severity = ImportNoteSeverities.Warning,
                    Code = p.Code,
                    Message = $"{p.Reason}. A placeholder {field.TypeCode} field was created in its place.",
                    RecommendedAction = PlaceholderAction(p.OriginalDefinition, field.SourceType),
                    SourceLogicalRef = field.LogicalRef,
                    SourceType = field.SourceType,
                    IsPlaceholder = true,
                    OriginalDefinition = p.OriginalDefinition,
                    OriginalMetadata = WithFullDescription(p.OriginalMetadataJson, p.FullDescription),
                });
            }

        var resolver = new ImportRefResolver(document);
        foreach (var item in skipped.Where(s => !string.IsNullOrEmpty(s.LogicalRef)))
        {
            // Shortened label/description: the importer already wrote a note that keeps the complete text.
            if (item.Code == "IMPORT_FIELD_TEXT_SHORTENED") continue;

            var info = resolver.Resolve(item.LogicalRef);
            if (info?.TableName is null || info.FieldName is null || !FieldElementTypes.Contains(info.ElementType)) continue;
            // A stand-in's note already carries why it is one.
            if (standIns.Contains((info.TableName.ToLowerInvariant(), (info.InternalName ?? info.FieldName).ToLowerInvariant()))) continue;

            var (severity, action) = ImportIssueClassifier.Classify(string.IsNullOrEmpty(item.Code) ? null : item.Code, item.Reason, ImportIssueSeverity.Warning);
            drafts.Add(new ImportFieldNoteDraft
            {
                TableName = info.TableName,
                FieldName = info.InternalName ?? info.FieldName,
                FieldLabel = info.FieldName,
                SourceFid = info.SourceFid,
                Severity = severity == ImportIssueSeverity.Information ? ImportNoteSeverities.Information : ImportNoteSeverities.Warning,
                Code = item.Code,
                Message = item.Reason,
                RecommendedAction = action,
                SourceLogicalRef = item.LogicalRef,
            });
        }

        return drafts;
    }

    public static string PlaceholderAction(string? originalDefinition, string? sourceType) =>
        sourceType is not null && sourceType.EndsWith("::Formula", StringComparison.Ordinal) && !string.IsNullOrEmpty(originalDefinition)
            ? FORMULA_PLACEHOLDER_ACTION
            : PLACEHOLDER_ACTION;

    /// <summary>Adds the source description (when it was longer than a field description holds) to the metadata snapshot, so nothing is cut.</summary>
    public static string? WithFullDescription(string? metadataJson, string? fullDescription)
    {
        if (string.IsNullOrEmpty(fullDescription)) return metadataJson;
        try
        {
            var node = string.IsNullOrEmpty(metadataJson) ? new JsonObject() : JsonNode.Parse(metadataJson) as JsonObject ?? new JsonObject();
            node["fullDescription"] = fullDescription;
            return node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException)
        {
            return metadataJson;
        }
    }
}

/// <param name="Stored">Notes stored.</param>
/// <param name="Unmatched">Notes whose field could not be found.</param>
/// <param name="VersionsWritten">Fields that got an Import entry in their audit history.</param>
/// <param name="VersionFailures">Fields whose audit-history entry could not be written (their notes are still stored).</param>
public sealed record ImportNoteRecordResult(int Stored, IReadOnlyList<ImportFieldNoteDraft> Unmatched, int VersionsWritten = 0, int VersionFailures = 0);

/// <summary>Writes an import's field notes to <c>meta.AppFieldImportNote</c> once the fields exist. Finds each field by the name the
/// import gave it, then its label, then its source number. A note whose field cannot be found is handed back, never dropped.</summary>
public class ImportFieldNoteRecorder
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppFieldImportNoteRepository _noteRepo;
    private readonly FieldVersionService? _versionService;

    public ImportFieldNoteRecorder(IAppRepository appRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IAppFieldImportNoteRepository noteRepo, FieldVersionService? versionService = null)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _noteRepo = noteRepo;
        _versionService = versionService;
    }

    public async Task<ImportNoteRecordResult> RecordAsync(Guid appPublicId, IReadOnlyList<ImportFieldNoteDraft> drafts, CancellationToken ct)
    {
        if (drafts.Count == 0) return new ImportNoteRecordResult(0, []);

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tables = (await _tableRepo.ListByAppAsync(appId, ct)).GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var notes = new List<AppFieldImportNote>();
        var unmatched = new List<ImportFieldNoteDraft>();
        var perField = new Dictionary<long, (AppField Field, List<ImportFieldNoteDraft> Drafts)>();

        foreach (var group in drafts.GroupBy(d => d.TableName, StringComparer.OrdinalIgnoreCase))
        {
            if (!tables.TryGetValue(group.Key, out var table))
            {
                unmatched.AddRange(group);
                continue;
            }

            var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            foreach (var draft in group)
            {
                var field = Find(fields, draft);
                if (field is null)
                {
                    unmatched.Add(draft);
                    continue;
                }

                if (!perField.TryGetValue(field.Id, out var entry))
                    perField[field.Id] = entry = (field, []);
                entry.Drafts.Add(draft);

                notes.Add(new AppFieldImportNote
                {
                    AppId = appId,
                    AppTableId = table.Id,
                    AppFieldId = field.Id,
                    Severity = ImportNoteSeverities.IsValid(draft.Severity) ? draft.Severity : ImportNoteSeverities.Warning,
                    Code = Cut(draft.Code, 100) ?? string.Empty,
                    Message = draft.Message,
                    RecommendedAction = Cut(draft.RecommendedAction, 1000),
                    SourceLogicalRef = Cut(draft.SourceLogicalRef, 300),
                    SourceType = Cut(draft.SourceType, 200),
                    SourceFieldId = field.Fid,
                    IsPlaceholder = draft.IsPlaceholder,
                    OriginalDefinition = draft.OriginalDefinition,
                    OriginalMetadata = draft.OriginalMetadata,
                });
            }
        }

        // The field's audit history is where an import's warnings are shown: one Import entry per field, its commit message saying in plain
        // words what the import could not carry over, its change rows holding the details and the complete original definition.
        var versionsWritten = 0;
        var versionFailures = 0;
        if (_versionService is not null)
            foreach (var (field, fieldDrafts) in perField.Values)
            {
                try
                {
                    await _versionService.RecordImportVersionAsync(field, ImportFieldNoteBuilder.CommitMessage(fieldDrafts), ImportFieldNoteBuilder.VersionChanges(fieldDrafts), ct);
                    versionsWritten++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    versionFailures++;
                }
            }

        await _noteRepo.AddManyAsync(notes, ct);
        return new ImportNoteRecordResult(notes.Count, unmatched, versionsWritten, versionFailures);
    }

    private static AppField? Find(IReadOnlyList<AppField> fields, ImportFieldNoteDraft draft)
    {
        var byName = fields.FirstOrDefault(f => string.Equals(f.Name, draft.FieldName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        var label = (draft.FieldLabel ?? draft.FieldName).Trim();
        var byLabel = fields.FirstOrDefault(f => string.Equals((f.Label ?? "").Trim(), label, StringComparison.OrdinalIgnoreCase));
        if (byLabel is not null) return byLabel;

        return draft.SourceFid is { } fid ? fields.FirstOrDefault(f => f.Fid == fid) : null;
    }

    private static string? Cut(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
