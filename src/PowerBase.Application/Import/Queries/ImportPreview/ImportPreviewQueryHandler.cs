using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Reporting;
using YamlDotNet.Core;

namespace PowerBase.Application.Import.Queries.ImportPreview;

public class ImportPreviewQueryHandler
{
    private readonly PblValidator _validator;
    private readonly IAppRepository _appRepo;

    public ImportPreviewQueryHandler(PblValidator validator, IAppRepository appRepo)
    {
        _validator = validator;
        _appRepo = appRepo;
    }

    public async Task<ImportPreviewResult> HandleAsync(ImportPreviewQuery query, CancellationToken ct = default)
    {
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
