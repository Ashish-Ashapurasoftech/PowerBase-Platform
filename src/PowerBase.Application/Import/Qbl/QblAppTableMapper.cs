using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// App- and table-level QBL → PBL mappings (QBL_TO_PBL_MAPPING_SPEC.md §1–2). Pure functions over the parsed AST.
/// Security-sensitive booleans are only mapped where the two settings are the same question asked in opposite
/// directions; everything else is reported, never guessed.
/// </summary>
public static class QblAppTableMapper
{
    private static readonly HashSet<string> CurrencySymbols = new(StringComparer.Ordinal) { "$", "₹", "€", "£", "¥" };

    // Date patterns PowerBase understands: tokens YYYY/YY/MM/DD in any order, "-" separated.
    private static readonly HashSet<string> DateTokens = new(StringComparer.OrdinalIgnoreCase) { "YYYY", "YY", "MM", "DD" };

    // ---- app ----

    public static PblAppFormatting Formatting(QblResourceNode app, List<PblIssue> issues, string appRef)
    {
        var f = new PblAppFormatting();

        var symbol = Str(app, "CurrencySymbol")?.Trim();
        if (!string.IsNullOrEmpty(symbol))
        {
            if (CurrencySymbols.Contains(symbol)) f.CurrencySymbol = symbol;
            else issues.Add(Warn("QBL_APP_CURRENCY_SYMBOL_UNSUPPORTED", $"Currency symbol '{symbol}' is not one of $ ₹ € £ ¥; using '$'.", appRef));
        }

        // Quickbase "Between" puts the symbol after a leading minus sign (-$123.45): PowerBase calls that BetweenSignAndNumber.
        f.CurrencyPosition = Str(app, "CurrencySymbolPosition") switch
        {
            "After" => "After",
            "Between" => "BetweenSignAndNumber",
            _ => "Before",
        };

        var numberFormat = Nested(app, "NumberFormat");
        if (Str(numberFormat, "SeparatorStyle") == "PeriodGroupingCommaDecimal")
        {
            f.ThousandSeparator = ".";
            f.DecimalSeparator = ",";
            issues.Add(Warn("QBL_APP_DECIMAL_COMMA_UNSUPPORTED", "The source app writes decimals with a comma (1.234.567,89); PowerBase always uses a decimal point, so only the '.' digit grouping is carried.", appRef));
        }

        f.DateFormat = DateFormat(Str(app, "DateFormat"), issues, appRef);
        return f;
    }

    /// <summary>"MM-DD-YYYY", "DD-MM-YY", "YYYY-MM-DD" … → the same tokens in the same order, "-" separated; null/unknown → "MM-DD-YYYY".</summary>
    public static string DateFormat(string? qbDateFormat, List<PblIssue>? issues = null, string? ref_ = null)
    {
        const string fallback = "MM-DD-YYYY";
        if (string.IsNullOrWhiteSpace(qbDateFormat))
            return fallback;

        var tokens = qbDateFormat.Trim().Split('-', '/', '.');
        if (tokens.Length == 3 && tokens.All(t => DateTokens.Contains(t)) && tokens.Select(t => t.ToUpperInvariant()).Distinct().Count() == 3)
            return string.Join("-", tokens.Select(t => t.ToUpperInvariant()));

        issues?.Add(Warn("QBL_APP_DATE_FORMAT_UNSUPPORTED", $"Date format '{qbDateFormat}' is not recognised; using {fallback}.", ref_));
        return fallback;
    }

    /// <summary>
    /// Copy/export/connect/token switches. Only mappings whose meaning is the same question in the same or exactly
    /// opposite direction are made: CanCopyApp, CanExportData, DisableNonAdminSyncConnections (inverted), RequireAppToken.
    /// <c>Searchable</c> (included in Quickbase's own global search) is NOT mapped to HideFromPublicSearch (keeping the app
    /// out of external search engines) — different questions, so it is left at PowerBase's default and reported.
    /// </summary>
    public static PblAppSecurity Security(QblResourceNode app, List<PblIssue> issues, string appRef)
    {
        var s = new PblAppSecurity();

        // "Everyone" → non-admins may; "Admin" → only admins. Unknown stays at PowerBase's default (the stricter one for copy).
        s.AllowNonAdminsToCopy = Str(app, "CanCopyApp") switch { "Everyone" => true, "Admin" => false, _ => null };
        s.AllowNonAdminsToExport = Str(app, "CanExportData") switch { "Everyone" => true, "Admin" => false, _ => null };

        // Source disables → PowerBase allows is the exact opposite.
        if (Bool(app, "DisableNonAdminSyncConnections") is { } disable)
            s.AllowNonAdminsToConnect = !disable;

        s.RequireAppTokens = Bool(app, "RequireAppToken");

        if (app.Properties.ContainsKey("Searchable"))
            issues.Add(Warn("QBL_APP_SEARCHABLE_NOT_MAPPED", "'Searchable' (Quickbase global search) is not the same setting as PowerBase's 'hide from public search'; left at the default.", appRef));

        return s;
    }

    public static string? BrandColor(QblResourceNode app)
    {
        var header = Nested(Nested(app, "Branding"), "AppHeader");
        var color = Str(header, "BackgroundColor")?.Trim();
        return !string.IsNullOrEmpty(color) && IsHex(color) ? color : null;
    }

    /// <summary>The app's table refs in the order the source lists them (<c>TableOrder</c>), or null when absent.</summary>
    public static List<string>? TableOrder(QblResourceNode app)
    {
        if (!app.Properties.TryGetValue("TableOrder", out var raw) || raw is not IEnumerable<object?> list || raw is string)
            return null;

        return list.Select(x => (x as QblRef)?["Table"] ?? (x as QblRef)?["Field"]).Where(x => x is not null).Select(x => x!).ToList();
    }

    // ---- table ----

    public static bool? IsShowInBar(QblResourceNode table) => Bool(table, "IncludeInTableBar") == false ? false : null;

    public static bool? AutoSaveOnLeave(QblResourceNode table) => Bool(table, "SaveOnExit") == true ? true : null;

    public static string? AliasHint(QblResourceNode table)
    {
        var alias = Str(table, "AliasHint")?.Trim();
        return !string.IsNullOrEmpty(alias) && alias.StartsWith("_DBID_", StringComparison.Ordinal) && alias.Length > 6
            && alias.All(c => char.IsLetterOrDigit(c) || c == '_') ? alias : null;
    }

    // ---- readers ----

    private static string? Str(QblResourceNode node, string name) => node.Properties.TryGetValue(name, out var v) ? v?.ToString() : null;

    private static string? Str(IReadOnlyDictionary<string, object?>? d, string name) =>
        d is not null && d.TryGetValue(name, out var v) ? v?.ToString() : null;

    private static IReadOnlyDictionary<string, object?>? Nested(QblResourceNode node, string name) =>
        node.Properties.TryGetValue(name, out var v) ? v as IReadOnlyDictionary<string, object?> : null;

    private static IReadOnlyDictionary<string, object?>? Nested(IReadOnlyDictionary<string, object?>? d, string name) =>
        d is not null && d.TryGetValue(name, out var v) ? v as IReadOnlyDictionary<string, object?> : null;

    private static bool? Bool(QblResourceNode node, string name) =>
        node.Properties.TryGetValue(name, out var v) ? v switch { bool b => b, string s when bool.TryParse(s, out var b) => b, _ => null } : null;

    private static bool IsHex(string c) =>
        c.StartsWith('#') && (c.Length is 4 or 7 or 9) && c.Skip(1).All(Uri.IsHexDigit);

    private static PblIssue Warn(string code, string message, string? elementRef) =>
        new() { Severity = PblIssueSeverity.Warning, Code = code, Message = message, ElementRef = elementRef };
}
