using System.Text.Json;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using YamlDotNet.Core;

namespace PowerBase.Application.Import.Queries.ImportPreview;

public class ImportPreviewQueryHandler
{
    private readonly PblValidator _validator;

    public ImportPreviewQueryHandler(PblValidator validator)
    {
        _validator = validator;
    }

    public Task<ImportPreviewResult> HandleAsync(ImportPreviewQuery query, CancellationToken ct = default)
    {
        PblDocument document;
        List<PblIssue> conversionIssues;
        try
        {
            (document, conversionIssues) = ImportDocumentParser.Parse(query.PblJson);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ImportPreviewResult
            {
                IsValid = false,
                Errors = [new PblIssueDto { Code = "INVALID_JSON", Message = $"Could not parse PBL document: {ex.Message}" }],
            });
        }
        catch (YamlException ex)
        {
            return Task.FromResult(new ImportPreviewResult
            {
                IsValid = false,
                Errors = [new PblIssueDto { Code = "INVALID_YAML", Message = $"Could not parse QBL document: {ex.Message}" }],
            });
        }

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

        return Task.FromResult(new ImportPreviewResult
        {
            IsValid = isValid,
            AppName = document.App?.Name ?? string.Empty,
            Tables = tables,
            Relationships = relationships,
            Forms = forms,
            Roles = roles,
            Errors = validation.Errors.Concat(conversionIssues.Where(i => i.Severity == PblIssueSeverity.Error)).Select(ToDto).ToList(),
            Warnings = validation.Warnings.Concat(conversionIssues.Where(i => i.Severity == PblIssueSeverity.Warning)).Select(ToDto).ToList(),
        });
    }

    private static PblIssueDto ToDto(PblIssue issue) => new()
    {
        Code = issue.Code,
        Message = issue.Message,
        ElementRef = issue.ElementRef,
    };
}
