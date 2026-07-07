using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Resolves a field's default value when a record is created. A default that begins
/// with <c>=</c> is treated as a formula (spreadsheet-style) and evaluated against
/// the record's other values; anything else is returned as a literal.
/// </summary>
public interface IFormulaDefaultResolver
{
    object? Resolve(string defaultValue, AppField field, IReadOnlyList<AppField> allFields, IReadOnlyDictionary<long, object?> values, AppTable? table = null);
}

public sealed class FormulaDefaultResolver : IFormulaDefaultResolver
{
    private readonly FormulaEngine _engine;
    private readonly IQueryContext _queryContext;
    private readonly IFormulaRuntimeContext _runtime;
    private readonly IAppRepository _appRepo;

    public FormulaDefaultResolver(FormulaEngine engine, IQueryContext queryContext, IFormulaRuntimeContext runtime, IAppRepository appRepo)
    {
        _engine = engine;
        _queryContext = queryContext;
        _runtime = runtime;
        _appRepo = appRepo;
    }

    public object? Resolve(string defaultValue, AppField field, IReadOnlyList<AppField> allFields, IReadOnlyDictionary<long, object?> values, AppTable? table = null)
    {
        // Literal unless it starts with '=' — the formula marker.
        if (string.IsNullOrEmpty(defaultValue) || defaultValue[0] != '=')
            return defaultValue;

        var expression = defaultValue[1..];
        var schema = new AppFieldSchema(allFields);
        var expectedType = FormulaTypeMap.FieldType(field.TypeCode, field.Settings) ?? FormulaType.Text;

        var compiled = _engine.Compile(expression, schema, expectedType);
        if (compiled.HasErrors) return null;

        var options = new EvaluationOptions
        {
            UtcNow = DateTime.UtcNow,
            CurrentUser = _queryContext.UserId > 0
                ? new UserRef(_queryContext.UserId.ToString(CultureInfo.InvariantCulture), _queryContext.UserEmail)
                : null,
            AppId = table is null ? string.Empty : _appRepo.GetPublicIdByIdAsync(table.AppId).GetAwaiter().GetResult().ToString(),
            TableId = table?.PublicId.ToString() ?? string.Empty,
            UrlRoot = _runtime.UrlRoot,
            ReturnUrl = _runtime.ReturnUrl,
        };

        try { return FormulaRawValue.ToRaw(_engine.Evaluate(compiled, new ValuesRecordContext(values), options)); }
        catch (FormulaEvaluationException) { return null; }
    }
}
