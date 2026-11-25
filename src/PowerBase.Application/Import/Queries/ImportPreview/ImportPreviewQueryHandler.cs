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
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;
    private readonly IAppFieldRepository? _fieldRepo;

    public ImportPreviewQueryHandler(PblValidator validator, IAppRepository appRepo,
        PowerBase.Application.Import.Services.PipelineOnlyImportService? pipelineOnly = null,
        QuickbaseIdResolver? qbResolver = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        IAppFieldRepository? fieldRepo = null)
    {
        _configuration = configuration;
        _fieldRepo = fieldRepo;
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

        // Two PowerFlows of the file with the same name: the import names the later ones "<name> 2", "<name> 3" (PowerFlow names are
        // unique per user), as PblPipelinesImporter.UniqueNameAsync does.
        var renamed = new Dictionary<PblPipeline, string>(ReferenceEqualityComparer.Instance);
        var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pipeline in prepared.Apps.SelectMany(a => a.Pipelines ?? []))
        {
            var key = (pipeline.Name ?? "").Trim();
            nameCount[key] = nameCount.TryGetValue(key, out var seen) ? seen + 1 : 1;
            if (nameCount[key] > 1) renamed[pipeline] = $"{key} {nameCount[key]}";
        }

        var requiredMissing = await RequiredFieldProblemsAsync(prepared.Apps.SelectMany(a => a.Pipelines ?? []), ct);
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
                Pipelines = (app.Pipelines ?? []).Select(p => WithRequired(p, requiredMissing, WithRename(p, renamed, WithSiteCheck(p, ToPipelineItem(p))))).ToList(),
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
                    Message = $"Quickbase table id '{t}' is not stored on any imported table (meta.AppTable.QBTableID); the PowerFlows that use it are not imported.",
                }));
            }
            item.Issues = PipelinesOnlyIssues(item);
            return item;
        }).ToList();
        return new ImportPreviewResult { IsValid = true, AppCount = apps.Count, Apps = apps };
    }

    /// <summary>The importer's rule for a Create Record step (PblPipelinesImporter.MissingRequiredFieldsAsync): a required field of the
    /// table without a value makes the step a draft. Checked here so the wizard shows it before the import.</summary>
    private async Task<Dictionary<PblPipeline, List<PblIssueDto>>> RequiredFieldProblemsAsync(IEnumerable<PblPipeline> pipelines, CancellationToken ct)
    {
        var result = new Dictionary<PblPipeline, List<PblIssueDto>>(ReferenceEqualityComparer.Instance);
        if (_fieldRepo is null) return result;
        var fieldsByTable = new Dictionary<long, IReadOnlyList<PowerBase.Domain.Entities.AppField>>();
        static IEnumerable<PblPipelineStep> All(IEnumerable<PblPipelineStep> steps) => steps.SelectMany(s => new[] { s }.Concat(All(s.Children)));
        foreach (var pipeline in pipelines)
            foreach (var step in All(pipeline.Steps).Where(s => s.Subtype == "create-record"))
            {
                if (step.Config.GetValueOrDefault("tablePublicId") is not PblPipelineTableValue table
                    || !table.TableRef.StartsWith("$Table_PB_", StringComparison.Ordinal)
                    || !long.TryParse(table.TableRef["$Table_PB_".Length..], out var tableId))
                    continue;
                if (!fieldsByTable.TryGetValue(tableId, out var fields))
                    fieldsByTable[tableId] = fields = await _fieldRepo.ListByTableAsync(tableId, ct);
                var mapped = (step.Config.GetValueOrDefault("fieldMappings") as IEnumerable<object?> ?? [])
                    .OfType<Dictionary<string, object?>>()
                    .Where(m => m.GetValueOrDefault("value") is { } v && !string.IsNullOrWhiteSpace(v is string text ? text : "x"))
                    .Select(m => m.GetValueOrDefault("field") is PblPipelineFieldValue f ? f.SourceId : null)
                    .Where(fid => fid is not null).Select(fid => fid!.Value).ToHashSet();
                foreach (var f in fields.Where(f => f.IsRequired && !f.IsSystem && !f.IsDeleted && f.Fid.HasValue
                             && !PowerBase.Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode) && !mapped.Contains(f.Fid!.Value)))
                {
                    if (!result.TryGetValue(pipeline, out var list)) result[pipeline] = list = [];
                    list.Add(new PblIssueDto
                    {
                        Code = "PIPELINE_STEP_INCOMPLETE",
                        Message = $"Step '{step.Name}' ({step.SourceLabel}): Required field '{(string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label)}' has no value; set it before turning the PowerFlow on.",
                        ElementRef = step.SourceLabel,
                    });
                }
            }
        return result;
    }

    private static ImportPreviewPipelineItem WithRequired(PblPipeline pipeline, Dictionary<PblPipeline, List<PblIssueDto>> missing, ImportPreviewPipelineItem item)
    {
        if (missing.TryGetValue(pipeline, out var list)) item.Issues.AddRange(list);
        return item;
    }

    private static ImportPreviewPipelineItem WithRename(PblPipeline pipeline, Dictionary<PblPipeline, string> renamed, ImportPreviewPipelineItem item)
    {
        if (renamed.TryGetValue(pipeline, out var name))
            item.Issues.Add(new PblIssueDto { Code = "PIPELINE_RENAMED", Message = $"Another PowerFlow in this file is named '{pipeline.Name}'; this one is imported as '{name}'." });
        return item;
    }

    /// <summary>The PowerBase API / site address an imported link is written on is not configured: the import writes the link as a
    /// relative path and reports it (PblPipelinesImporter); the preview says so beforehand.</summary>
    private ImportPreviewPipelineItem WithSiteCheck(PblPipeline pipeline, ImportPreviewPipelineItem item)
    {
        string? Base(bool api) => api
            ? (_configuration?["PipelineExecution:ApiBaseUrl"] is { Length: > 0 } a ? a : _configuration?["Api:BaseUrl"])
            : _configuration?["Frontend:BaseUrl"];
        if (PipelinePreviewIssues.UsesSite(pipeline, api: true) && string.IsNullOrWhiteSpace(Base(true)))
            item.Issues.Add(new PblIssueDto { Code = "PIPELINE_STEP_INCOMPLETE", Message = "The PowerBase API address (PipelineExecution:ApiBaseUrl) is not configured; links to the PowerBase API are written as a relative path." });
        if (PipelinePreviewIssues.UsesSite(pipeline, api: false) && string.IsNullOrWhiteSpace(Base(false)))
            item.Issues.Add(new PblIssueDto { Code = "PIPELINE_STEP_INCOMPLETE", Message = "The PowerBase site address (Frontend:BaseUrl) is not configured; links to PowerBase pages are written as a relative path." });
        return item;
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
            // A note that needs no action (tags, an HTTP account made, a Quickbase link already rewritten) is information, so the
            // PowerFlows a user must complete stay the only warnings.
            Severity = PipelinePreviewIssues.IsInformation(i.Code) ? ImportIssueSeverity.Information : i.Severity,
            ApplicationName = i.ApplicationName,
            TableName = $"PowerFlow: {i.FieldName}",
            FieldName = StepOf(i.Description) ?? "PowerFlow",
            ElementType = i.ElementType,
            Description = i.Description,
            OriginalConfiguration = i.OriginalConfiguration,
            RecommendedAction = PipelinePreviewIssues.Action(i.Code),
            Code = i.Code,
            TechnicalDetail = i.TechnicalDetail,
        }).ToList();
        // Each imported PowerFlow's group starts with its status: imported as in Quickbase, or a partial import and why.
        var withStatus = new List<ImportIssue>();
        var byName = item.Pipelines.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var group in issues.GroupBy(i => i.TableName ?? "", StringComparer.Ordinal))
        {
            var list = group.ToList();
            if (list.All(i => i.ElementType == "Pipeline") && group.Key.StartsWith("PowerFlow: ", StringComparison.Ordinal)
                && byName.TryGetValue(group.Key["PowerFlow: ".Length..], out var pipeline)
                && PipelinePreviewIssues.Status(item.AppName, pipeline.Name, list, pipeline.StepCount, pipeline.SkippedStepCount) is { } status)
                withStatus.Add(status);
            withStatus.AddRange(list);
        }
        // A PowerFlow whose only change is skipped steps has no note of its own: its status alone.
        foreach (var pipeline in item.Pipelines.Where(p => p.Issues.Count == 0 && p.SkippedStepCount > 0))
            if (PipelinePreviewIssues.Status(item.AppName, pipeline.Name, [], pipeline.StepCount, pipeline.SkippedStepCount) is { } status)
                withStatus.Add(status);
        issues = withStatus;
        return new ImportIssueReport
        {
            Issues = issues,
            Summary = new ImportIssueSummary
            {
                ErrorCount = issues.Count(i => i.Severity == ImportIssueSeverity.Error),
                WarningCount = issues.Count(i => i.Severity == ImportIssueSeverity.Warning),
                InformationCount = issues.Count(i => i.Severity == ImportIssueSeverity.Information),
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
                => ($"Not imported: {d[PREFIX.Length..end]}", "Not imported"),
            var d when d.StartsWith("No existing table matches Quickbase table '", StringComparison.Ordinal)
                => ("Quickbase tables not found", "Table"),
            _ => (null, null),
        };
        if (table is null) return i;
        return new ImportIssue
        {
            Severity = i.Severity, ApplicationName = i.ApplicationName, TableName = table, FieldName = field,
            ElementType = i.ElementType, Description = i.Description, OriginalConfiguration = i.OriginalConfiguration,
            RecommendedAction = field == "Not imported" ? PipelinePreviewIssues.NotImportedAction(i.Description) : i.RecommendedAction,
            Code = i.Code, TechnicalDetail = i.TechnicalDetail,
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
