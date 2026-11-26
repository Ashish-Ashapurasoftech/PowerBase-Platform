using System.Globalization;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using Xunit;

namespace PowerBase.UnitTests.Formulas;

/// <summary>
/// Number formulas SQL computes exactly: which translate, which never do, and — against a real SQL Server when
/// POWERBASE_TEST_SQL_CONNECTION is set — that SQL gives the formula engine's value for every row (blanks and zeros included).
/// </summary>
public class SqlFormulaFieldsTests
{
    private const int Amount = 9, Qty = 10, Price = 11, Name = 12;

    private static AppField Plain(int fid, string type, string name) =>
        new() { Id = 1000 + fid, Fid = fid, Name = name, TypeCode = type };

    private static AppField NumberFormula(int fid, string expression) =>
        new() { Id = 1000 + fid, Fid = fid, Name = $"F{fid}", TypeCode = "Formula_Number",
            Settings = System.Text.Json.JsonSerializer.Serialize(new { expression }) };

    private static List<AppField> Table(params AppField[] formulas) =>
    [
        new() { Id = 1003, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
        Plain(Amount, "Currency", "Amount"), Plain(Qty, "Number", "Qty"), Plain(Price, "Number", "Price"), Plain(Name, "Text", "Name"),
        .. formulas,
    ];

    [Theory]
    [InlineData("[Amount] * 1.18", "((f_9) * (1.18))")]
    [InlineData("[Qty] * [Price] - 2", "(((f_10) * (f_11)) - (2))")]
    [InlineData("-[Amount] / [Qty]", "((-(f_9)) / NULLIF((f_10), 0))")]
    [InlineData("[Record ID#] * 2", "((Id) * (2))")]
    public void Translates_exact_number_arithmetic(string expression, string sql)
    {
        var standIns = SqlFormulaFields.Build(Table(NumberFormula(20, expression)), isAppEncrypted: false);

        standIns.Should().ContainKey(1020);
        standIns[1020].PhysicalColumnName.Should().Be(sql);
        standIns[1020].TypeCode.Should().Be("Number");
        standIns[1020].IsSystem.Should().BeTrue("the repository then reads the expression where a column name goes");
        standIns[1020].PhysicalColumnName.Should().NotStartWith("c_", "never SELECTed as a stored column");
    }

    [Fact]
    public void Inlines_another_translated_formula()
    {
        var standIns = SqlFormulaFields.Build(Table(NumberFormula(20, "[Amount] * 1.18"), NumberFormula(21, "[F20] + 1")), false);

        standIns[1021].PhysicalColumnName.Should().Be("((((f_9) * (1.18))) + (1))");
    }

    [Theory]
    [InlineData("If([Amount] > 10, 1, 0)")]          // functions: not in the exact subset
    [InlineData("[Amount] ^ 2")]                     // power goes through double in the evaluator
    [InlineData("ToNumber([Name])")]                 // text conversion
    [InlineData("Round([Amount] * 1.18, 2)")]
    public void Leaves_everything_else_to_the_projector(string expression)
    {
        SqlFormulaFields.Build(Table(NumberFormula(20, expression)), false).Should().BeEmpty();
    }

    [Fact]
    public void Number_columns_stay_plain_in_an_encrypted_app_so_the_formula_still_runs_in_sql()
    {
        // Number / Currency / Percent / Rating are encryption-exempt (PhysicalNaming.IsEncryptionExemptTypeCode): SQL reads real numbers.
        SqlFormulaFields.Build(Table(NumberFormula(20, "[Amount] * 2")), isAppEncrypted: true).Should().ContainKey(1020);
    }

    [Fact]
    public void Never_translates_over_a_column_that_holds_ciphertext()
    {
        // A field that does store ciphertext (a Text field in an encrypted app) never reaches SQL, even behind ToNumber().
        SqlFormulaFields.Build(Table(NumberFormula(20, "ToNumber([Name]) * 2")), isAppEncrypted: true).Should().BeEmpty();

        // The column test itself: an encrypted, non-exempt field gives no SQL column.
        var fields = Table();
        var name = fields.Single(f => f.Fid == Name);
        name.IsEncrypted = true;
        PowerBase.Application.Relationships.EncryptedRowFilter.IsEncrypted(name, false).Should().BeTrue();
    }

    [Fact]
    public void A_ready_stored_formula_keeps_its_stored_column()
    {
        SqlFormulaFields.Build(Table(NumberFormula(20, "[Amount] * 2")), false, new HashSet<long> { 1020 }).Should().BeEmpty();
    }

    [Fact]
    public void A_cycle_is_never_translated()
    {
        SqlFormulaFields.Build(Table(NumberFormula(20, "[F21] + 1"), NumberFormula(21, "[F20] + 1")), false).Should().BeEmpty();
    }

    // ── SQL gives the engine's value (needs a SQL Server) ─────────────────────────────────────

    public static readonly TheoryData<string> EqualityExpressions = new()
    {
        "[Amount] * 1.18",
        "[Qty] * [Price] - 2.5",
        "-[Amount] + [Qty] * 3",
        "[Amount] / [Qty]",
        "([Amount] - [Price]) / ([Qty] + 1)",
        "[Record ID#] * 0.5 + [Amount]",
    };

    [Theory]
    [MemberData(nameof(EqualityExpressions))]
    public async Task Sql_matches_the_formula_engine_row_by_row(string expression)
    {
        var connectionString = Environment.GetEnvironmentVariable("POWERBASE_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var fields = Table(NumberFormula(20, expression));
        var sql = SqlFormulaFields.Build(fields, false)[1020].PhysicalColumnName!;

        // Rows with blanks, zeros, negatives and 4-decimal values (the DECIMAL(18,4) of a Number column).
        var rnd = new Random(20261126);
        decimal? Value() => rnd.Next(6) switch { 0 => null, 1 => 0m, _ => Math.Round((decimal)(rnd.NextDouble() * 2000 - 1000), 4) };
        var rows = Enumerable.Range(1, 400).Select(id => (Id: (long)id, A: Value(), Q: Value(), P: Value())).ToList();

        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        await db.ExecuteAsync("CREATE TABLE #t (Id BIGINT PRIMARY KEY, f_9 DECIMAL(18,4) NULL, f_10 DECIMAL(18,4) NULL, f_11 DECIMAL(18,4) NULL)");
        foreach (var r in rows)
            await db.ExecuteAsync("INSERT #t VALUES (@Id, @A, @Q, @P)", new { r.Id, r.A, r.Q, r.P });
        var fromSql = (await db.QueryAsync<(long Id, decimal? V)>($"SELECT Id, {sql} FROM #t")).ToDictionary(x => x.Id, x => x.V);

        var engine = new FormulaEngine();
        var compiled = engine.Compile(expression, new AppFieldSchema(fields), PowerBase.Formula.Types.FormulaType.Number);
        compiled.HasErrors.Should().BeFalse();
        var mismatches = new List<string>();
        foreach (var r in rows)
        {
            var ctx = new Values(new Dictionary<long, object?> { [3] = r.Id, [Amount] = r.A, [Qty] = r.Q, [Price] = r.P });
            var v = engine.Evaluate(compiled, ctx, EvaluationOptions.Default);
            decimal? expected = v.IsNull ? null : v.AsNumber();
            var actual = fromSql[r.Id];
            var same = expected is null ? actual is null
                : actual is not null && (expected == actual
                    // Division: SQL keeps at least 6 decimals (DECIMAL scale rules), C# keeps 28 digits — the same value to 1e-6.
                    || (expression.Contains('/') && Math.Abs(expected.Value - actual.Value) <= 0.000001m));
            if (!same) mismatches.Add($"Id {r.Id}: engine {expected?.ToString(CultureInfo.InvariantCulture) ?? "blank"}, SQL {actual?.ToString(CultureInfo.InvariantCulture) ?? "blank"}");
        }
        mismatches.Should().BeEmpty();
    }

    private sealed class Values(IReadOnlyDictionary<long, object?> values) : IRecordContext
    {
        public object? GetValue(long fid) => values.TryGetValue(fid, out var v) ? v : null;
    }
}
