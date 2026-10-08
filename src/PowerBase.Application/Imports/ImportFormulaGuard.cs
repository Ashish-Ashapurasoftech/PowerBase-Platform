namespace PowerBase.Application.Imports;

/// <summary>Stops a formula nested deeper than anyone writes by hand from reaching the formula parser: the parser recurses once per
/// level, so a few hundred opening brackets would exhaust the stack and take the whole server down, not just fail the request.
/// Brackets inside quoted text and square-bracket field names do not count.</summary>
public static class ImportFormulaGuard
{
    public const int MaxNesting = 50;

    public static bool IsTooDeep(string formula)
    {
        int depth = 0, max = 0;
        char? quote = null;
        for (var i = 0; i < formula.Length; i++)
        {
            var c = formula[i];
            if (quote is { } q)
            {
                if (c == q) quote = null;
                continue;
            }
            switch (c)
            {
                case '"': quote = c; break;
                case '[': quote = ']'; break; // a field name: [Unit price (USD)]
                case '(' or '{':
                    if (++depth > max) max = depth;
                    break;
                case ')' or '}': if (depth > 0) depth--; break;
            }
        }
        return max > MaxNesting;
    }
}
