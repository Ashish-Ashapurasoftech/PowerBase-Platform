using System.Collections.Generic;

namespace PowerBase.Application.Reports.Queries.GetRolesReportsMatrix;

public record RolesReportsMatrixResult
{
    public List<MatrixRole> Roles { get; set; } = new();
    public List<MatrixTable> Tables { get; set; } = new();
}

public record MatrixRole
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public record MatrixTable
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<MatrixReport> Reports { get; set; } = new();
}

public record MatrixReport
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public List<Guid> VisibleToRoleIds { get; set; } = new();
}
