namespace PowerBase.API.Models.Fields;

public record UpdateFieldRequest(
    string Name,
    string? Label,
    string? Description,
    bool IsRequired,
    string? DefaultValue,
    bool IsSearchable,
    bool IsSortable,
    bool IsFilterable,
    bool IsReportable,
    bool IsAuditable,
    bool IsUnique,
    string? Settings);
