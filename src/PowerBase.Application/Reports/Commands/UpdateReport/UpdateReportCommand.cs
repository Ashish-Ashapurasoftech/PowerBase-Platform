namespace PowerBase.Application.Reports.Commands.UpdateReport;

public record UpdateReportCommand(
    Guid PublicId,
    string Name,
    string? Description,
    string Visibility,
    IReadOnlyList<long> Columns,
    long? SortFieldId,
    bool SortDesc);
