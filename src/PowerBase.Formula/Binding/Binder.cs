using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Syntax;

namespace PowerBase.Formula.Binding;

/// <summary>
/// Resolves <c>[Field Name]</c> references against an <see cref="IFieldSchema"/>,
/// stamping each <see cref="FieldRefExpr"/> with its Fid and type and collecting
/// the set of referenced fields. Unknown fields and reserved syntax
/// (<c>[old.*]</c>, relationship references) are reported as diagnostics.
/// </summary>
public static class Binder
{
    public static IReadOnlyList<long> Bind(Expr root, IFieldSchema schema, List<FormulaDiagnostic> diagnostics)
    {
        var fids = new List<long>();
        Walk(root, schema, diagnostics, fids);
        return fids.Distinct().ToList();
    }

    private static void Walk(Expr expr, IFieldSchema schema, List<FormulaDiagnostic> diagnostics, List<long> fids)
    {
        switch (expr)
        {
            case FieldRefExpr f:
                Resolve(f, schema, diagnostics, fids);
                break;
            case UnaryExpr u:
                Walk(u.Operand, schema, diagnostics, fids);
                break;
            case BinaryExpr b:
                Walk(b.Left, schema, diagnostics, fids);
                Walk(b.Right, schema, diagnostics, fids);
                break;
            case FunctionCallExpr c:
                foreach (var arg in c.Args) Walk(arg, schema, diagnostics, fids);
                break;
            // LiteralExpr, ErrorExpr: nothing to bind.
        }
    }

    private static void Resolve(FieldRefExpr f, IFieldSchema schema, List<FormulaDiagnostic> diagnostics, List<long> fids)
    {
        if (f.Name.StartsWith("old.", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new FormulaDiagnostic(
                FormulaErrorCode.UnsupportedReference,
                "Referencing a field's previous value ([old.*]) is not supported yet.",
                f.Span));
            return;
        }

        if (schema.TryResolve(f.Name, out var field))
        {
            f.Fid = field.Fid;
            f.IsBound = true;
            f.Type = field.Type;
            fids.Add(field.Fid);
            return;
        }

        diagnostics.Add(new FormulaDiagnostic(
            FormulaErrorCode.UnknownField,
            $"Unknown field [{f.Name}].",
            f.Span));
    }
}
