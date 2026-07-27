using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Records.Commands.InvokeButtonAction;

/// <summary>
/// Resolves a <see cref="ValueSource"/> slot (data / field / formula) against a single
/// record row. Every "value from data / field / formula" control in the Action Button
/// settings (label, color, filename, prompt default, add-data values, redirect, gates)
/// goes through this — the formula branch reuses the same compile/evaluate pipeline as
/// <see cref="Formulas.Queries.EvaluateFormulaQueryHandler"/> and <see cref="FormulaProjector"/>.
/// </summary>
public interface IActionButtonValueResolver
{
    /// <param name="row">The record's raw values, keyed by physical column name (f_{fid}),
    /// as returned by <see cref="IRecordRepository.GetByPublicIdAsync"/>.</param>
    Task<object?> ResolveAsync(
        ValueSource? source,
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<string, object?> row,
        FormulaType expectedType,
        CancellationToken ct = default);
}

public sealed class ActionButtonValueResolver : IActionButtonValueResolver
{
    private readonly FormulaEngine _engine;
    private readonly IQueryContext _queryContext;
    private readonly IFormulaRuntimeContext _runtime;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;

    public ActionButtonValueResolver(
        FormulaEngine engine,
        IQueryContext queryContext,
        IFormulaRuntimeContext runtime,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo)
    {
        _engine = engine;
        _queryContext = queryContext;
        _runtime = runtime;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
    }

    public async Task<object?> ResolveAsync(
        ValueSource? source,
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<string, object?> row,
        FormulaType expectedType,
        CancellationToken ct = default)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Kind))
            return null;

        switch (source.Kind)
        {
            case ValueSourceKinds.Data:
                return source.Data;

            case ValueSourceKinds.Field:
                if (source.FieldFid is not int fid) return null;
                return row.TryGetValue(PhysicalNaming.ColumnName(fid), out var v) ? v : null;

            case ValueSourceKinds.Formula:
                return await ResolveFormulaAsync(source.Formula, table, fields, row, expectedType, ct);

            default:
                return null;
        }
    }

    private async Task<object?> ResolveFormulaAsync(
        string? expression, AppTable table, IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<string, object?> row, FormulaType expectedType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var schema = new AppFieldSchema(fields);
        var compiled = _engine.Compile(expression, schema, expectedType);
        if (compiled.HasErrors)
            return null;

        var fidToColMap = fields.Where(f => f.Fid.HasValue)
            .ToDictionary(f => (long)f.Fid!.Value, f => f.PhysicalColumnName ?? string.Empty);
        var recordCtx = new RowRecordContext(row, fidToColMap);
        var crossTable = new CrossTableQueryContext(_tableRepo, _fieldRepo, _recordRepo, table);
        var evalCtx = new CrossTableRecordContext(recordCtx, crossTable);

        var appPublicId = await _appRepo.GetPublicIdByIdAsync(table.AppId, ct);
        var options = new EvaluationOptions
        {
            UtcNow = DateTime.UtcNow,
            CurrentUser = _queryContext.UserId > 0
                ? new UserRef(_queryContext.UserId.ToString(CultureInfo.InvariantCulture), _queryContext.UserEmail)
                : null,
            AppId = appPublicId.ToString(),
            TableId = table.PublicId.ToString(),
            UrlRoot = _runtime.UrlRoot,
            ReturnUrl = _runtime.ReturnUrl,
        };

        try { return FormulaRawValue.ToRaw(_engine.Evaluate(compiled, evalCtx, options)); }
        catch (FormulaEvaluationException) { return null; }
    }
}
