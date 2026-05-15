namespace PowerBase.Application.Reports.Commands.CreateReport;

public record CreateReportCommand(
    Guid TablePublicId,
    string Name,
    string? Description,
    string Visibility,
    List<long> Columns,
    long? SortFieldId,
    bool SortDesc);
