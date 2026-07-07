using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas.Queries;

public sealed record EvaluateFormulaQuery(
    Guid TablePublicId,
    string Expression,
    string? ExpectedType,
    IReadOnlyDictionary<long, object?> Values);

public sealed class EvaluateFormulaResult
{
    public bool Valid { get; init; }
    public string ResultType { get; init; } = string.Empty;
    public object? Value { get; init; }
    public IReadOnlyList<FormulaDiagnosticDto> Diagnostics { get; init; } = [];
}

/// <summary>
/// Compiles and evaluates an expression against supplied field values — drives the
/// live preview in the formula editor and form-rule builder. Runtime errors fail
/// soft to a null value (the formula still "validates").
/// </summary>
public sealed class EvaluateFormulaQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly FormulaEngine _engine;
    private readonly IQueryContext _queryContext;
    private readonly IFormulaRuntimeContext _runtime;
    private readonly IRecordRepository _recordRepo;
    private readonly IAppRepository _appRepo;

    public EvaluateFormulaQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        FormulaEngine engine,
        IQueryContext queryContext,
        IFormulaRuntimeContext runtime,
        IRecordRepository recordRepo,
        IAppRepository appRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _engine = engine;
        _queryContext = queryContext;
        _runtime = runtime;
        _recordRepo = recordRepo;
        _appRepo = appRepo;
    }

    public async Task<EvaluateFormulaResult> HandleAsync(EvaluateFormulaQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var schema = new AppFieldSchema(fields);

        var compiled = _engine.Compile(query.Expression ?? string.Empty, schema, FormulaTypeMap.ParseExpected(query.ExpectedType));
        var diagnostics = compiled.Diagnostics.Select(FormulaDiagnosticDto.From).ToList();

        if (compiled.HasErrors)
            return new EvaluateFormulaResult { Valid = false, ResultType = compiled.ResultType.ToString(), Value = null, Diagnostics = diagnostics };

        var crossTable = new CrossTableQueryContext(_tableRepo, _fieldRepo, _recordRepo, table);
        var context = new CrossTableRecordContext(new ValuesRecordContext(query.Values), crossTable);
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

        object? value;
        try { value = FormulaRawValue.ToRaw(_engine.Evaluate(compiled, context, options)); }
        catch (FormulaEvaluationException) { value = null; }

        return new EvaluateFormulaResult { Valid = true, ResultType = compiled.ResultType.ToString(), Value = value, Diagnostics = diagnostics };
    }
}
