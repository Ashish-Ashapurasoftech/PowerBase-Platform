using PowerBase.Formula.Builtins;
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

    public static IReadOnlyList<long> Bind(Expr root, IFieldSchema schema, List<FormulaDiagnostic> diagnostics, ITableAliasSchema? aliasSchema = null, IVariableSchema? variableSchema = null)
    {
        var fids = new List<long>();
        Walk(root, schema, aliasSchema ?? NoAliasSchema.Instance, variableSchema, diagnostics, fids, new Dictionary<string, LiteralExpr>(StringComparer.OrdinalIgnoreCase));
        return fids.Distinct().ToList();
    }

    /// <param name="constants">Variables declared so far whose value is a plain literal, by name — so
    /// <c>GetFieldValue($cfg)</c> can resolve when <c>cfg</c> was set to <c>14</c>.</param>
    private static void Walk(Expr expr, IFieldSchema schema, ITableAliasSchema aliasSchema, IVariableSchema? variableSchema, List<FormulaDiagnostic> diagnostics, List<long> fids,
        Dictionary<string, LiteralExpr> constants)
    {
        switch (expr)
        {
            case FieldRefExpr f:
                Resolve(f, schema, aliasSchema, variableSchema, diagnostics, fids);
                break;
            case UnaryExpr u:
                Walk(u.Operand, schema, aliasSchema, variableSchema, diagnostics, fids, constants);
                break;
            case BinaryExpr b:
                Walk(b.Left, schema, aliasSchema, variableSchema, diagnostics, fids, constants);
                Walk(b.Right, schema, aliasSchema, variableSchema, diagnostics, fids, constants);
                break;
            case FunctionCallExpr c:
                foreach (var arg in c.Args) Walk(arg, schema, aliasSchema, variableSchema, diagnostics, fids, constants);
                if (FieldValueCall.IsCall(c)) ResolveFieldValueCall(c, schema, constants, diagnostics, fids);
                break;
            case LetExpr l:
                foreach (var decl in l.Declarations)
                {
                    Walk(decl.Value, schema, aliasSchema, variableSchema, diagnostics, fids, constants);
                    if (!string.IsNullOrEmpty(decl.Name) && FieldValueCall.TryGetLiteral(decl.Value, constants, out var literal))
                        constants.TryAdd(decl.Name, literal);
                }
                Walk(l.Body, schema, aliasSchema, variableSchema, diagnostics, fids, constants);
                break;
            // LiteralExpr, VariableRefExpr, ErrorExpr: nothing to bind — a variable resolves
            // against the formula's own declarations in the type checker, not the field schema.
        }
    }

    /// <summary>Resolves <c>GetFieldValue(14)</c> against the schema as the field <c>fid_14</c>. An ID that is
    /// known up front (a literal, or a variable set to one) is bound now; one that is only known when the
    /// formula runs marks the call dynamic and is resolved per record by the evaluator.</summary>
    private static void ResolveFieldValueCall(FunctionCallExpr c, IFieldSchema schema, IReadOnlyDictionary<string, LiteralExpr> constants,
        List<FormulaDiagnostic> diagnostics, List<long> fids)
    {
        if (c.Args.Count != 1)
        {
            Fn.RequireArity(FieldValueCall.Name, c.Args.Count, 1, 1, c.Span, diagnostics);
            return;
        }

        if (!FieldValueCall.TryGetLiteral(c.Args[0], constants, out var literal))
        {
            c.IsDynamicFieldValue = true;
            return;
        }

        if (!FieldValueCall.TryGetFieldNumber(literal.Value, out var fid))
        {
            diagnostics.Add(new FormulaDiagnostic(
                FormulaErrorCode.InvalidArgument,
                $"{FieldValueCall.Name} expects a field ID like 14 or \"fid_14\".",
                c.Args[0].Span));
            return;
        }

        if (schema.TryResolve($"{FieldIdReference.Prefix}{fid}", out var field))
        {
            c.ResolvedField = field;
            fids.Add(field.Fid);
            return;
        }

        diagnostics.Add(new FormulaDiagnostic(
            FormulaErrorCode.UnknownField,
            $"Unknown field [{FieldIdReference.Prefix}{fid}].",
            c.Args[0].Span));
    }

    private static void Resolve(FieldRefExpr f, IFieldSchema schema, ITableAliasSchema aliasSchema, IVariableSchema? variableSchema, List<FormulaDiagnostic> diagnostics, List<long> fids)
    {
        if (f.Name.StartsWith("old.", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new FormulaDiagnostic(
                FormulaErrorCode.UnsupportedReference,
                "Referencing a field's previous value ([old.*]) is not supported yet.",
                f.Span));
            return;
        }

        // An app variable wins over a field of the same name — Quickbase's rule, so a Quickbase formula means
        // the same here. It is a constant of type Text (a formula converts it: ToNumber([Tax])), not a field,
        // so it adds no field dependency. Saved formulas name a variable by id ([var_5|Tax]), so a variable or
        // field added later can't change what an existing formula refers to.
        if (variableSchema is not null && variableSchema.TryResolve(f.Name, out var variable))
        {
            f.VariableId = variable.Id;
            f.VariableValue = variable.Value;
            f.IsBound = true;
            f.Type = FormulaType.Text;

            // Typed by name while a field of that name is on this table too: say which one wins, always.
            if (!VariableIdReference.TryParse(f.Name, out _) && schema.TryResolve(f.Name, out _))
                diagnostics.Add(new FormulaDiagnostic(
                    FormulaErrorCode.AmbiguousReference,
                    $"Note: [{f.Name}] is both an App Variable and a Field on this table. This formula will use the App Variable.",
                    f.Span,
                    FormulaSeverity.Warning));
            return;
        }

        if (schema.TryResolve(f.Name, out var field))
        {
            f.Fid = field.Fid;
            f.IsBound = true;
            f.Type = field.Type;
            f.IsAddress = field.IsAddress;
            fids.Add(field.Fid);
            return;
        }

        // [Address : Street 1] - one part of an Address field.
        if (AddressParts.TrySplit(f.Name, out var addressName, out var partLabel) && schema.TryResolve(addressName, out var addressField) && addressField.IsAddress)
        {
            var key = AddressParts.KeyOf(partLabel);
            if (key is null)
            {
                diagnostics.Add(new FormulaDiagnostic(
                    FormulaErrorCode.UnknownField,
                    $"[{addressName}] has no part called {partLabel}. Use Street 1, Street 2, City, State, Postal Code or Country.",
                    f.Span));
                return;
            }
            f.Fid = addressField.Fid;
            f.IsBound = true;
            f.Type = addressField.Type;
            f.IsAddress = true;
            f.AddressPart = key;
            fids.Add(addressField.Fid);
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
