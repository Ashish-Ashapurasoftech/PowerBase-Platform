using PowerBase.Application.Common.Interfaces;
using PowerBase.Formula;

namespace PowerBase.Application.Formulas.Queries;

public sealed record ValidateFormulaQuery(
    Guid TablePublicId, string Expression, string? ExpectedType,
    IReadOnlyList<PowerBase.Application.Reports.SummaryVariable>? SummaryVariables = null);

public sealed class ValidateFormulaResult
{
    public bool Valid { get; init; }
    public string ResultType { get; init; } = string.Empty;
    public IReadOnlyList<FormulaDiagnosticDto> Diagnostics { get; init; } = [];
}

/// <summary>Compiles an expression against a table's field schema and returns diagnostics — drives the authoring UI.</summary>
public sealed class ValidateFormulaQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly FormulaEngine _engine;
    private readonly IAppVariableRepository? _variableRepo;

    public ValidateFormulaQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, FormulaEngine engine,
        IAppVariableRepository? variableRepo = null)
    {
        _variableRepo = variableRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _engine = engine;
    }

    public async Task<ValidateFormulaResult> HandleAsync(ValidateFormulaQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        // A report's summary formula reads that report's summary variables, not the table's fields.
        if (query.SummaryVariables is not null)
        {
            var scope = PowerBase.Application.Reports.SummaryFormulaFields.BuildVariableFields(query.SummaryVariables, fields, table.Id);
            var summaryCompiled = _engine.Compile(query.Expression ?? string.Empty, new AppFieldSchema(scope), FormulaTypeMap.ParseExpected(query.ExpectedType));
            return new ValidateFormulaResult
            {
                Valid = !summaryCompiled.HasErrors,
                ResultType = FormulaTypeMap.ResultTypeName(summaryCompiled),
                Diagnostics = summaryCompiled.Diagnostics.Select(FormulaDiagnosticDto.From).ToList(),
            };
        }

        var schema = new AppFieldSchema(fields);
        var aliasSchema = await AppTableAliasSchema.BuildAsync(_tableRepo, table.AppId, ct);

        var variableSchema = new AppVariableSchema(await FormulaVariables.LoadAsync(_variableRepo, table.AppId, ct));

        var compiled = _engine.Compile(query.Expression ?? string.Empty, schema, FormulaTypeMap.ParseExpected(query.ExpectedType), aliasSchema, variableSchema);

        return new ValidateFormulaResult
        {
            Valid = !compiled.HasErrors,
            ResultType = FormulaTypeMap.ResultTypeName(compiled),
            Diagnostics = compiled.Diagnostics.Select(FormulaDiagnosticDto.From).ToList(),
        };
    }
}
