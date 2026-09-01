using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Binding;

/// <summary>
/// Resolves <c>[Field Name]</c> references against an <see cref="IFieldSchema"/>,
/// stamping each <see cref="FieldRefExpr"/> with its Fid and type and collecting
/// the set of referenced fields. Unknown fields and reserved syntax
/// (<c>[old.*]</c>, relationship references) are reported as diagnostics.
/// </summary>
public static class Binder
{
    /// <summary>A schema that resolves no aliases — the default when a caller has no table-alias
    /// concept to offer (formula fields today), so every <c>[_DBID_*]</c> token behaves exactly as
    /// before this existed: an ordinary unresolved field reference.</summary>
    private sealed class NoAliasSchema : ITableAliasSchema
    {
        public static readonly NoAliasSchema Instance = new();
        public bool TryResolve(string alias, out string tableId) { tableId = string.Empty; return false; }
    }

    public static IReadOnlyList<long> Bind(Expr root, IFieldSchema schema, List<FormulaDiagnostic> diagnostics, ITableAliasSchema? aliasSchema = null)
    {
        var fids = new List<long>();
        Walk(root, schema, aliasSchema ?? NoAliasSchema.Instance, diagnostics, fids);
        return fids.Distinct().ToList();
    }

    private static void Walk(Expr expr, IFieldSchema schema, ITableAliasSchema aliasSchema, List<FormulaDiagnostic> diagnostics, List<long> fids)
    {
        switch (expr)
        {
            case FieldRefExpr f:
                Resolve(f, schema, aliasSchema, diagnostics, fids);
                break;
            case UnaryExpr u:
                Walk(u.Operand, schema, aliasSchema, diagnostics, fids);
                break;
            case BinaryExpr b:
                Walk(b.Left, schema, aliasSchema, diagnostics, fids);
                Walk(b.Right, schema, aliasSchema, diagnostics, fids);
                break;
            case FunctionCallExpr c:
                foreach (var arg in c.Args) Walk(arg, schema, aliasSchema, diagnostics, fids);
                break;
            case LetExpr l:
                foreach (var decl in l.Declarations) Walk(decl.Value, schema, aliasSchema, diagnostics, fids);
                Walk(l.Body, schema, aliasSchema, diagnostics, fids);
                break;
            // LiteralExpr, VariableRefExpr, ErrorExpr: nothing to bind — a variable resolves
            // against the formula's own declarations in the type checker, not the field schema.
        }
    }

    private static void Resolve(FieldRefExpr f, IFieldSchema schema, ITableAliasSchema aliasSchema, List<FormulaDiagnostic> diagnostics, List<long> fids)
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

        // A real field with this name always wins above; only fall through to table-alias
        // resolution when there isn't one — matches how table aliases never collide with field
        // names in practice.
        if (aliasSchema.TryResolve(f.Name, out var tableId))
        {
            f.TableAliasId = tableId;
            f.IsBound = true;
            f.Type = FormulaType.Text;
            return;
        }

        diagnostics.Add(new FormulaDiagnostic(
            FormulaErrorCode.UnknownField,
            $"Unknown field [{f.Name}].",
            f.Span));
    }
}
