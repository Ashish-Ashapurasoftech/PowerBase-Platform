using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Reporting;

/// <summary>Where one <see cref="PblDocument"/> element (by its LogicalRef) sits in the app: which
/// table and field it belongs to, and what kind of element it is. Used to turn a bare element
/// reference on an issue into the Table/Field names the structured report shows.</summary>
public sealed record ImportRefInfo(string? TableName, string? FieldName, string ElementType);

/// <summary>Builds a LogicalRef → (table, field, element type) lookup from a <see cref="PblDocument"/>,
/// so an issue carrying only an element reference (as every <c>PblIssue</c>/<c>ImportSkippedItem</c>
/// does today) can be placed under the right App → Table → Field in the structured report without
/// having to change the ~90 call sites that raise those issues.</summary>
public sealed class ImportRefResolver
{
    private readonly Dictionary<string, ImportRefInfo> _byRef = new(StringComparer.Ordinal);

    /// <summary>Fallback index by the bare (unqualified) local ref, e.g. "$Field_Name" for a field whose
    /// real LogicalRef is "$Table_X::$Field_Name". <see cref="QblToPblConverter"/> raises several issues
    /// (notably every "QBL_EMPTY_FORMULA" one) against the bare ref it had on hand before the field's
    /// final table-qualified LogicalRef existed, so an exact-match lookup on <see cref="_byRef"/> alone
    /// missed almost every field-level issue. A null value means the bare ref is ambiguous — reused by
    /// more than one element — in which case resolving it would risk pointing at the wrong table, so it
    /// is deliberately left unresolved (falls back to "General") rather than guessed.</summary>
    private readonly Dictionary<string, ImportRefInfo?> _byLocalRef = new(StringComparer.Ordinal);

    public ImportRefResolver(PblDocument? document)
    {
        if (document is null) return;

        var tableNameByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);

        foreach (var table in document.Tables ?? [])
        {
            Add(table.LogicalRef, table.Name, null, "Table");

            foreach (var field in table.Fields ?? [])
                Add(field.LogicalRef, table.Name, field.Label ?? field.Name, "Field");

            foreach (var report in table.Reports ?? [])
                Add(report.LogicalRef, table.Name, report.Name, "Report");

            foreach (var link in table.ReportLinks ?? [])
                Add(link.LogicalRef, table.Name, link.Label ?? link.Name, "Report Link");
        }

        foreach (var rel in document.Relationships ?? [])
        {
            tableNameByRef.TryGetValue(rel.ParentTableRef, out var parentName);
            tableNameByRef.TryGetValue(rel.ChildTableRef, out var childName);

            Add(rel.LogicalRef, childName, rel.ReferenceFieldLabel ?? rel.ReferenceFieldName, "Relationship");

            foreach (var lookup in rel.Lookups)
                Add(lookup.LogicalRef, childName, lookup.Label ?? lookup.Name, "Lookup Field");

            foreach (var summary in rel.Summaries)
                Add(summary.LogicalRef, parentName, summary.Label ?? summary.Name, "Summary Field");
        }

        foreach (var form in document.Forms ?? [])
        {
            tableNameByRef.TryGetValue(form.TableRef, out var tableName);
            Add(form.LogicalRef, tableName, form.Name, "Form");

            foreach (var rule in form.Rules ?? [])
                Add(rule.LogicalRef, tableName, rule.Name, "Form Rule");
        }

        foreach (var role in document.Roles ?? [])
            Add(role.LogicalRef, null, role.Name, "Role");

        foreach (var variable in document.Variables ?? [])
            Add(variable.LogicalRef, null, variable.Name, "Variable");

        foreach (var page in document.Pages ?? [])
            Add(page.LogicalRef, null, page.Name, "Page");

        foreach (var dashboard in document.Dashboards ?? [])
            Add(dashboard.LogicalRef, null, dashboard.Name, "Dashboard");

        foreach (var connected in document.ConnectedTables ?? [])
            Add(connected.TableRef, connected.TableName, null, "Connected Table");
    }

    private void Add(string? logicalRef, string? tableName, string? fieldName, string elementType)
    {
        if (string.IsNullOrEmpty(logicalRef)) return;
        var info = new ImportRefInfo(tableName, fieldName, elementType);
        _byRef[logicalRef] = info;

        var local = LocalPart(logicalRef);
        if (local == logicalRef) return;
        _byLocalRef[local] = _byLocalRef.ContainsKey(local) ? null : info;
    }

    /// <summary>The part of a "::"-qualified LogicalRef after its last separator — <see cref="Add"/>'s
    /// qualifying convention matches <c>QblToPblConverter.Qualify</c>'s <c>$"{parentRef}::{localRef}"</c>.</summary>
    private static string LocalPart(string logicalRef)
    {
        var idx = logicalRef.LastIndexOf("::", StringComparison.Ordinal);
        return idx < 0 ? logicalRef : logicalRef[(idx + 2)..];
    }

    public ImportRefInfo? Resolve(string? elementRef)
    {
        if (string.IsNullOrEmpty(elementRef)) return null;
        if (_byRef.TryGetValue(elementRef, out var info)) return info;
        return _byLocalRef.GetValueOrDefault(elementRef);
    }
}
