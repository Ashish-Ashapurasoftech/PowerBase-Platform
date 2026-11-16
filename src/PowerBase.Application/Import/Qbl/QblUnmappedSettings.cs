namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Quickbase settings PowerBase has no equivalent for. The converter carries every setting it can; for the ones it cannot, this says which
/// setting of which field/report was set to something other than the Quickbase default, in plain words, so the import reports it (and a
/// field's audit history keeps it) instead of dropping it silently. A setting at its default is not reported: nothing was lost.
/// Pure — no I/O.
/// </summary>
public static class QblUnmappedSettings
{
    private sealed record Rule(string Path, Func<string?, bool> IsSet, Func<string?, string> Message, string[]? Types = null);

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static readonly string[] Urls = ["QB::Field::URL", "QB::Field::URL::Formula", "QB::Field::Lookup"];
    private static readonly string[] Phones = ["QB::Field::PhoneNumber", "QB::Field::PhoneNumber::Formula", "QB::Field::Lookup"];
    private static readonly string[] Emails = ["QB::Field::Email", "QB::Field::Email::Formula", "QB::Field::Lookup"];

    private static readonly Rule[] FieldRules =
    [
        new("AllowDataCopy", v => Is(v, "false"),
            _ => "\"Allow data to be copied\" is turned off in Quickbase; PowerBase has no per-field switch for it, so the field's data is copied with the app."),
        new("Summary.DisplayAsLinkToChildren", v => Is(v, "true"),
            _ => "Quickbase shows this summary as a link to the child records; PowerBase shows the plain value.", ["QB::Field::Summary"]),
        new("Abbreviate", v => Is(v, "true"), _ => "\"Abbreviate\" is on in Quickbase; PowerBase does not abbreviate the value.", Urls),
        new("ButtonColor", NotBlank, v => $"The button colour ({v}) is not carried; PowerBase uses its own button colour.", Urls),
        new("InputOptions.AllowCreateChoices", v => Is(v, "true"),
            _ => "Quickbase lets users add new choices while entering data; PowerBase has no such option.", ["QB::Field::TextMultipleChoice", "QB::Field::MultiselectText"]),
        new("InputOptions.InputType", v => Is(v, "FromAnotherField"),
            _ => "The choices come from another field's values in Quickbase; use the field's shared-values setting in PowerBase.",
            ["QB::Field::TextMultipleChoice", "QB::Field::MultiselectText"]),
        new("UseNewWindow", v => Is(v, "true"), _ => "The link opens in a new window in Quickbase; PowerBase's report link has no such option.", ["QB::Field::ReportLink"]),
        new("IsInternational", v => Is(v, "true"), _ => "The phone number accepts international formats in Quickbase; PowerBase does not distinguish them.", Phones),
        new("HasExtension", v => Is(v, "true"), _ => "The phone number has an extension part in Quickbase; PowerBase's phone field has none.", ["QB::Field::PhoneNumber"]),
        new("DefaultCountry", v => NotBlank(v) && !Is(v, "US"), v => $"The default country ({v}) is not carried.", ["QB::Field::PhoneNumber"]),
        new("SortByDomain", v => Is(v, "true"), _ => "Quickbase sorts this email field by domain; PowerBase sorts by the whole address.", Emails),
        new("EnableEmailAll", v => Is(v, "true"), _ => "Quickbase offers 'email all' on this field; PowerBase has no such option.", Emails),
        new("DefaultDomain", NotBlank, v => $"The default email domain ({v}) is not carried.", Emails),
        new("UseNewBrowserWindow", v => Is(v, "false"), _ => "Quickbase opens the file in the same window; PowerBase opens it in a new one.", ["QB::Field::FileAttachment"]),
        new("AllowOpenAccess", v => Is(v, "true"), _ => "Quickbase allows open (no sign-in) access to the files; PowerBase never does.", ["QB::Field::FileAttachment"]),
        new("AllowMentions", v => Is(v, "true"), _ => "@mentions are allowed in this field in Quickbase; PowerBase does not support mentions.", ["QB::Field::RichText", "QB::Field::Lookup"]),
        new("Choices.AllowCreateChoices", v => Is(v, "true"), _ => "Quickbase lets users add users to the choices while entering data; PowerBase has no such option.", ["QB::Field::User"]),
        new("Choices.AllowInviteExternalUser", v => Is(v, "true"), _ => "Quickbase lets users invite new users from this field; PowerBase has no such option.", ["QB::Field::User"]),
        new("SnapshotSourceLookupField", NotBlank, _ => "The snapshot reads its value through a lookup field in Quickbase; PowerBase snapshots a field of the same table only.", null),
        new("Format", v => Is(v, "International"), _ => "The address uses the international format in Quickbase; PowerBase's address field has one format.", ["QB::Field::Address"]),
        new("OverrideSubFieldAccess", v => Is(v, "true"), _ => "Quickbase gives this field its own access rules apart from its source field; PowerBase does not.", null),
        new("Reference.UseRecordPickerForGridEdit", v => Is(v, "true"), _ => "Quickbase uses the record picker when editing this reference in a grid; PowerBase uses its own control.", ["QB::Field::Reference"]),
    ];

    private static readonly (string Key, string Label)[] AppUiSwitches =
    [
        ("HideFavorites", "favorites"), ("HideHelp", "help"), ("HideSearch", "search"),
        ("HideSettings", "settings"), ("HideTestAs", "\"test as\""), ("HideUsers", "users"),
    ];

    private static readonly (string Key, string Label)[] ToolbarSwitches =
    [
        ("HideNewRecord", "new record"), ("HideImportExport", "import / export"), ("HidePrint", "print"), ("HideEmail", "email"),
        ("HideGridEdit", "grid edit"), ("HideInBar", "the table's button in the bar"), ("HideSaveSpreadsheet", "save as spreadsheet"),
        ("HideCreateCustomizeReport", "create / customize report"), ("HidePersonalSettings", "personal settings"),
    ];

    private static bool IsOn(object? block, string key) =>
        block is IReadOnlyDictionary<string, object?> ro ? ro.TryGetValue(key, out var v) && Is(ValueText(v), "true")
        : block is IDictionary<string, object?> d && d.TryGetValue(key, out var v2) && Is(ValueText(v2), "true");

    private static object? Child(object? block, string key) =>
        block is IReadOnlyDictionary<string, object?> ro ? (ro.TryGetValue(key, out var v) ? v : null)
        : block is IDictionary<string, object?> d && d.TryGetValue(key, out var v2) ? v2 : null;

    /// <summary>The Quickbase role settings PowerBase has no equivalent for (which app menu items and table toolbar buttons the role hides), in plain
    /// words. PowerBase roles only carry permissions, so these cannot be applied; a role that hides nothing yields nothing.</summary>
    public static IReadOnlyList<string> ForRole(QblResourceNode role)
    {
        var messages = new List<string>();

        var appUi = role.Properties.GetValueOrDefault("AppUI");
        var hiddenMenu = AppUiSwitches.Where(s => IsOn(appUi, s.Key)).Select(s => s.Label).ToList();
        if (hiddenMenu.Count > 0)
            messages.Add($"Quickbase hides these app menu items from this role: {string.Join(", ", hiddenMenu)}. PowerBase roles cannot hide menu items, so they stay visible.");

        var tableUi = role.Properties.GetValueOrDefault("TableUI");
        var global = Child(tableUi, "Global");
        var hiddenButtons = ToolbarSwitches.Where(s => IsOn(global, s.Key)).Select(s => s.Label).ToList();
        if (hiddenButtons.Count > 0)
            messages.Add($"Quickbase hides these table buttons from this role on every table: {string.Join(", ", hiddenButtons)}. PowerBase roles cannot hide toolbar buttons, so they stay visible.");

        var overriddenTables = 0;
        if (tableUi is IReadOnlyDictionary<string, object?> ro)
        {
            foreach (var (key, value) in ro)
                if (key != "Global" && ToolbarSwitches.Any(s => IsOn(value, s.Key))) overriddenTables++;
        }
        else if (tableUi is IDictionary<string, object?> d)
        {
            foreach (var (key, value) in d)
                if (key != "Global" && ToolbarSwitches.Any(s => IsOn(value, s.Key))) overriddenTables++;
        }
        if (overriddenTables > 0)
            messages.Add($"Quickbase hides table buttons from this role on {overriddenTables} specific table(s) (their own toolbar settings). PowerBase roles cannot hide toolbar buttons, so they stay visible.");

        return messages;
    }

    /// <summary>The Quickbase settings of a field that PowerBase cannot carry, set to something other than their default.</summary>
    public static IReadOnlyList<string> ForField(QblResourceNode field)
    {
        var messages = new List<string>();
        foreach (var rule in FieldRules)
        {
            if (rule.Types is not null && !rule.Types.Contains(field.Type, StringComparer.Ordinal)) continue;
            var value = Get(field, rule.Path);
            if (rule.IsSet(value)) messages.Add(rule.Message(value));
        }
        return messages;
    }

    /// <summary>The Quickbase settings of a report or chart that PowerBase cannot carry, set to something other than their default.</summary>
    public static IReadOnlyList<string> ForReport(QblResourceNode report)
    {
        var messages = new List<string>();

        if (report.Type == "QB::Report::Table" || report.Type == "QB::Report::GridEdit")
        {
            if (Get(report, "ColorCoding.ColorBy") is { } by && (Is(by, "RowValue") || Is(by, "Formula")))
                messages.Add($"Colour coding by {(Is(by, "Formula") ? "a formula" : "a field value")} is not carried; PowerBase reports have no colour coding.");
            if (Is(Get(report, "HideMainTable"), "true"))
                messages.Add("'Hide the main table' is on in Quickbase; PowerBase has no such option.");
            if (Is(Get(report, "ShowSummaryTable"), "true"))
                messages.Add("'Show the summary table' is on in Quickbase; PowerBase has no such option.");
            if (Is(Get(report, "DisplayOnlyNewOrChangedRecords"), "true"))
                messages.Add("'Display only new or changed records' is on in Quickbase; PowerBase has no such option.");
            if (Get(report, "DefaultRowHeight") is { } height && NotBlank(height) && !Is(height, "Default"))
                messages.Add($"The row height ({height}) is not carried.");
            if (Get(report, "RowNumberVisibility") is { } rows && NotBlank(rows) && !Is(rows, "Default"))
                messages.Add($"Row number visibility ({rows}) is not carried.");

            var columns = (report.Properties.TryGetValue("ColumnsUI", out var ui) ? ui : null) as System.Collections.IEnumerable;
            if (columns is not null && ui is not string)
            {
                var aligned = 0;
                var pinned = 0;
                foreach (var column in columns)
                {
                    if (column is not IDictionary<string, object?> c) continue;
                    if (c.TryGetValue("Alignment", out var a) && a?.ToString() is { } al && NotBlank(al) && !Is(al, "Auto")) aligned++;
                    if (c.TryGetValue("Pinned", out var p) && p?.ToString() is { } pi && NotBlank(pi) && !Is(pi, "Default") && !Is(pi, "None")) pinned++;
                }
                if (aligned > 0) messages.Add($"The alignment of {aligned} column(s) is not carried.");
                if (pinned > 0) messages.Add($"{pinned} pinned column(s) are not pinned in PowerBase.");
            }
        }

        if (report.Type.StartsWith("QB::Chart::", StringComparison.Ordinal) || report.Type == "QB::Report::Summary")
        {
            if (Get(report, "DrilldownReport") is { } drill && NotBlank(drill) && !Is(drill, "Default"))
                messages.Add("The drill-down report is not carried; PowerBase drills down to the records.");
            if (AnyKey(report.Properties, "LogScale", v => Is(v, "true")))
                messages.Add("The logarithmic scale is not carried.");
            if (AnyKey(report.Properties, "LegendColors", NonEmptyCollection) || AnyKey(report.Properties, "LegendColor", NotBlank))
                messages.Add("The custom legend colours are not carried; PowerBase picks the colours.");
            if (report.Type == "QB::Chart::Gauge" && report.Properties.TryGetValue("Colors", out var colors) && colors is not null)
                messages.Add("The gauge colour bands and thresholds are not carried.");
        }

        if (report.Type == "QB::Report::Calendar")
            messages.Add("Calendar reports have no PowerBase equivalent.");

        return messages;
    }

    private static bool NonEmptyCollection(string? v) => NotBlank(v);

    /// <summary>The value at a dotted path inside a node's properties (nested blocks), as text; null when absent.</summary>
    private static string? Get(QblResourceNode node, string path)
    {
        object? current = node.Properties;
        foreach (var part in path.Split('.'))
        {
            current = current switch
            {
                IReadOnlyDictionary<string, object?> ro when ro.TryGetValue(part, out var v) => v,
                IDictionary<string, object?> d when d.TryGetValue(part, out var v) => v,
                _ => null,
            };
            if (current is null) return null;
        }

        return current switch
        {
            bool b => b ? "true" : "false",
            QblRef or QblBadRef or QblVarRef => "(reference)",
            System.Collections.IEnumerable e and not string => e.Cast<object?>().Any() ? "(list)" : null,
            _ => current.ToString(),
        };
    }

    /// <summary>True when any nested block has a property of that name whose value satisfies the test.</summary>
    private static bool AnyKey(object? block, string key, Func<string?, bool> test, int depth = 0)
    {
        if (depth > 8) return false;
        switch (block)
        {
            case IDictionary<string, object?> d:
                foreach (var (k, v) in d)
                {
                    if (string.Equals(k, key, StringComparison.Ordinal) && test(ValueText(v))) return true;
                    if (AnyKey(v, key, test, depth + 1)) return true;
                }
                return false;
            case IReadOnlyDictionary<string, object?> ro:
                foreach (var (k, v) in ro)
                {
                    if (string.Equals(k, key, StringComparison.Ordinal) && test(ValueText(v))) return true;
                    if (AnyKey(v, key, test, depth + 1)) return true;
                }
                return false;
            case string:
                return false;
            case System.Collections.IEnumerable list:
                foreach (var item in list)
                    if (AnyKey(item, key, test, depth + 1)) return true;
                return false;
            default:
                return false;
        }
    }

    private static string? ValueText(object? v) => v switch
    {
        null => null,
        bool b => b ? "true" : "false",
        string s => s,
        System.Collections.IEnumerable e => e.Cast<object?>().Any() ? "(list)" : null,
        _ => v.ToString(),
    };
}
