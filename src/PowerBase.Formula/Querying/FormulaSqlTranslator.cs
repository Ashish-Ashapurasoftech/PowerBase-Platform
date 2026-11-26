using System.Globalization;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Querying;

/// <summary>
/// Turns a compiled Number formula into a T-SQL expression with exactly the evaluator's result, so a report can filter, sort and
/// total by it in SQL (every record, using the database) instead of working it out in memory for every row.
/// <para>
/// Only a small, exact subset is translated; anything else returns null and the caller keeps the in-memory path:
/// number literals, number field references (the caller decides which fields have a plain SQL column), unary minus / plus,
/// and <c>+ - * /</c>. Semantics match <c>Evaluator.Arithmetic</c>: a blank operand gives a blank result (SQL NULL propagation),
/// division by zero gives blank (<c>NULLIF(divisor, 0)</c>), decimals are exact for + - * (SQL DECIMAL arithmetic).
/// </para>
/// No user text ever reaches the SQL: literals are numbers formatted with the invariant culture and columns come from the caller.
/// </summary>
public static class FormulaSqlTranslator
{
    /// <summary>Deepest expression translated (keeps SQL decimal precision growth well inside DECIMAL(38)).</summary>
    public const int MaxDepth = 12;

    /// <param name="column">The SQL expression of a Number field the formula reads (a column such as <c>f_9</c>, or the translation
    /// of another formula), or null when that field can't be read in SQL — the whole formula is then not translated.</param>
    public static string? TryTranslate(CompiledFormula formula, Func<long, string?> column)
    {
        if (formula.HasErrors || formula.UsesDynamicFieldReference || formula.Root is null) return null;
        if (formula.ResultType != FormulaType.Number) return null;
        return Translate(formula.Root, column, 0);
    }

    private static string? Translate(Expr e, Func<long, string?> column, int depth)
    {
        if (depth > MaxDepth) return null;
        switch (e)
        {
            case LiteralExpr { Value: var v } when v.Type == FormulaType.Number && !v.IsNull:
                // Parenthesised so "-(1.5)" and "a - -1" stay valid; DECIMAL literal keeps exact decimal arithmetic.
                return "(" + v.AsNumber().ToString("0.############################", CultureInfo.InvariantCulture) + ")";

            case FieldRefExpr f when f.IsBound && f.Type == FormulaType.Number
                                     && !f.IsAddress && f.TableAliasId is null && f.VariableId is null && f.VariableValue is null:
                return column(f.Fid) is { } col ? "(" + col + ")" : null;

            case UnaryExpr { Op: UnaryOp.Plus } u when u.Type == FormulaType.Number:
                return Translate(u.Operand, column, depth + 1);

            case UnaryExpr { Op: UnaryOp.Negate } u when u.Type == FormulaType.Number:
                return Translate(u.Operand, column, depth + 1) is { } o ? "(-" + o + ")" : null;

            case BinaryExpr b when b.Type == FormulaType.Number && b.Left.Type == FormulaType.Number && b.Right.Type == FormulaType.Number:
            {
                var l = Translate(b.Left, column, depth + 1);
                var r = l is null ? null : Translate(b.Right, column, depth + 1);
                if (l is null || r is null) return null;
                return b.Op switch
                {
                    BinaryOp.Add => $"({l} + {r})",
                    BinaryOp.Subtract => $"({l} - {r})",
                    BinaryOp.Multiply => $"({l} * {r})",
                    BinaryOp.Divide => $"({l} / NULLIF({r}, 0))",
                    _ => null,   // Power: the evaluator goes through double; not exact in SQL DECIMAL
                };
            }

            default:
                return null;
        }
    }
}
