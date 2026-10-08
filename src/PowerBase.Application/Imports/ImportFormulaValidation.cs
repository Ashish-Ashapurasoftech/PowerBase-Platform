using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Formulas.Queries;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Imports;

/// <summary>A column of the file being imported, as the mapping screen knows it: its id and the name a formula refers to it by.</summary>
public sealed record ImportFormulaColumn(int Fid, string Name);

/// <param name="Expression">The formula being typed.</param>
/// <param name="ExpectedType">Optional result type to check for, as for any formula.</param>
/// <param name="SourceTableId">A table source: its fields are what the formula may use. Null for a file.</param>
/// <param name="FileColumns">A file source: its columns (all text) are what the formula may use.</param>
/// <param name="VirtualColumns">The import's virtual columns, which the formula may also use.</param>
/// <param name="OwnFid">When the formula defines a virtual column, that column's id: it cannot use itself, and its result must be one a virtual column can hold.</param>
public sealed record ImportFormulaCheck(
    string Expression, string? ExpectedType, Guid? SourceTableId, List<ImportFormulaColumn>? FileColumns,
    List<ImportVirtualColumn>? VirtualColumns, int? OwnFid);

/// <summary>Checks a formula typed on the import screen the way the import will compile it: against the source's fields (a table's, or a
/// file's columns) and the import's virtual columns. The table formula check only knows a table's own fields, so it cannot serve a file or
/// a virtual column. Reads nothing but field definitions, writes nothing, and uses the same access rules the import does: a member of the
/// app, who may read the source table, seeing only the fields their role shows them.</summary>
public sealed class ValidateImportFormulaHandler(
    IAppRepository apps, IAppTableRepository tables, IAppFieldRepository fields, IAppAccessService access, IRolePermissionEnforcer enforcer,
    FormulaEngine engine)
{
    public const int MaxFileColumns = 1000;

    public async Task<ValidateFormulaResult> HandleAsync(Guid appId, ImportFormulaCheck check, CancellationToken ct)
    {
        await access.RequireMembershipByAppPublicIdAsync(appId, ct);
        var source = await SourceFieldsAsync(appId, check, ct);

        // What the formula may also use: every other virtual column that works on its own. A broken one is simply not offered.
        var others = (check.VirtualColumns ?? []).Where(v => check.OwnFid is null || v.Fid != check.OwnFid).ToList();
        var (virtuals, _) = ImportVirtual.Resolve(others, source, engine, lenient: true);
        var schema = new AppFieldSchema(virtuals.Count == 0 ? source : source.Concat(virtuals.Select(v => v.Field)).ToList());

        var expression = check.Expression ?? string.Empty;
        if (expression.Length > ImportPlanBuilder.MaxFormulaLength)
            return Invalid($"The formula is too long (at most {ImportPlanBuilder.MaxFormulaLength} characters).", expression);
        if (ImportFormulaGuard.IsTooDeep(expression))
            return Invalid($"The formula is nested too deeply (at most {ImportFormulaGuard.MaxNesting} levels).", expression);

        // The import compiles formulas without the cross-table names, so this does too: what passes here passes when the import is saved.
        var compiled = engine.Compile(expression, schema, FormulaTypeMap.ParseExpected(check.ExpectedType));
        var diagnostics = compiled.Diagnostics.Select(FormulaDiagnosticDto.From).ToList();
        var valid = !compiled.HasErrors;
        if (valid && check.OwnFid is not null && ImportVirtual.FieldTypeOf(compiled.ResultType) is null)
        {
            valid = false;
            diagnostics.Add(Error($"A virtual column cannot hold {compiled.ResultType}. Make the formula return text, a number, a checkbox, a date or a date and time.", expression));
        }
        return new ValidateFormulaResult { Valid = valid, ResultType = compiled.ResultType.ToString(), Diagnostics = diagnostics };
    }

    private async Task<IReadOnlyList<AppField>> SourceFieldsAsync(Guid appId, ImportFormulaCheck check, CancellationToken ct)
    {
        if (check.SourceTableId is { } tableId)
        {
            await access.RequirePermissionByTablePublicIdAsync(tableId, PermissionCodes.RecordsRead, ct);
            var table = await tables.GetByPublicIdAsync(tableId, ct);
            // A table of another app is not found, not refused: its existence is not for this caller to learn.
            if (table.AppId != await apps.GetIdByPublicIdAsync(appId, ct)) throw new NotFoundException("Table", tableId);
            var all = await fields.ListByTableAsync(table.Id, ct);
            var tableAccess = await enforcer.GetTableAccessAsync(table, all, ct);
            return tableAccess.Unrestricted ? all : tableAccess.VisibleFields;
        }
        if (check.FileColumns is not { Count: > 0 } columns) return [];
        if (columns.Count > MaxFileColumns) throw new ValidationException(new Dictionary<string, string[]> { ["Formula"] = ["The file has too many columns."] });
        // A file's columns are all text; each is known by the name the mapping screen shows. Ids outside the file range are not columns.
        return ImportFileColumns.ToFields(columns
            .Where(c => c.Fid > ImportFileColumns.FidBase && c.Fid < ImportVirtual.FidBase && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new ImportFileColumn(c.Fid - ImportFileColumns.FidBase, c.Name.Trim(), c.Name.Trim(), c.Fid, false)).ToList());
    }

    private static ValidateFormulaResult Invalid(string message, string expression) =>
        new() { Valid = false, Diagnostics = [Error(message, expression)] };

    private static FormulaDiagnosticDto Error(string message, string expression) =>
        new() { Code = "ImportFormula", Message = message, Start = 0, Length = Math.Max(expression.Length, 1), Severity = "Error" };
}
