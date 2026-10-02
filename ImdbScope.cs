using Chronicle.Plugins;

namespace Chronicle.Plugin.IMDb;

/// <summary>
/// The plugin's settings, shared by the provider and both tasks. The scope settings only change
/// what the next index build keeps; nothing is dropped from an index that already exists until
/// the next Sync IMDb Datasets run. Defaults keep everything.
/// </summary>
internal sealed record ImdbScope(
    bool IncludeAdult,
    IReadOnlySet<string>? TitleTypes,
    IReadOnlySet<string>? AkaRegions,
    IReadOnlySet<string>? AkaLanguages,
    bool EpisodeCredits,
    bool KeepDownloads)
{
    public const string KeyNotice         = "disk_space_notice";
    public const string KeyIncludeAdult   = "include_adult";
    public const string KeyTitleTypes     = "title_types";
    public const string KeyAkaRegions     = "aka_regions";
    public const string KeyAkaLanguages   = "aka_languages";
    public const string KeyEpisodeCredits = "episode_credits";
    public const string KeyKeepDownloads  = "keep_downloads";

    public static readonly ImdbScope Everything = new(true, null, null, null, true, false);

    /// <summary>True when the scope drops titles or credits, so people credited only on what was
    /// dropped can go too. Alternate-title filters don't count: they don't affect anyone's
    /// credits. With the default scope every person is kept, credited or not.</summary>
    public bool DropsTitlesOrCredits => !IncludeAdult || TitleTypes is not null || !EpisodeCredits;

    /// <summary>Stamped into the index so a scope change is noticed and triggers a rebuild.</summary>
    public string Fingerprint =>
        string.Join("|",
            IncludeAdult ? "adult" : "noadult",
            Join(TitleTypes), Join(AkaRegions), Join(AkaLanguages),
            EpisodeCredits ? "epcredits" : "noepcredits");

    public bool KeepsTitleType(string type) => TitleTypes is null || TitleTypes.Contains(type);

    public static ImdbScope FromSettings(IReadOnlyDictionary<string, string> settings) => new(
        Bool(settings, KeyIncludeAdult, true),
        Set(settings, KeyTitleTypes),
        Set(settings, KeyAkaRegions),
        Set(settings, KeyAkaLanguages),
        Bool(settings, KeyEpisodeCredits, true),
        Bool(settings, KeyKeepDownloads, false));

    public static string? DataDirectory(IReadOnlyDictionary<string, string> settings) =>
        settings.GetValueOrDefault(IPluginTask.DataDirectorySettingsKey);

    private static bool Bool(IReadOnlyDictionary<string, string> s, string key, bool fallback) =>
        s.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    // Empty means "all"; values are comma- or whitespace-separated.
    private static IReadOnlySet<string>? Set(IReadOnlyDictionary<string, string> s, string key)
    {
        if (!s.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        var items = raw.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? null : set;
    }

    private static string Join(IReadOnlySet<string>? set) =>
        set is null ? "*" : string.Join(",", set.Select(x => x.ToLowerInvariant()).Order(StringComparer.Ordinal));
}
