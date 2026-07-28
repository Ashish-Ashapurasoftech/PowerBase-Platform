namespace PowerBase.Application.Import.Pbl;

/// <summary>
/// PowerBase Blueprint Language (PBL) — the canonical JSON import format. A QBL (Quickbase)
/// document is converted into this shape before import; this shape can also be authored
/// directly (developer/API use).
///
/// Every element carries a <see cref="LogicalRef"/>. All intra-document references (a field's
/// table, a report's field list, etc.) point to logical refs — never to a DBID or any other
/// physical identifier. A DBID may travel along as a cosmetic <c>AliasHint</c> only.
/// </summary>
public sealed class PblDocument
{
    public string Version { get; set; } = "1.0";
    public PblApp App { get; set; } = new();
    public List<PblTable> Tables { get; set; } = [];
    public List<PblRelationship> Relationships { get; set; } = [];
    public List<PblForm> Forms { get; set; } = [];
    public List<PblRole> Roles { get; set; } = [];
}

public sealed class PblApp
{
    /// <summary>Logical ref, e.g. "$App_Project_Management".</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }

    /// <summary>Cosmetic only — a source DBID carried through for human traceability. Never used for matching.</summary>
    public string? AliasHint { get; set; }
}

public sealed class PblTable
{
    /// <summary>Logical ref, e.g. "$Table_Clients".</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? PluralLabel { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public List<PblField> Fields { get; set; } = [];

    /// <summary>Table/summary reports. Column and sort references are by field <see cref="PblField.Name"/>
    /// within this table — resolved to the created field's Fid at import time.</summary>
    public List<PblReport> Reports { get; set; } = [];

    public string? AliasHint { get; set; }
}

public sealed class PblField
{
    /// <summary>Logical ref, e.g. "$Field_Clients_Company_Name".</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }

    /// <summary>PowerBase <see cref="PowerBase.Domain.Enums.FieldTypeCode"/> value, already
    /// mapped by the QBL→PBL converter (or authored directly for API/developer use).</summary>
    public string TypeCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsRequired { get; set; }
    public string? DefaultValue { get; set; }

    /// <summary>Raw JSON settings blob for the target type (see FieldSettingsModels). Ignored
    /// for TypeCode "Formula" — use <see cref="ResultType"/>/<see cref="FormulaExpression"/> instead,
    /// since those require field-schema validation before a Settings blob can be built.</summary>
    public string? Settings { get; set; }

    /// <summary>Only meaningful when TypeCode is "Formula". One of PowerBase's
    /// FormulaResultTypes (Text, Number, Date, DateTime, Duration, Bool, User).</summary>
    public string? ResultType { get; set; }

    /// <summary>Only meaningful when TypeCode is "Formula". PowerBase-syntax formula text
    /// (already translated from QBL syntax, in a later phase). References other fields in
    /// this table by their PBL <see cref="Name"/> — the same name the field is created with.</summary>
    public string? FormulaExpression { get; set; }

    public string? AliasHint { get; set; }
}

public sealed class PblReport
{
    /// <summary>Logical ref, e.g. "$Report_Clients_ByBalance".</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>One of CreateReportCommandHandler's AllowedReportTypes: Table, Summary, GridEdit.</summary>
    public string ReportType { get; set; } = "Table";

    /// <summary>Field Names (matching a <see cref="PblField.Name"/> in the same table) to
    /// show as columns, in order.</summary>
    public List<string> Columns { get; set; } = [];
    public List<PblSortSpec> SortFields { get; set; } = [];
}

public sealed class PblSortSpec
{
    /// <summary>A field Name (matching a <see cref="PblField.Name"/> in the same table).</summary>
    public string FieldName { get; set; } = string.Empty;
    public bool Desc { get; set; }
}
