using System.Text.Json;
using PowerBase.Application.Import.Pbl;

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
        try
        {
            document = PblSerializer.Deserialize(query.PblJson);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ImportPreviewResult
            {
                IsValid = false,
                Errors = [new PblIssueDto { Code = "INVALID_JSON", Message = $"Could not parse PBL document: {ex.Message}" }],
            });
        }

        var validation = _validator.Validate(document);

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
            }).ToList(),
            Reports = (t.Reports ?? []).Select(r => r.Name).ToList(),
        }).ToList();

        return Task.FromResult(new ImportPreviewResult
        {
            IsValid = validation.IsValid,
            AppName = document.App?.Name ?? string.Empty,
            Tables = tables,
            Errors = validation.Errors.Select(ToDto).ToList(),
            Warnings = validation.Warnings.Select(ToDto).ToList(),
        });
    }

    private static PblIssueDto ToDto(PblIssue issue) => new()
    {
        Code = issue.Code,
        Message = issue.Message,
        ElementRef = issue.ElementRef,
    };
}
