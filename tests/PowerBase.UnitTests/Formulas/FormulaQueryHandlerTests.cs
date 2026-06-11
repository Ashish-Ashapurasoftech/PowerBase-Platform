using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas.Queries;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Formulas;

public class FormulaQueryHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly FormulaEngine _engine = new();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private static AppField Field(int fid, string name, string typeCode, string? settings = null) =>
        new() { Id = fid, Fid = fid, Name = name, TypeCode = typeCode, Settings = settings };

    private Guid SetupTable(params AppField[] fields)
    {
        var tableId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new AppTable { Id = 5, PublicId = tableId, Name = "T" });
        _fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(fields.ToList());
        return tableId;
    }

    [Fact]
    public async Task Validate_reports_unknown_field()
    {
        var tableId = SetupTable(Field(1, "Qty", "Number"));
        var sut = new ValidateFormulaQueryHandler(_tableRepo, _fieldRepo, _engine);

        var result = await sut.HandleAsync(new ValidateFormulaQuery(tableId, "[Nope] + 1", "Number"));

        result.Valid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "UnknownField");
    }

    [Fact]
    public async Task Validate_accepts_well_typed_formula()
    {
        var tableId = SetupTable(Field(1, "Qty", "Number"));
        var sut = new ValidateFormulaQueryHandler(_tableRepo, _fieldRepo, _engine);

        var result = await sut.HandleAsync(new ValidateFormulaQuery(tableId, "[Qty] * 2", "Number"));

        result.Valid.Should().BeTrue();
        result.ResultType.Should().Be("Number");
    }

    [Fact]
    public async Task Validate_flags_result_type_mismatch()
    {
        var tableId = SetupTable(Field(1, "Qty", "Number"));
        var sut = new ValidateFormulaQueryHandler(_tableRepo, _fieldRepo, _engine);

        var result = await sut.HandleAsync(new ValidateFormulaQuery(tableId, "[Qty]", "Text"));

        result.Valid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "ResultTypeMismatch");
    }

    [Fact]
    public async Task Evaluate_computes_value_from_supplied_values()
    {
        var tableId = SetupTable(Field(1, "Qty", "Number"));
        var sut = new EvaluateFormulaQueryHandler(_tableRepo, _fieldRepo, _engine, _queryContext);
        var values = new Dictionary<long, object?> { [1] = 7m };

        var result = await sut.HandleAsync(new EvaluateFormulaQuery(tableId, "[Qty] * 3", "Number", values));

        result.Valid.Should().BeTrue();
        result.Value.Should().Be(21m);
    }
}
