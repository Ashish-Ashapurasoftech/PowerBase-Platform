using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerBase.Application.Apps.RoleUi;

/// <summary>Per-table switches of a role's "User interface" settings. Every flag hides something; false = shown.</summary>
public record RoleUiTableSettings(
    Guid TablePublicId,
    bool HideInTableBar = false,
    bool HideAdd = false,
    bool HideImportExport = false,
    bool HideSaveAsSpreadsheet = false,
    bool HideMultiRecord = false)
{
    [JsonIgnore]
    public bool HidesAnything => HideInTableBar || HideAdd || HideImportExport || HideSaveAsSpreadsheet || HideMultiRecord;
}

/// <summary>
/// A role's "User interface" settings (Roles &gt; User interface). UI only: these hide tables, buttons, menus and
/// Personal-settings sections in the client; they never change what the role may read or write (Data Permissions).
/// Stored as JSON in meta.AppRole.UiSettingsJson.
/// </summary>
public record RoleUiSettings(
    IReadOnlyList<RoleUiTableSettings> Tables,
    // App options
    bool HidePersonalSettingsLink = false,
    bool HideAppAndTableSettings = false,
    bool HideToolsMenu = false,
    bool HideUsers = false,
    bool HideSearch = false,
    // Personal settings sections
    bool HideAppearance = false,
    bool HideFontConfiguration = false,
    bool HideGridDensity = false,
    bool HideLayoutStructure = false)
{
    public static readonly RoleUiSettings Empty = new([]);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Parses stored JSON; null / blank / unreadable JSON = nothing hidden.</summary>
    public static RoleUiSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            var parsed = JsonSerializer.Deserialize<RoleUiSettings>(json, JsonOptions);
            return parsed is null ? Empty : parsed with { Tables = parsed.Tables ?? [] };
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Drops rows that hide nothing and duplicate tables; null when nothing at all is hidden.</summary>
    public static string? Serialize(RoleUiSettings settings)
    {
        var normalized = Normalize(settings);
        return normalized.HidesAnything ? JsonSerializer.Serialize(normalized, JsonOptions) : null;
    }

    [JsonIgnore]
    public bool HidesAnything =>
        (Tables?.Count ?? 0) > 0
        || HidePersonalSettingsLink || HideAppAndTableSettings || HideToolsMenu || HideUsers || HideSearch
        || HideAppearance || HideFontConfiguration || HideGridDensity || HideLayoutStructure;

    public static RoleUiSettings Normalize(RoleUiSettings settings) => settings with
    {
        Tables = (settings.Tables ?? [])
            .Where(t => t.TablePublicId != Guid.Empty && t.HidesAnything)
            .GroupBy(t => t.TablePublicId)
            .Select(g => g.Last())
            .ToList()
    };

    /// <summary>
    /// Combines the settings of every role a user has in the app. Something is hidden only when EVERY role hides
    /// it — a role that shows it wins, the same "most access wins" rule the Data Permissions use for several roles.
    /// </summary>
    public static RoleUiSettings Merge(IReadOnlyList<RoleUiSettings> roles)
    {
        if (roles.Count == 0) return Empty;
        if (roles.Count == 1) return Normalize(roles[0]);

        bool All(Func<RoleUiSettings, bool> pick) => roles.All(pick);

        var maps = roles.Select(r => (r.Tables ?? []).GroupBy(t => t.TablePublicId).ToDictionary(g => g.Key, g => g.Last())).ToList();
        var tableIds = maps.SelectMany(m => m.Keys).Distinct();
        var tables = new List<RoleUiTableSettings>();
        foreach (var id in tableIds)
        {
            bool AllT(Func<RoleUiTableSettings, bool> pick) => maps.All(m => m.TryGetValue(id, out var t) && pick(t));
            var merged = new RoleUiTableSettings(id,
                AllT(t => t.HideInTableBar), AllT(t => t.HideAdd), AllT(t => t.HideImportExport),
                AllT(t => t.HideSaveAsSpreadsheet), AllT(t => t.HideMultiRecord));
            if (merged.HidesAnything) tables.Add(merged);
        }

        return new RoleUiSettings(tables,
            All(r => r.HidePersonalSettingsLink), All(r => r.HideAppAndTableSettings), All(r => r.HideToolsMenu),
            All(r => r.HideUsers), All(r => r.HideSearch),
            All(r => r.HideAppearance), All(r => r.HideFontConfiguration), All(r => r.HideGridDensity),
            All(r => r.HideLayoutStructure));
    }
}
