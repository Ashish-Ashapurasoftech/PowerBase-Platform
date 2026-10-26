namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Maps Quickbase icon display names ("Personal Record", "Application") to PowerBase icon-catalog ids
/// — the exact strings the UI persists and renders (<c>icon-catalog.ts</c>: a bare PrimeIcons suffix such as
/// <c>folder</c>, or <c>lucide:{name}</c>). One shared mapper for app and table icons.
///
/// Matching is case/spacing-insensitive. A name with no direct entry falls back to a keyword match, and
/// finally to the supplied default (<see cref="DefaultAppIcon"/> / <see cref="DefaultTableIcon"/>). A value
/// that already is a catalog id (or a <c>pi pi-x</c> class) is returned unchanged.
/// </summary>
public static class QblIconMapper
{
    public const string DefaultAppIcon = "database";
    public const string DefaultTableIcon = "table";

    private static readonly Dictionary<string, string> Direct = new(StringComparer.OrdinalIgnoreCase)
    {
        // Seen in real exports (cares.qbl).
        ["Folder"] = "folder",
        ["Personal Record"] = "id-card",
        ["Spreadsheet"] = "table",
        ["Edit"] = "pencil",
        ["Table Header"] = "table",
        ["Table Footer Gear"] = "cog",
        ["Group"] = "users",
        ["Team"] = "users",
        ["Team Members"] = "users",
        ["White List"] = "list-check",
        ["Attendance List"] = "list-check",
        ["Upload 2"] = "upload",
        ["Upload"] = "upload",
        ["Download"] = "download",
        ["Stock Market"] = "chart-line",
        ["Schedule"] = "calendar-clock",
        ["Calendar Year"] = "calendar",
        ["Calendar"] = "calendar",
        ["Salesman"] = "briefcase",
        ["Nurse"] = "heart",
        ["Envelope"] = "envelope",
        ["At Sign"] = "at",
        ["Accessibility"] = "user",
        ["Sole Proprietorship"] = "user",
        ["Sole Proprietorship Pencil"] = "user-edit",
        ["School"] = "building",
        ["College"] = "graduation-cap",
        ["Report 2"] = "chart-bar",
        ["Report"] = "chart-bar",
        ["Product"] = "box",
        ["Person"] = "user",
        ["Pages"] = "file",
        ["File"] = "file",
        ["Line Items"] = "list",
        ["Key"] = "key",
        ["Directive Board"] = "clipboard",
        ["Copy"] = "copy",
        ["Contact"] = "address-book",
        ["Building"] = "building",
        ["Black Checkmark"] = "check",
        ["Application"] = DefaultAppIcon,

        // Common Quickbase names not in cares.
        ["Contacts"] = "address-book",
        ["Customer"] = "user",
        ["Customers"] = "users",
        ["Users"] = "users",
        ["User"] = "user",
        ["Task"] = "check-square",
        ["Tasks"] = "check-square",
        ["Checklist"] = "list-check",
        ["Check"] = "check",
        ["Settings"] = "cog",
        ["Gear"] = "cog",
        ["Money"] = "money-bill",
        ["Dollar"] = "dollar",
        ["Invoice"] = "receipt",
        ["Receipt"] = "receipt",
        ["Shopping Cart"] = "shopping-cart",
        ["Cart"] = "shopping-cart",
        ["Map"] = "map",
        ["Globe"] = "globe",
        ["Phone"] = "phone",
        ["Mail"] = "envelope",
        ["Email"] = "envelope",
        ["Home"] = "home",
        ["House"] = "home",
        ["Star"] = "star",
        ["Flag"] = "flag",
        ["Tag"] = "tag",
        ["Tags"] = "tags",
        ["Lock"] = "lock",
        ["Document"] = "file",
        ["Documents"] = "file",
        ["Notes"] = "book",
        ["Book"] = "book",
        ["Chart"] = "chart-bar",
        ["Camera"] = "camera",
        ["Image"] = "image",
        ["Truck"] = "truck",
        ["Car"] = "car",
        ["Clock"] = "clock",
        ["Time"] = "clock",
        ["Inbox"] = "inbox",
        ["Heart"] = "heart",
        ["Hospital"] = "building",
        ["Doctor"] = "heart",
    };

    // Checked in order against the lower-cased name when there is no direct entry.
    private static readonly (string Keyword, string Icon)[] Keywords =
    [
        ("calendar", "calendar"), ("schedule", "calendar-clock"), ("mail", "envelope"), ("email", "envelope"),
        ("phone", "phone"), ("user", "user"), ("person", "user"), ("people", "users"), ("team", "users"),
        ("group", "users"), ("folder", "folder"), ("file", "file"), ("document", "file"), ("report", "chart-bar"),
        ("chart", "chart-bar"), ("list", "list"), ("table", "table"), ("building", "building"), ("money", "money-bill"),
        ("dollar", "dollar"), ("cart", "shopping-cart"), ("key", "key"), ("lock", "lock"), ("star", "star"),
        ("flag", "flag"), ("map", "map"), ("home", "home"), ("school", "building"), ("nurse", "heart"),
        ("health", "heart"), ("check", "check"),
    ];

    /// <summary>The catalog id for a Quickbase icon name, or <paramref name="fallback"/> when nothing matches
    /// (null/blank included).</summary>
    public static string Map(string? qbIconName, string fallback)
    {
        var name = qbIconName?.Trim();
        if (string.IsNullOrEmpty(name))
            return fallback;

        // Already a PowerBase value — keep it.
        if (name.StartsWith("lucide:", StringComparison.Ordinal) || name.StartsWith("pi pi-", StringComparison.Ordinal))
            return name;

        if (Direct.TryGetValue(name, out var direct))
            return direct;

        var lower = name.ToLowerInvariant();
        foreach (var (keyword, icon) in Keywords)
            if (lower.Contains(keyword, StringComparison.Ordinal))
                return icon;

        return fallback;
    }

    public static string MapApp(string? qbIconName) => Map(qbIconName, DefaultAppIcon);
    public static string MapTable(string? qbIconName) => Map(qbIconName, DefaultTableIcon);
}
