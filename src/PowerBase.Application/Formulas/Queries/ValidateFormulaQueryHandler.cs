using PowerBase.Application.Common.Interfaces;
using PowerBase.Formula;

namespace PowerBase.Application.Formulas.Queries;

public sealed record ValidateFormulaQuery(Guid TablePublicId, string Expression, string? ExpectedType);

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

    public ValidateFormulaQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, FormulaEngine engine)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _engine = engine;
    }

    public async Task<ValidateFormulaResult> HandleAsync(ValidateFormulaQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var schema = new AppFieldSchema(fields);
        var aliasSchema = await AppTableAliasSchema.BuildAsync(_tableRepo, table.AppId, ct);

        var compiled = _engine.Compile(query.Expression ?? string.Empty, schema, FormulaTypeMap.ParseExpected(query.ExpectedType), aliasSchema);

        return new ValidateFormulaResult
        {
            Valid = !compiled.HasErrors,
            ResultType = compiled.ResultType.ToString(),
            Diagnostics = compiled.Diagnostics.Select(FormulaDiagnosticDto.From).ToList(),
        };
    }
}
