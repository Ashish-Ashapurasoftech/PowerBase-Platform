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

    /// <summary>App-level variables (readable from formulas as <c>[Name]</c>).</summary>
    public List<PblVariable> Variables { get; set; } = [];

    /// <summary>Code pages (custom HTML). Created only when the tenant has Code Pages enabled and
    /// the importing user holds the Code Page capability; otherwise reported as skipped.</summary>
    public List<PblPage> Pages { get; set; } = [];

    /// <summary>Which form a role uses to edit / add a record, per table (Quickbase "role defaults"). Only explicit choices are listed.</summary>
    public List<PblFormRoleDefault> FormRoleDefaults { get; set; } = [];

    /// <summary>Tables whose records Quickbase copies from a table in another application (a connected table).</summary>
    public List<PblConnectedTable> ConnectedTables { get; set; } = [];

    /// <summary>Dashboards (Quickbase "dashboards": tabs of report, chart, button, text and web-page widgets).</summary>
    public List<PblDashboard> Dashboards { get; set; } = [];
}

public sealed class PblDashboard
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<PblDashboardTab> Tabs { get; set; } = [];
}

public sealed class PblDashboardTab
{
    public string Name { get; set; } = string.Empty;
    public List<PblDashboardWidget> Widgets { get; set; } = [];
}

public sealed class PblDashboardWidget
{
    /// <summary>Report, Text, ButtonBar, WebPage or Spacer (PowerBase's dashboard widget types).</summary>
    public string WidgetType { get; set; } = "Report";
    public string Title { get; set; } = string.Empty;
    public bool ShowTitle { get; set; }

    /// <summary>Grid position on PowerBase's 12-column dashboard grid.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 4;
    public int H { get; set; } = 4;

    /// <summary>A <see cref="PblReport.LogicalRef"/> (a table report, summary report or chart) for Report widgets and report buttons.</summary>
    public string? ReportRef { get; set; }
    public string? Content { get; set; }
    public string? Url { get; set; }
    public string? ButtonLabel { get; set; }
    public string? ButtonColor { get; set; }
    public bool OpenInNewTab { get; set; }
}

/// <summary>A connected table: the table's records come from <see cref="SourceTableName"/> in the application <see cref="SourceAppName"/>.</summary>
public sealed class PblConnectedTable
{
    public string TableRef { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;

    /// <summary>The data source's name (a Quickbase application), taken from its key.</summary>
    public string SourceAppName { get; set; } = string.Empty;
    public string SourceTableName { get; set; } = string.Empty;

    /// <summary>"Destructive" (match the source exactly) or anything else (keep everything and add new).</summary>
    public string UpdateMode { get; set; } = "Destructive";

    /// <summary>Quickbase cron: minute hour day-of-month month day-of-week. Null = refreshed by hand.</summary>
    public string? CronTab { get; set; }
    public bool NotifyOnFailure { get; set; } = true;

    /// <summary>The source's own filter text (Quickbase syntax), when the connection copies only some records; shown in the report.</summary>
    public string? DataFilter { get; set; }

    /// <summary>The same filter as conditions on source columns: "all" or "any" of them must hold.</summary>
    public string FilterLogic { get; set; } = "all";
    public List<PblConnectedFilter> FilterConditions { get; set; } = [];

    public List<PblConnectedField> Fields { get; set; } = [];
}

public sealed class PblConnectedFilter
{
    public string SourceColumnName { get; set; } = string.Empty;
    public int? SourceFid { get; set; }

    /// <summary>PowerBase filter operator: eq, ne, contains, notContains …</summary>
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
}

public sealed class PblConnectedField
{
    /// <summary>The field on this table.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>The source column's name and its Quickbase field id (the same number as the source field's Fid in PowerBase).</summary>
    public string SourceColumnName { get; set; } = string.Empty;
    public int? SourceFid { get; set; }
}

/// <summary>One role's (or everyone's) form choice on a table. A null ref means "the table's default form".</summary>
public sealed class PblFormRoleDefault
{
    public string TableRef { get; set; } = string.Empty;

    /// <summary>A <see cref="PblRole.LogicalRef"/>; null = Everyone.</summary>
    public string? RoleRef { get; set; }

    /// <summary>A <see cref="PblForm.LogicalRef"/>.</summary>
    public string? EditFormRef { get; set; }
    public string? AddFormRef { get; set; }
}

public sealed class PblVariable
{
    /// <summary>Logical ref, e.g. "$Variable_Tax_Rate".</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class PblPage
{
    /// <summary>Logical ref, e.g. "$Page_Dashboard".</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw page code. Stored as a single "html" Code page (PowerBase's one-page-one-file model).</summary>
    public string Code { get; set; } = string.Empty;
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

    /// <summary>App-level number/currency/date formatting; the fallback when a field sets none of its own.</summary>
    public PblAppFormatting? Formatting { get; set; }

    /// <summary>Copy/export/connect/token/search switches.</summary>
    public PblAppSecurity? Security { get; set; }

    /// <summary>Brand colour (hex), from the source app header background.</summary>
    public string? BrandColorHex { get; set; }
}

public sealed class PblAppFormatting
{
    /// <summary>One of $ ₹ € £ ¥ — anything else has been defaulted to "$" by the converter.</summary>
    public string CurrencySymbol { get; set; } = "$";

    /// <summary>"Before", "Between" or "After".</summary>
    public string CurrencyPosition { get; set; } = "Before";

    /// <summary>Digit-grouping character: "," or ".".</summary>
    public string ThousandSeparator { get; set; } = ",";

    /// <summary>Decimal character: "." or ",".</summary>
    public string DecimalSeparator { get; set; } = ".";

    /// <summary>Date pattern with "-" separators, e.g. "MM-DD-YYYY".</summary>
    public string DateFormat { get; set; } = "MM-DD-YYYY";
}

public sealed class PblAppSecurity
{
    public bool? AllowNonAdminsToCopy { get; set; }
    public bool? AllowNonAdminsToExport { get; set; }
    public bool? AllowNonAdminsToConnect { get; set; }
    public bool? RequireAppTokens { get; set; }
    public bool? HideFromPublicSearch { get; set; }
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

    /// <summary>Report links of the source table that do not belong to a relationship (they match on a field other than the child's
    /// reference field, or are a second link to the same child). Created after the relationships, on the source field number.</summary>
    public List<PblStandaloneReportLink> ReportLinks { get; set; } = [];

    /// <summary>Table/summary reports. Column and sort references are by field <see cref="PblField.Name"/>
    /// within this table — resolved to the created field's Fid at import time.</summary>
    public List<PblReport> Reports { get; set; } = [];

    public string? AliasHint { get; set; }

    /// <summary>Position in the source app's table order (0-based).</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Null = leave PowerBase's default (shown). False hides the table from the sidebar only.</summary>
    public bool? IsShowInBar { get; set; }

    /// <summary>A <see cref="PblField.Name"/> on this table: the unique key used by relationships.</summary>
    public string? KeyFieldName { get; set; }

    /// <summary>A <see cref="PblField.Name"/> on this table that titles a record (only when the source enables a custom title).</summary>
    public string? RecordTitleFieldName { get; set; }

    /// <summary>Up to three <see cref="PblField.Name"/>s, in order, for the record picker.</summary>
    public List<string> RecordPickerFieldNames { get; set; } = [];

    /// <summary>Custom data rule formula in PowerBase syntax (already translated) and whether it is switched on.</summary>
    public string? CustomDataRule { get; set; }
    public bool CustomDataRuleEnabled { get; set; }

    /// <summary>"Save on exit" → auto-save when leaving the page.</summary>
    public bool? AutoSaveOnLeave { get; set; }

    /// <summary>Table default-report settings (sort, header text, grid edit, group display, row numbers, quick search, dynamic filters).</summary>
    public PblDefaultReportSettings? DefaultReportSettings { get; set; }
}

/// <summary>The table's "Default report" page: default columns, default dynamic filters and default sort. Applied to the table's hidden
/// Default Report Settings record once every field exists; a null/empty part leaves PowerBase's own default.</summary>
public sealed class PblDefaultReportSettings
{
    /// <summary>The fields shown as default columns, in order (a <see cref="PblField.Name"/>, or the name or label of a reference, lookup,
    /// summary or report link). Null = leave the default (every reportable field).</summary>
    public List<string>? ColumnFieldNames { get; set; }

    /// <summary>For <see cref="DynamicFilterType"/> "Custom": the fields users may filter by on the fly, in order.</summary>
    public List<string> DynamicFilterFieldNames { get; set; } = [];

    /// <summary>A <see cref="PblField.Name"/> on the table; null = default order.</summary>
    public string? SortFieldName { get; set; }
    public bool SortDescending { get; set; }

    /// <summary>"Default", "Wrap" or "Truncate".</summary>
    public string? ColumnHeaderText { get; set; }
    public bool? InlineEditing { get; set; }
    public bool? GroupDefaultCollapsed { get; set; }
    public bool? ShowRowNumbers { get; set; }
    public bool? AllowQuickSearch { get; set; }
    public string? DynamicFilterType { get; set; }
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

    /// <summary>The source field's numeric <c>Id:</c>. Formulas, record filters and picker/key settings refer to fields by it.</summary>
    public int? SourceId { get; set; }

    /// <summary>For formula fields whose source type decides the PowerBase variant even though they evaluate to text:
    /// "Url", "Email", "Phone" or "RichText". Null = pick the variant from the compiled result type.</summary>
    public string? FormulaVariant { get; set; }

    public string? HelpText { get; set; }
    public bool? IsUnique { get; set; }
    public bool? IsAuditable { get; set; }
    public bool? IsSearchable { get; set; }
    public bool? IsReportable { get; set; }
}

public sealed class PblReport
{
    /// <summary>Logical ref, e.g. "$Report_Clients_ByBalance".</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>One of ReportConfigValidatorRegistry's supported report types: Table, Summary, Chart.</summary>
    public string ReportType { get; set; } = "Table";

    /// <summary>Field Names (matching a <see cref="PblField.Name"/> in the same table) to
    /// show as columns, in order.</summary>
    public List<string> Columns { get; set; } = [];
    public List<PblSortSpec> SortFields { get; set; } = [];

    public string? Description { get; set; }
    public bool ShowDescription { get; set; }

    /// <summary>Row filter — the translated source criteria. Null = unfiltered.</summary>
    public PblFilterGroup? Filter { get; set; }

    public bool? HideTotals { get; set; }

    /// <summary>Summary reports: the "Rows" group levels, outermost first. Empty = group by Record ID# (so the report can still be created and repaired).</summary>
    public List<PblSummaryGroup> SummaryRows { get; set; } = [];

    /// <summary>Summary reports: the crosstab column dimension, when the source report has one.</summary>
    public PblSummaryGroup? SummaryCrosstab { get; set; }

    /// <summary>Chart reports: the chart settings.</summary>
    public PblChart? Chart { get; set; }

    /// <summary>Summary reports: the summarize-data columns.</summary>
    public List<PblSummaryAggregation> Aggregations { get; set; } = [];

    public bool? GroupDefaultCollapsed { get; set; }
    public bool? AllowQuickSearch { get; set; }
    public string? DynamicFilterType { get; set; }

    /// <summary>For DynamicFilterType "Custom": the fields users may filter by on the fly (a <see cref="PblField.Name"/> each).</summary>
    public List<string> DynamicFilterFieldNames { get; set; } = [];

    /// <summary>"Default", "Wrap" or "Truncate".</summary>
    public string? ColumnHeaderText { get; set; }
    public bool? ShowEditIcon { get; set; }
    public bool? ShowViewIcon { get; set; }
    public bool? DisableBulkDelete { get; set; }
    public bool? DisableGridEdit { get; set; }

    /// <summary>Roles allowed to see the report. Null = no restriction information in the source (visible to all with table access).</summary>
    public List<string>? VisibleToRoleRefs { get; set; }
}

/// <summary>An AND/OR tree of field conditions — the translation of a Quickbase query.</summary>
public sealed class PblFilterGroup
{
    /// <summary>"and" or "or".</summary>
    public string Logic { get; set; } = "and";
    public List<PblFilterCondition> Conditions { get; set; } = [];
    public List<PblFilterGroup> Groups { get; set; } = [];

    public int Count => Conditions.Count + Groups.Sum(g => g.Count);
}

public sealed class PblFilterCondition
{
    /// <summary>A <see cref="PblField.Name"/> on the table the filter applies to.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>One of PowerBase's filter operators (eq, ne, contains, notContains, startsWith, notStartsWith, gt, gte, lt, lte, includes, notIncludes).</summary>
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }

    /// <summary>The source compared to the signed-in user (<c>_curuser_</c>).</summary>
    public bool UseCurrentUser { get; set; }

    /// <summary>"ask" (the viewer is prompted when the report runs) or "field" (compare to <see cref="ValueFieldName"/> on the same record); null = a literal value.</summary>
    public string? ValueMode { get; set; }
    public string? ValueFieldName { get; set; }
}

/// <summary>One "Rows" group of a Summary report (or its crosstab column): a field and how its values are combined.</summary>
public sealed class PblSummaryGroup
{
    /// <summary>A <see cref="PblField.Name"/> on the report's table.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>"EqualValues", "Month", "Year", … (same vocabulary as <see cref="PblSortSpec.GroupMode"/>).</summary>
    public string GroupMode { get; set; } = "EqualValues";
}

/// <summary>One "summarize data" column of a Summary report: a function over a field (the record count is always shown).</summary>
public sealed class PblSummaryAggregation
{
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Sum, Avg, Min or Max.</summary>
    public string Function { get; set; } = "Sum";

    /// <summary>"Normal" or "PercentOfColumnTotal".</summary>
    public string DisplayAs { get; set; } = "Normal";
}

/// <summary>A chart report's settings (Chart reports: Pie, StackedBar, Gauge …). The measured values are the report's <see cref="PblReport.Aggregations"/>
/// (none = the record count).</summary>
public sealed class PblChart
{
    /// <summary>Bar, StackedBar, Pie, Gauge … (PowerBase's chart types).</summary>
    public string ChartType { get; set; } = "Bar";

    /// <summary>The category axis (a pie's slices): what the chart is grouped by.</summary>
    public PblSummaryGroup? Category { get; set; }

    /// <summary>The optional second grouping (stacked series).</summary>
    public PblSummaryGroup? Series { get; set; }

    /// <summary>"Labels" or "Values".</summary>
    public string SortBy { get; set; } = "Labels";

    /// <summary>"Asc" or "Desc".</summary>
    public string SortDirection { get; set; } = "Asc";

    public decimal? GoalValue { get; set; }
    public string? GoalLabel { get; set; }
    public bool DataLabelsVisible { get; set; }

    /// <summary>Gauge: the measured field and how it is summarized (Sum, Avg, Max, Min).</summary>
    public string? GaugeFieldName { get; set; }
    public string? GaugeFunction { get; set; }
}

public sealed class PblSortSpec
{
    /// <summary>A field Name (matching a <see cref="PblField.Name"/> in the same table).</summary>
    public string FieldName { get; set; } = string.Empty;
    public bool Desc { get; set; }

    /// <summary>True when this level also groups the report (not just sorts it).</summary>
    public bool IsGroup { get; set; }

    /// <summary>How values are combined when grouping ("EqualValues", "Month", …); null = equal values.</summary>
    public string? GroupMode { get; set; }
}
