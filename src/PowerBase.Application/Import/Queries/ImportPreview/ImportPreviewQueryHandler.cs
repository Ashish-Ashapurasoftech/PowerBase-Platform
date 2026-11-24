using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Quickbase;
using PowerBase.Application.Import.Reporting;
using YamlDotNet.Core;

namespace PowerBase.Application.Import.Queries.ImportPreview;

public class ImportPreviewQueryHandler
{
    private readonly PblValidator _validator;
    private readonly IAppRepository _appRepo;

    private readonly PowerBase.Application.Import.Services.PipelineOnlyImportService? _pipelineOnly;
    private readonly QuickbaseIdResolver? _qbResolver;

    public ImportPreviewQueryHandler(PblValidator validator, IAppRepository appRepo,
        PowerBase.Application.Import.Services.PipelineOnlyImportService? pipelineOnly = null,
        QuickbaseIdResolver? qbResolver = null)
    {
        _pipelineOnly = pipelineOnly;
        _qbResolver = qbResolver;
        _validator = validator;
        _appRepo = appRepo;
    }

    public async Task<ImportPreviewResult> HandleAsync(ImportPreviewQuery query, CancellationToken ct = default)
    {
        if (_pipelineOnly is not null && query.Mode == ImportMode.CreateNewApp && QblPipelineOnlyBinder.IsPipelinesOnly(query.PblJson))
            return await PipelinesOnlyAsync(query.PblJson, ct);

        IReadOnlyList<ParsedApp> parsed;
        try
        {
            parsed = ImportDocumentParser.ParseAll(query.PblJson);
        }
        catch (JsonException ex)
        {
            return FileError("INVALID_JSON", $"Could not parse PBL document: {ex.Message}");
        }
        catch (YamlException ex)
        {
            return FileError("INVALID_YAML", $"Could not parse QBL document: {ex.Message}");
        }

        // Update works on exactly one application; nothing in the file is worth reviewing when it has several.
        if (query.Mode == ImportMode.UpdateExistingApp && parsed.Count > 1)
        {
            var multiAppErrors = new List<PblIssueDto> { new() { Code = ImportDocumentParser.MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE, Message = ImportDocumentParser.MultipleAppsMessage(parsed) } };
            return new ImportPreviewResult
            {
                IsValid = false,
                AppCount = parsed.Count,
                Errors = multiAppErrors,
                Issues = ImportIssueReportBuilder.BuildFromPreview("", multiAppErrors, [], document: null),
            };
        }

        var apps = new List<ImportPreviewAppItem>();
        foreach (var app in parsed)
            apps.Add(await BuildAppAsync(app, query.Mode, ct));

        // Two applications in one file sharing a name cannot both be created.
        foreach (var group in apps.Where(a => a.IsValid).GroupBy(a => a.AppName.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var dup in group.Skip(1))
                dup.Warnings.Add(new PblIssueDto { Code = "APP_NAME_DUPLICATED_IN_FILE", Message = $"Another application in this file is also named '{dup.AppName}'. Rename one of them to import both.", ElementRef = dup.AppRef });

        // Quickbase ids (app id from the catalog, table ids from Quickbase's API) — one app after another: the apps of one file
        // usually share a user token, and parallel calls on one token can hit Quickbase's rate limit.
        if (_qbResolver is not null)
            for (var i = 0; i < apps.Count; i++)
                await ResolveQuickbaseIdsAsync(apps[i], parsed[i], ct);

        // Built last, once every per-app warning (including the cross-app duplicate-name one above) is final.
        for (var i = 0; i < apps.Count; i++)
            apps[i].Issues = ImportIssueReportBuilder.BuildFromPreview(apps[i].AppName, apps[i].Errors, apps[i].Warnings, parsed[i].Document);

        return new ImportPreviewResult
        {
            IsValid = apps.Any(a => a.IsValid),
            AppCount = apps.Count,
            Apps = apps,
        };
    }

    private async Task<ImportPreviewAppItem> BuildAppAsync(ParsedApp parsed, ImportMode mode, CancellationToken ct)
    {
        var document = parsed.Document;
        var conversionIssues = parsed.Issues;

        var validation = _validator.Validate(document);
        var isValid = validation.IsValid && conversionIssues.All(i => i.Severity != PblIssueSeverity.Error);

        var tables = (document.Tables ?? []).Select(t => new ImportPreviewTableItem
        {
            LogicalRef = t.LogicalRef,
            Name = t.Name,
            MappingChoice = "Local",
            MasterMappingAvailable = false,
            Fields = (t.Fields ?? []).Select(f => new ImportPreviewFieldItem
            {
                LogicalRef = f.LogicalRef,
                Name = f.Name,
                TypeCode = f.TypeCode,
                IsSupported = PblValidator.IsCreatableFieldType(f.TypeCode),
                IsPendingValidation = string.Equals(f.TypeCode, PblValidator.FormulaTypeCode, StringComparison.OrdinalIgnoreCase),
            }).ToList(),
            Reports = (t.Reports ?? []).Select(r => r.Name).ToList(),
        }).ToList();

        var relationships = (document.Relationships ?? []).Select(r => new ImportPreviewRelationshipItem
        {
            LogicalRef = r.LogicalRef,
            ParentTableRef = r.ParentTableRef,
            ChildTableRef = r.ChildTableRef,
            ReferenceFieldName = r.ReferenceFieldName,
            LookupCount = r.Lookups.Count,
            SummaryCount = r.Summaries.Count,
        }).ToList();

        var forms = (document.Forms ?? []).Select(f => new ImportPreviewFormItem
        {
            LogicalRef = f.LogicalRef,
            Name = f.Name,
            TableRef = f.TableRef,
            SectionCount = f.Sections.Count,
            RuleCount = f.Rules.Count,
        }).ToList();

        var roles = (document.Roles ?? []).Select(r => new ImportPreviewRoleItem
        {
            LogicalRef = r.LogicalRef,
            Name = r.Name,
            TablePermissionCount = r.TablePermissions.Count,
        }).ToList();

        var appName = document.App?.Name ?? string.Empty;
        var nameExists = mode == ImportMode.CreateNewApp && isValid && !string.IsNullOrWhiteSpace(appName)
            && await _appRepo.NameExistsAsync(appName.Trim(), ct);

        var warnings = validation.Warnings.Concat(conversionIssues.Where(i => i.Severity == PblIssueSeverity.Warning)).Select(ToDto).ToList();
        if (nameExists)
            warnings.Add(new PblIssueDto { Code = "APP_NAME_ALREADY_EXISTS", Message = $"An app named '{appName}' already exists. Rename it to import.", ElementRef = parsed.AppRef });

        return new ImportPreviewAppItem
        {
            AppRef = parsed.AppRef,
            AppName = appName,
            IsValid = isValid,
            NameExists = nameExists,
            Tables = tables,
            Relationships = relationships,
            Forms = forms,
            Roles = roles,
            Variables = (document.Variables ?? []).Select(v => v.Name).ToList(),
            Pages = (document.Pages ?? []).Select(p => p.Name).ToList(),
            Errors = validation.Errors.Concat(conversionIssues.Where(i => i.Severity == PblIssueSeverity.Error)).Select(ToDto).ToList(),
            Warnings = warnings,
            // Pipelines are only imported with a new app; their notes are their own and never make the app invalid.
            Pipelines = mode == ImportMode.CreateNewApp ? (parsed.Pipelines ?? []).Select(ToPipelineItem).ToList() : [],
        };
    }

    private async Task ResolveQuickbaseIdsAsync(ImportPreviewAppItem item, ParsedApp parsed, CancellationToken ct)
    {
        var logicalId = string.IsNullOrWhiteSpace(parsed.Document.App?.LogicalRef) ? parsed.AppRef : parsed.Document.App!.LogicalRef;
        item.Quickbase = await _qbResolver!.ResolveAsync(logicalId, QuickbaseFileTables(parsed.Document), ct: ct);
    }

    /// <summary>The file's tables as the Quickbase id resolver needs them.</summary>
    public static List<QuickbaseFileTable> QuickbaseFileTables(PblDocument document) =>
        (document.Tables ?? []).Select(t => new QuickbaseFileTable(t.LogicalRef, t.Name, t.AliasHint)).ToList();

    private static ImportPreviewPipelineItem ToPipelineItem(PblPipeline pipeline) => new()
    {
        LogicalRef = pipeline.LogicalRef,
        Name = pipeline.Name,
        StepCount = pipeline.SourceStepCount,
        SkippedStepCount = pipeline.SkippedStepCount,
        ChangesExecution = pipeline.ChangesExecution,
        Issues = pipeline.Issues.Select(i => new PblIssueDto
        {
            Code = i.Code,
            Message = i.StepName is null ? i.Message : $"Step '{i.StepName}' ({i.StepId}): {i.Message}",
            ElementRef = i.StepId,
        }).ToList(),
    };

    /// <summary>A file of pipelines only: one entry per existing app its pipelines go into.</summary>
    private async Task<ImportPreviewResult> PipelinesOnlyAsync(string content, CancellationToken ct)
    {
        PowerBase.Application.Import.Services.PipelineOnlyImportService.Prepared prepared;
        try { prepared = await _pipelineOnly!.PrepareAsync(content, ct); }
        catch (YamlException ex) { return FileError("INVALID_YAML", $"Could not parse QBL document: {ex.Message}"); }
        if (prepared.Apps.Count == 0)
            return FileError("PIPELINE_ONLY_NO_APP", PowerBase.Application.Import.Services.PipelineOnlyImportService.NoAppMessage(prepared.Binding));

        var apps = prepared.Apps.Select((app, i) =>
        {
            var target = prepared.Binding.Apps.First(a => a.AppRef == app.AppRef).App;
            var item = new ImportPreviewAppItem
            {
                AppRef = app.AppRef,
                AppName = target.Name,
                IsValid = true,
                IsPipelinesOnly = true,
                TargetAppPublicId = target.PublicId,
                TableMatches = prepared.Binding.Matches.Where(m => m.AppPublicId == target.PublicId)
                    .Select(m => $"{m.QbTableId} → {m.TableName}").Distinct().ToList(),
                Pipelines = (app.Pipelines ?? []).Select(ToPipelineItem).ToList(),
            };
            if (i == 0)
            {
                item.Warnings.AddRange(prepared.NotImported.Select(p => new PblIssueDto
                {
                    Code = "PIPELINE_ONLY_APP_NOT_FOUND",
                    Message = $"PowerFlow '{p.Name}' is not imported: {p.Reason}",
                }));
                item.Warnings.AddRange(prepared.Binding.Notes.Select(n => new PblIssueDto { Code = "PIPELINE_ONLY_NOTE", Message = n }));
                item.Warnings.AddRange(prepared.Binding.UnmatchedTables.Select(t => new PblIssueDto
                {
                    Code = "PIPELINE_ONLY_TABLE_NOT_FOUND",
                    Message = $"No existing table matches Quickbase table '{t}'; its steps are skipped.",
                }));
            }
            item.Issues = PipelinesOnlyIssues(item);
            return item;
        }).ToList();
        return new ImportPreviewResult { IsValid = true, AppCount = apps.Count, Apps = apps };
    }

    /// <summary>The same grouped, collapsible report an application file's preview shows: the file's notes under "General" and
    /// each PowerFlow's step issues in a group of its own (one sub-entry per step).</summary>
    private static ImportIssueReport PipelinesOnlyIssues(ImportPreviewAppItem item)
    {
        var report = new ImportReport
        {
            Skipped = item.Warnings.Select(w => new ImportSkippedItem { LogicalRef = "", Name = "", Reason = w.Message, Code = w.Code }).ToList(),
            Pipelines = item.Pipelines.Where(p => p.Issues.Count > 0).Select(p => new PipelineImportReportItem
            {
                LogicalRef = p.LogicalRef,
                Name = p.Name,
                Issues = p.Issues.Select(i => new PipelineImportIssueItem { Code = i.Code, Message = i.Message, StepId = i.ElementRef }).ToList(),
            }).ToList(),
        };
        var built = ImportIssueReportBuilder.BuildFromImportReport(item.AppName, report, document: null);
        var issues = built.Issues.Select(i => i.ElementType != "Pipeline" ? Regrouped(i) : new ImportIssue
        {
            Severity = i.Severity,
            ApplicationName = i.ApplicationName,
            TableName = $"PowerFlow: {i.FieldName}",
            FieldName = StepOf(i.Description) ?? "PowerFlow",
            ElementType = i.ElementType,
            Description = i.Description,
            OriginalConfiguration = i.OriginalConfiguration,
            RecommendedAction = i.RecommendedAction,
            Code = i.Code,
            TechnicalDetail = i.TechnicalDetail,
        }).ToList();
        return new ImportIssueReport
        {
            Issues = issues,
            Summary = new ImportIssueSummary
            {
                ErrorCount = built.Summary.ErrorCount,
                WarningCount = built.Summary.WarningCount,
                InformationCount = built.Summary.InformationCount,
                AffectedFieldCount = issues.Where(i => i.Severity != ImportIssueSeverity.Information).Select(i => (i.TableName, i.FieldName)).Distinct().Count(),
            },
        };
    }

    /// <summary>A file-level entry in a group of its own instead of one long "General" list: a PowerFlow left out gets its
    /// PowerFlow's group (like the step issues of the imported ones), an unmatched Quickbase table the tables' group.</summary>
    private static ImportIssue Regrouped(ImportIssue i)
    {
        const string PREFIX = "PowerFlow '", MIDDLE = "' is not imported: ";
        // The report builder drops the code of these entries, so they are told apart by their (fixed) message.
        (string? table, string? field) = i.Description switch
        {
            var d when d.StartsWith(PREFIX, StringComparison.Ordinal) && d.IndexOf(MIDDLE, StringComparison.Ordinal) is var end and > 0
                => ($"PowerFlow: {d[PREFIX.Length..end]}", "Not imported"),
            var d when d.StartsWith("No existing table matches Quickbase table '", StringComparison.Ordinal)
                => ("Quickbase tables not found", "Table"),
            _ => (null, null),
        };
        if (table is null) return i;
        return new ImportIssue
        {
            Severity = i.Severity, ApplicationName = i.ApplicationName, TableName = table, FieldName = field,
            ElementType = i.ElementType, Description = i.Description, OriginalConfiguration = i.OriginalConfiguration,
            RecommendedAction = i.RecommendedAction, Code = i.Code, TechnicalDetail = i.TechnicalDetail,
        };
    }

    /// <summary>"Step 'Search records' (d): …" → "Step 'Search records' (d)".</summary>
    private static string? StepOf(string message)
    {
        if (!message.StartsWith("Step '", StringComparison.Ordinal)) return null;
        var end = message.IndexOf("): ", StringComparison.Ordinal);
        return end > 0 ? message[..(end + 1)] : null;
    }

    private static ImportPreviewResult FileError(string code, string message)
    {
        var errors = new List<PblIssueDto> { new() { Code = code, Message = message } };
        return new ImportPreviewResult
        {
            IsValid = false,
            Errors = errors,
            Issues = ImportIssueReportBuilder.BuildFromPreview("", errors, [], document: null),
        };
    }

    private static PblIssueDto ToDto(PblIssue issue) => new()
    {
        Code = issue.Code,
        Message = issue.Message,
        ElementRef = issue.ElementRef,
    };
}
