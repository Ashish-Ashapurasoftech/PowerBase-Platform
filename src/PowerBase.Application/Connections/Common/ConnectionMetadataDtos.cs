using System;

namespace PowerBase.Application.Connections.Common;

/// <summary>An app reachable through a saved account, as shown in a step's App picker.</summary>
public class ConnectionAppDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A table inside an app reachable through a saved account.</summary>
public class ConnectionTableDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A field of a table reachable through a saved account. Shape matches the editor's
/// field metadata so the builder's field-mapping code treats both sources identically.
/// </summary>
public class ConnectionFieldDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string TypeCode { get; set; } = string.Empty;

    /// <summary>The Quickbase-compatible FID (meta.AppField.Id).</summary>
    public int? Fid { get; set; }

    public string? Settings { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsRequired { get; set; }
    public bool IsSystem { get; set; }
}
