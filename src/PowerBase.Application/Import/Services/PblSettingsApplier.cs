using PowerBase.Application.Formulas;
using System.Text.Json;
using PowerBase.Application.Apps.Commands.UpdateApp;
using PowerBase.Application.Apps.Commands.UpdateAppBranding;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.SetKey;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Relationships.Commands.UpdateReferenceFilter;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Applies the app-level and table-level settings of a PBL document onto a freshly created app, through the
/// same commands/repositories the UI uses so every normal validation still applies. Each piece is independent: one
/// that PowerBase rejects is reported (with source context) and the rest still go through.
/// </summary>
public class PblSettingsApplier
{
    private const string UPDATE_COMMIT_MESSAGE = "Applied by app import";
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly UpdateAppCommandHandler _updateAppHandler;
    private readonly UpdateAppBrandingCommandHandler _updateBrandingHandler;
    private readonly SetKeyCommandHandler _setKeyHandler;
    private readonly UpdateFieldCommandHandler _updateFieldHandler;
    private readonly FormulaTranslator _formulaTranslator;
    private readonly IRelationshipRepository _relationshipRepo;
    private readonly UpdateReferenceFilterCommandHandler _updateReferenceFilterHandler;

    public PblSettingsApplier(
        IAppRepository appRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        UpdateAppCommandHandler updateAppHandler, UpdateAppBrandingCommandHandler updateBrandingHandler,
        SetKeyCommandHandler setKeyHandler, UpdateFieldCommandHandler updateFieldHandler, FormulaTranslator formulaTranslator,
        IRelationshipRepository relationshipRepo, UpdateReferenceFilterCommandHandler updateReferenceFilterHandler)
    {
        _relationshipRepo = relationshipRepo;
        _updateReferenceFilterHandler = updateReferenceFilterHandler;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _updateAppHandler = updateAppHandler;
        _updateBrandingHandler = updateBrandingHandler;
        _setKeyHandler = setKeyHandler;
        _updateFieldHandler = updateFieldHandler;
        _formulaTranslator = formulaTranslator;
    }

    // ---- descriptions of lookup / summary / reference fields ----

    /// <summary>The source's comment on a lookup, summary or reference field becomes the field's description. These fields are made by the
    /// relationship handlers, which have no description input, so it is set afterwards.</summary>
    public virtual async Task ApplyRelationshipFieldDescriptionsAsync(PblDocument document, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var rels = document.Relationships ?? [];
        if (!rels.Any(r => r.ReferenceFieldDescription is not null || r.Lookups.Any(l => l.Description is not null) || r.Summaries.Any(s => s.Description is not null)))
            return;

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tables = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var tableNameByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);
        var fieldCache = new Dictionary<long, IReadOnlyList<AppField>>();

        async Task SetAsync(string tableRef, int? sourceId, string? label, string name, string description)
        {
            if (!tableNameByRef.TryGetValue(tableRef, out var tableName) || !tables.TryGetValue(tableName, out var table)) return;
            if (!fieldCache.TryGetValue(table.Id, out var fields))
                fieldCache[table.Id] = fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            var field = fields.FirstOrDefault(f => sourceId.HasValue && f.Fid == sourceId)
                ?? fields.FirstOrDefault(f => string.Equals(f.Label ?? f.Name, label ?? name, StringComparison.OrdinalIgnoreCase));
            if (field is null || !string.IsNullOrWhiteSpace(field.Description)) return;
            try
            {
                await _updateFieldHandler.HandleAsync(new UpdateFieldCommand(
                    table.PublicId, field.PublicId, field.Label ?? field.Name, description.Length > 500 ? description[..500] : description, field.IsRequired, field.DefaultValue,
                    field.IsSearchable, field.IsSortable, field.IsFilterable, field.IsReportable, field.IsAuditable,
                    field.IsUnique, field.IsEncrypted, field.IsAutoFill, field.Settings, UPDATE_COMMIT_MESSAGE), ct);
                fieldCache.Remove(table.Id);
            }
            catch (DomainException ex)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = tableRef, Name = field.Label ?? field.Name, Reason = $"Description not applied: {ex.Message}" });
            }
        }

        foreach (var rel in rels)
        {
            if (rel.ReferenceFieldDescription is { } refDescription)
                await SetAsync(rel.ChildTableRef, rel.ReferenceFieldSourceId, rel.ReferenceFieldLabel, rel.ReferenceFieldName, refDescription);
            foreach (var l in rel.Lookups.Where(l => l.Description is not null))
                await SetAsync(rel.ChildTableRef, l.SourceId, l.Label, l.Name, l.Description!);
            foreach (var s in rel.Summaries.Where(s => s.Description is not null))
                await SetAsync(rel.ParentTableRef, s.SourceId, s.Label, s.Name, s.Description!);
        }
    }

    // ---- dependent dropdowns ----

    /// <summary>Sets up the "choices depend on another field" filter of every reference field the source had one on. Needs both
    /// tables' fields to exist, so it runs after the fields, lookups and formulas are in.</summary>
    public virtual async Task ApplyReferenceFiltersAsync(PblDocument document, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var wanted = (document.Relationships ?? []).Where(r => r.ReferenceFilterFormFieldName is not null && r.ReferenceFilterParentFieldName is not null).ToList();
        if (wanted.Count == 0) return;

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tables = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var tableNameByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);

        foreach (var rel in wanted)
        {
            try
            {
                if (!tableNameByRef.TryGetValue(rel.ChildTableRef, out var childName) || !tables.TryGetValue(childName, out var child)
                    || !tableNameByRef.TryGetValue(rel.ParentTableRef, out var parentName) || !tables.TryGetValue(parentName, out var parent))
                    continue;

                var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);
                var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
                var childMap = FieldFids.ByNameOrLabel(childFields);
                var parentMap = FieldFids.ByNameOrLabel(parentFields);
                if (!childMap.TryGetValue(rel.ReferenceFilterFormFieldName!, out var formFid) || !parentMap.TryGetValue(rel.ReferenceFilterParentFieldName!, out var parentFid))
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = rel.LogicalRef, Name = rel.ReferenceFieldName, Reason = "Dependent dropdown not set up: one of its two fields was not created." });
                    continue;
                }

                var referenceField = childFields.FirstOrDefault(f => rel.ReferenceFieldSourceId.HasValue && f.Fid == rel.ReferenceFieldSourceId)
                    ?? childFields.FirstOrDefault(f => string.Equals(f.Name, rel.ReferenceFieldName, StringComparison.OrdinalIgnoreCase));
                var relationship = referenceField is null ? null : (await _relationshipRepo.ListByTableAsync(child.Id, ct)).FirstOrDefault(r => r.ReferenceFieldId == referenceField.Id);
                if (relationship is null)
                {
                    skipped.Add(new ImportSkippedItem { LogicalRef = rel.LogicalRef, Name = rel.ReferenceFieldName, Reason = "Dependent dropdown not set up: the relationship was not found." });
                    continue;
                }

                await _updateReferenceFilterHandler.HandleAsync(new UpdateReferenceFilterCommand(
                    relationship.PublicId, [new ReferenceFilterConditionInput((int)formFid, (int)parentFid, null, null, null)]), ct);
            }
            catch (DomainException ex)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = rel.LogicalRef, Name = rel.ReferenceFieldName, Reason = $"Dependent dropdown not set up: {(ex is ValidationException ve ? string.Join(" ", ve.Errors.SelectMany(e => e.Value)) : ex.Message)}" });
            }
        }
    }

    // ---- app ----

    public virtual async Task ApplyAppSettingsAsync(PblApp pbl, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        try
        {
            var existing = await _appRepo.GetByPublicIdAsync(appPublicId, ct);

            AppFormattingSettings? formatting = null;
            if (pbl.Formatting is { } f)
            {
                formatting = new AppFormattingSettings
                {
                    Currency = new CurrencyFormatSettings { Symbol = f.CurrencySymbol, Position = f.CurrencyPosition },
                    // App-level numbers always use two decimal places; Standard grouping is the fallback pattern.
                    Number = new NumberFormatSettings { DecimalPlaces = 2, ThousandSeparator = f.ThousandSeparator, DisplayPattern = "Standard" },
                    Date = new DateFormatSettings { FormatString = f.DateFormat, Separator = "-" },
                };
            }

            // Start from what the app already has and override only what the source actually said — never reset a
            // security switch the source did not mention.
            var security = string.IsNullOrEmpty(existing.SecurityOptions)
                ? new AppSecurityOptionsSettings()
                : JsonSerializer.Deserialize<AppSecurityOptionsSettings>(existing.SecurityOptions, CaseInsensitive) ?? new AppSecurityOptionsSettings();
            if (pbl.Security is { } s)
            {
                if (s.AllowNonAdminsToCopy.HasValue) security.AllowNonAdminsToCopy = s.AllowNonAdminsToCopy.Value;
                if (s.AllowNonAdminsToExport.HasValue) security.AllowNonAdminsToExport = s.AllowNonAdminsToExport.Value;
                if (s.AllowNonAdminsToConnect.HasValue) security.AllowNonAdminsToConnect = s.AllowNonAdminsToConnect.Value;
                if (s.RequireAppTokens.HasValue) security.RequireAppTokens = s.RequireAppTokens.Value;
                if (s.HideFromPublicSearch.HasValue) security.HideFromPublicSearch = s.HideFromPublicSearch.Value;
            }

            await _updateAppHandler.HandleAsync(new UpdateAppCommand(
                appPublicId, existing.Name, existing.Description, existing.Icon, existing.Color, formatting, security, existing.IsEncrypted), ct);
        }
        catch (Exception ex) when (ex is DomainException)
        {
            skipped.Add(new ImportSkippedItem { LogicalRef = pbl.LogicalRef, Name = pbl.Name, Reason = $"App formatting/security settings not applied: {Describe(ex)}" });
        }
    }

    // ---- tables ----

    /// <summary>Sets each table's key field. Must run right after the tables and their fields exist and BEFORE relationships,
    /// forms and reports: changing a key rewires the relationship fields that reference it, which is only safe while
    /// nothing has been built on top of them yet.</summary>
    public virtual async Task ApplyTableKeysAsync(PblDocument document, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tables = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var table in (document.Tables ?? []).Where(t => t.KeyFieldName is not null))
        {
            if (!tables.TryGetValue(table.Name, out var created))
                continue;

            await TryAsync(skipped, table, $"Key field '{table.KeyFieldName}'", async () =>
            {
                var byName = FieldIndex(await _fieldRepo.ListByTableAsync(created.Id, ct));
                if (!byName.TryGetValue(table.KeyFieldName!, out var keyField) || keyField.Fid is not { } keyFid)
                    throw new NotFoundException("Field", table.KeyFieldName!);
                await _setKeyHandler.HandleAsync(new SetKeyCommand(created.PublicId, keyFid), ct);
            });
        }
    }

    /// <summary>Show-in-bar, auto-save, record title, record picker and unique flags. Needs every field to exist.</summary>
    public virtual async Task ApplyTableSettingsAsync(PblDocument document, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tableByName = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var table in document.Tables ?? [])
        {
            if (!tableByName.TryGetValue(table.Name, out var created))
                continue;

            var fields = await _fieldRepo.ListByTableAsync(created.Id, ct);
            var byName = FieldIndex(fields);

            await TryAsync(skipped, table, "Hide from sidebar", async () =>
            {
                if (table.IsShowInBar == false)
                    await _tableRepo.SetShowInBarAsync(appId, [created.PublicId], false, ct);
            });

            await TryAsync(skipped, table, "Auto-save on leave", async () =>
            {
                if (table.AutoSaveOnLeave == true)
                    await _tableRepo.SetAutoSaveOnLeaveAsync(created.PublicId, true, ct);
            });

            if (table.RecordTitleFieldName is not null)
            {
                await TryAsync(skipped, table, $"Record title field '{table.RecordTitleFieldName}'", async () =>
                {
                    if (!byName.TryGetValue(table.RecordTitleFieldName, out var titleField))
                        throw new NotFoundException("Field", table.RecordTitleFieldName);
                    await _tableRepo.SetDisplayFieldAsync(created.Id, titleField.Id, ct);
                });
            }

            if (table.RecordPickerFieldNames.Count > 0)
            {
                await TryAsync(skipped, table, "Record picker fields", async () =>
                {
                    var ids = table.RecordPickerFieldNames.Select(n => byName.TryGetValue(n, out var f) ? f.Id : (long?)null).Where(i => i.HasValue).Select(i => i).ToList();
                    if (ids.Count == 0) throw new NotFoundException("Field", string.Join(", ", table.RecordPickerFieldNames));
                    await _tableRepo.SetDefaultRecordPickerFieldsAsync(
                        created.Id, ids.ElementAtOrDefault(0), ids.ElementAtOrDefault(1), ids.ElementAtOrDefault(2), ct);
                });
            }

            foreach (var field in (table.Fields ?? []).Where(f => f.IsUnique == true))
                await MakeUniqueAsync(table, field, created, byName, skipped, ct);
        }
    }

    /// <summary>The custom data rule needs the finished schema (lookups, summaries, formulas) to compile against. A rule that
    /// does not translate is still stored — as written, switched off — so the source text is not lost.</summary>
    public virtual async Task ApplyCustomDataRulesAsync(PblDocument document, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var tableByName = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var table in (document.Tables ?? []).Where(t => !string.IsNullOrWhiteSpace(t.CustomDataRule)))
        {
            if (!tableByName.TryGetValue(table.Name, out var created))
                continue;

            await TryAsync(skipped, table, "Custom data rule", async () =>
            {
                var fields = await _fieldRepo.ListByTableAsync(created.Id, ct);
                var translation = _formulaTranslator.Translate("Text", table.CustomDataRule, fields, await AppTableAliasSchema.BuildAsync(_tableRepo, created.AppId, ct));

                if (translation.Status == FormulaTranslationStatus.NeedsManualReview || translation.SettingsJson is null)
                {
                    await _tableRepo.UpdateCustomDataRuleAsync(created.PublicId, table.CustomDataRule, false, ct);
                    skipped.Add(new ImportSkippedItem { LogicalRef = table.LogicalRef, Name = table.Name,
                        Reason = $"Custom data rule needs manual review ({string.Join("; ", translation.Diagnostics)}); stored switched off." });
                    return;
                }

                using var doc = JsonDocument.Parse(translation.SettingsJson);
                var expression = doc.RootElement.GetProperty("expression").GetString();
                await _tableRepo.UpdateCustomDataRuleAsync(created.PublicId, expression, table.CustomDataRuleEnabled, ct);
            });
        }
    }

    private async Task MakeUniqueAsync(PblTable table, PblField field, AppTable created, Dictionary<string, AppField> byName, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        try
        {
            if (!byName.TryGetValue(field.Label ?? field.Name, out var existing) && !byName.TryGetValue(field.Name, out existing))
                return;
            if (existing.IsUnique)
                return;

            await _updateFieldHandler.HandleAsync(new UpdateFieldCommand(
                created.PublicId, existing.PublicId, existing.Label ?? existing.Name, existing.Description, existing.IsRequired, existing.DefaultValue,
                existing.IsSearchable, existing.IsSortable, existing.IsFilterable, existing.IsReportable, existing.IsAuditable,
                IsUnique: true, existing.IsEncrypted, existing.IsAutoFill, existing.Settings, UPDATE_COMMIT_MESSAGE), ct);
        }
        catch (DomainException ex)
        {
            skipped.Add(new ImportSkippedItem { LogicalRef = field.LogicalRef, Name = field.Name, Reason = $"Unique not applied: {Describe(ex)}" });
        }
    }

    // ---- helpers ----

    private static async Task TryAsync(List<ImportSkippedItem> skipped, PblTable table, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DomainException ex)
        {
            skipped.Add(new ImportSkippedItem { LogicalRef = table.LogicalRef, Name = table.Name, Reason = $"{what} not applied: {Describe(ex)}" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // These are optional polish settings: an unexpected failure must not undo an otherwise good import.
            skipped.Add(new ImportSkippedItem { LogicalRef = table.LogicalRef, Name = table.Name, Reason = $"{what} not applied: {ex.Message}" });
        }
    }

    /// <summary>Field lookup by Name, then Label (PowerBase's system fields carry generated names).</summary>
    private static Dictionary<string, AppField> FieldIndex(IReadOnlyList<AppField> fields)
    {
        var map = new Dictionary<string, AppField>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields) map.TryAdd(f.Name, f);
        foreach (var f in fields.Where(f => !string.IsNullOrWhiteSpace(f.Label))) map.TryAdd(f.Label!.Trim(), f);
        return map;
    }

    private static string Describe(Exception ex) =>
        ex is Domain.Exceptions.ValidationException ve ? string.Join(" ", ve.Errors.SelectMany(e => e.Value)) : ex.Message;
}
