using System.Net.Http;
using Chronicle.Plugin.IMDb.Index;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.IMDb;

/// <summary>
/// Thrown when the local index hasn't been built yet. Deliberately an
/// <see cref="HttpRequestException"/> with no status code: Chronicle treats exactly that as
/// "provider unreachable right now" and leaves the item Pending (instead of NotFound or
/// Skipped), so nothing is written off before the first Sync IMDb Datasets run.
/// </summary>
public sealed class ImdbIndexUnavailableException()
    : HttpRequestException("The IMDb index hasn't been built yet. Run the plugin's \"Sync IMDb Datasets\" task first.");

/// <summary>
/// Thrown when a stored IMDb id is no longer in IMDb's datasets (IMDb merges and deletes titles
/// without redirects). Deliberately not <see cref="KeyNotFoundException"/>: Chronicle answers that
/// by re-searching and, when nothing matches, deleting the item's IMDb data and its shared
/// <c>imdb</c> external id, which TMDB and others also use. Any other exception marks the row
/// Failed (then Exhausted) and keeps everything, with this message pointing at Fix Match
/// (PLUGIN_IMDB.md §10.3).
/// </summary>
public sealed class ImdbIdMissingException(string externalId)
    : Exception($"imdb-missing: {externalId} is no longer in IMDb's datasets (IMDb may have merged or removed it). " +
                "The last IMDb data is kept; use Fix Match to point the item at the right IMDb title.");

/// <summary>
/// Chronicle metadata provider backed by IMDb's non-commercial datasets (PLUGIN_IMDB.md).
/// Every lookup is a read against the local index that <see cref="ImdbSyncDatasetsTask"/> builds,
/// so there are no network calls, API keys or rate limits here.
///
/// Ids: <c>imdb:tt…</c> for titles and episodes, <c>imdb:tt…/season:N</c> for seasons (IMDb has
/// no season entity), <c>imdb:nm…</c> for people. Seasons and episodes are found through the
/// parent show's id, the way TVMaze and TheTVDB find theirs; they're never searched by name.
/// </summary>
public sealed class ImdbMetadataProvider : IMetadataProvider
{
    /// <summary>Minimum score for a with-year match to skip the without-year search (shared cascade).</summary>
    private const int ExactMatchThreshold = 60;

    /// <summary>Candidates fetched per title query, the size of one TMDB search page.</summary>
    private const int QueryLimit = 20;

    private ImdbIndexStore? _store;

    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.imdb";
    public string Name     => "IMDb";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Capabilities ──────────────────────────────────────────────────────────

    private static readonly List<string> TitleFields =
        ["title", "year", "runtime_minutes", "genres", "cast", "crew", "rating"];

    // Lower priority than TMDB (10) by default, since IMDb has no overviews or artwork; the
    // user's Metadata Assignment order decides every field either way.
    private const int Priority = 20;

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new() { MediaTypeName = ImdbMediaTypes.Movies, DisplayName = "Movies", HierarchyLevels = 1,
                DefaultPriority = Priority, SupportedFields = [.. TitleFields] },
        new() { MediaTypeName = ImdbMediaTypes.Tv, DisplayName = "TV", HierarchyLevels = 3,
                HierarchyLabels = ["Show", "Season", "Episode"], DefaultPriority = Priority,
                SupportedFields = [.. TitleFields], LevelFields = SeriesLevelFields() },
        new() { MediaTypeName = ImdbMediaTypes.Anime, DisplayName = "Anime", HierarchyLevels = 3,
                HierarchyLabels = ["Show", "Season", "Episode"], DefaultPriority = Priority,
                SupportedFields = [.. TitleFields], LevelFields = SeriesLevelFields() },
        new() { MediaTypeName = ImdbMediaTypes.AnimeMovies, DisplayName = "Anime Movies", HierarchyLevels = 1,
                DefaultPriority = Priority, SupportedFields = [.. TitleFields] },
        // New types (PLUGIN_IMDB.md §5.3). Real types whether or not a given library has any.
        new() { MediaTypeName = ImdbMediaTypes.MusicVideos, DisplayName = "Music Videos", HierarchyLevels = 1,
                InteractionVerb = "watched", ProgressUnit = "minutes",
                DefaultPriority = Priority, SupportedFields = [.. TitleFields] },
        // Shared with the game plugins (IGDB, LaunchBox, RAWG, Steam): one games type in Chronicle.
        new() { MediaTypeName = ImdbMediaTypes.Game, DisplayName = "Video Games", HierarchyLevels = 1,
                InteractionVerb = "played", ProgressUnit = "percent",
                DefaultPriority = Priority, SupportedFields = [.. TitleFields] },
        // No DisplayName: IMDb contributes to people, it doesn't register the type (same as TMDB).
        new() { MediaTypeName = ImdbMediaTypes.People, HierarchyLevels = 1, InteractionVerb = "viewed",
                ProgressUnit = "percent", IsTrackable = false, DefaultPriority = Priority,
                SupportedFields = ["title", "tags", "extended_data"] },
    ];

    private static Dictionary<int, List<string>> SeriesLevelFields() => new()
    {
        [1] = ["title", "year"],
        [2] = ["title", "year", "runtime_minutes", "cast", "crew", "rating"],
    };

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["imdb:"];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = ImdbScope.KeyNotice,
                Label       = "Disk space",
                Type        = SettingType.Notice,
                Description = "This plugin downloads IMDb's datasets (about 2 GB) and builds a local index of " +
                              "about 10 GB in the plugin's data folder (measured October 2026 with everything kept). " +
                              "A sync takes around 10-15 minutes and needs about 12 GB free on top of the current " +
                              "index, which stays in use until the new one is finished. Nothing is matched until " +
                              "\"Sync IMDb Datasets\" has run once. The settings below make the index smaller; " +
                              "changes apply at the next sync. " + ImdbMapper.Attribution,
            },
            new SettingDefinition
            {
                Key          = ImdbScope.KeyIncludeAdult,
                Label        = "Include adult titles",
                Description  = "Index IMDb's adult titles (they're flagged as adult in Chronicle and can be hidden " +
                               "with a library filter). Turning this off drops about 417,000 titles and their credits.",
                Type         = SettingType.Boolean,
                DefaultValue = "true",
            },
            new SettingDefinition
            {
                Key          = ImdbScope.KeyTitleTypes,
                Label        = "Title types to index",
                Description  = "Leave empty to index everything. Otherwise a comma-separated list of IMDb title " +
                               "types: movie, tvMovie, short, tvShort, video, tvSpecial, tvSeries, tvMiniSeries, " +
                               "tvEpisode, tvPilot, videoGame. Leaving out tvEpisode (9.9 million rows) or short " +
                               "(1.2 million) saves the most.",
                Type         = SettingType.Text,
                DefaultValue = "",
            },
            new SettingDefinition
            {
                Key          = ImdbScope.KeyAkaRegions,
                Label        = "Alternate-title regions",
                Description  = "Leave empty to keep every region's titles. Otherwise a comma-separated list of " +
                               "region codes (e.g. US, GB, CA). Alternate titles are the largest single saving: " +
                               "59.5 million rows unrestricted. Original titles are always kept.",
                Type         = SettingType.Text,
                DefaultValue = "",
            },
            new SettingDefinition
            {
                Key          = ImdbScope.KeyAkaLanguages,
                Label        = "Alternate-title languages",
                Description  = "Leave empty to keep every language. Otherwise a comma-separated list of language " +
                               "codes (e.g. en, fr). Titles IMDb records without a language are kept.",
                Type         = SettingType.Text,
                DefaultValue = "",
            },
            new SettingDefinition
            {
                Key          = ImdbScope.KeyEpisodeCredits,
                Label        = "Episode credits",
                Description  = "Index cast and crew for individual episodes. Off keeps episode titles, numbers, " +
                               "runtimes and ratings but drops guest cast, which is most of IMDb's 102 million " +
                               "credit rows.",
                Type         = SettingType.Boolean,
                DefaultValue = "true",
            },
            new SettingDefinition
            {
                Key          = ImdbScope.KeyKeepDownloads,
                Label        = "Keep downloaded files",
                Description  = "Keep the ~2 GB of downloaded dataset files after a sync instead of deleting them. " +
                               "Only useful for rebuilding without downloading again.",
                Type         = SettingType.Boolean,
                DefaultValue = "false",
            },
        ],
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        var dataDir = ImdbScope.DataDirectory(settings);
        _store = dataDir is null ? null : new ImdbIndexStore(dataDir);
    }

    /// <summary>Test seam: point the provider at an index folder directly.</summary>
    internal void ConfigureForTesting(string dataDir) => _store = new ImdbIndexStore(dataDir);

    private ImdbIndexReader OpenIndex() =>
        _store?.OpenReader() ?? throw new ImdbIndexUnavailableException();

    // ── Search ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var mediaType = context.MediaTypeName?.ToLowerInvariant();

        // People: by id only, never by name (people-section design rule, same as TMDB).
        if (mediaType == ImdbMediaTypes.People)
            return Task.FromResult(SearchPerson(context));

        if (ImdbMediaTypes.SearchTypes(mediaType) is { Count: 0 })
            return Task.FromResult<IReadOnlyList<ScoredCandidate>>([]);

        using var index = OpenIndex();
        // Levels mean show/season/episode only for series types. A movie's level just says how
        // deep it sits in collections (2,000+ movies are level 1 inside one), and it's still
        // matched by title.
        IReadOnlyList<ScoredCandidate> result = !ImdbMediaTypes.IsSeriesType(mediaType) ? SearchTitle(index, context, mediaType)
            : context.HierarchyLevel switch
            {
                1 => SearchSeason(index, context),
                2 => SearchEpisode(index, context),
                _ => SearchTitle(index, context, mediaType),
            };
        return Task.FromResult(result);
    }

    private IReadOnlyList<ScoredCandidate> SearchPerson(MediaSearchContext context)
    {
        if (context.KnownExternalIds?.GetValueOrDefault(ImdbMapper.Source) is not { } raw
            || !ImdbIds.TryParse(raw, out var id, out _) || id.Kind != ImdbIdKind.Person)
            return [];

        using var index = OpenIndex();
        return index.GetName(id.Number) is { } name
            ? [new ScoredCandidate(ImdbMapper.MapPerson(index, name), 100, "cross-reference ID match")]
            : [];
    }

    /// <summary>
    /// The shared search cascade (PLUGIN_IMDB.md §5.2), step for step the same as TMDB's: known
    /// id first; then each alternate title with the year, stopping at the first exact-title hit;
    /// then each title again without the year. Only the candidate source differs (the local
    /// index instead of an HTTP search).
    /// </summary>
    private static IReadOnlyList<ScoredCandidate> SearchTitle(ImdbIndexReader index, MediaSearchContext context, string? mediaType)
    {
        // Stage 0: an IMDb id Chronicle already knows (usually from TMDB).
        if (context.KnownExternalIds?.GetValueOrDefault(ImdbMapper.Source) is { } known
            && ImdbIds.TryParse(known, out var knownId, out _) && knownId.Kind == ImdbIdKind.Title
            && index.GetTitle(knownId.Number) is { } knownTitle)
        {
            // An id that points at the wrong kind of title (a series on a movie item, say) leaves
            // the item unmatched for IMDb rather than searching: whatever a search picked would
            // replace that id in the imdb row other plugins (TVMaze, Simkl, Fanart.tv) trust, and
            // reset all of them. Which side is wrong is for the user to settle with Fix Match.
            // An id IMDb has dropped (not in the index) falls through to a normal search.
            return ImdbMediaTypes.Accepts(mediaType, knownTitle)
                ? [new ScoredCandidate(Candidate(knownTitle, index.GetRating(knownTitle.Id)?.Votes ?? 0), 100,
                    "cross-reference ID match")]
                : [];
        }

        var types = ImdbMediaTypes.SearchTypes(mediaType);
        var titles = (context.AltTitles is { Count: > 0 } alt ? alt : (IEnumerable<string>)[context.Name])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Stage 1a: with the year.
        var stage1a = new List<Ranked>();
        foreach (var raw in titles)
        {
            var title = TitleText.StripYearSuffix(raw, out var suffixYear);
            stage1a.AddRange(Candidates(index, context, mediaType, types, title, context.Year ?? suffixYear));
            if (stage1a.Any(c => c.C.Score >= ExactMatchThreshold))
                return Rank(stage1a);
        }

        // Stage 1b: without the year.
        var stage1b = new List<Ranked>();
        foreach (var raw in titles)
            stage1b.AddRange(Candidates(index, context, mediaType, types, TitleText.StripYearSuffix(raw, out _), null));

        return Rank([.. stage1a, .. stage1b]);
    }

    /// <summary>Scores every title the index returns for one query. A title can match through
    /// several spellings (primary, original, alternate); its best-scoring spelling counts, so an
    /// item named by its original or localised title still scores as exact.</summary>
    private static IEnumerable<Ranked> Candidates(ImdbIndexReader index, MediaSearchContext context,
        string? mediaType, IReadOnlyCollection<string>? types, string title, int? year)
    {
        var hits = index.Search(TitleText.Normalize(title), types, year, QueryLimit);
        return hits
            .Where(h => ImdbMediaTypes.Accepts(mediaType, h.Title))
            .GroupBy(h => h.Title.Id)
            .Select(g =>
            {
                var best = g.Select(h => (Hit: h, Score: ScoreCandidate(context, h.Raw, h.Title.StartYear),
                                          OwnTitle: h.Raw == h.Title.PrimaryTitle || h.Raw == h.Title.OriginalTitle))
                            .OrderByDescending(x => x.Score.Score).ThenByDescending(x => x.OwnTitle).First();
                return new Ranked(
                    new ScoredCandidate(Candidate(best.Hit.Title, best.Hit.Votes), best.Score.Score, best.Score.Reason),
                    best.OwnTitle, best.Hit.Votes);
            });
    }

    /// <summary>A scored candidate plus what breaks ties between equal scores.</summary>
    private sealed record Ranked(ScoredCandidate C, bool OwnTitle, int Votes);

    /// <summary>
    /// Score order; equal scores go to a title that matched by its own (primary or original) title
    /// over one that matched through a regional alternate title, then by votes (IMDb's equivalent
    /// of TMDB's popularity tiebreak). Top 10, one entry per title. Live example: with no year,
    /// "Love" also matches Amour through a regional title "Love", and Amour has more votes than
    /// Love (2015). The scores themselves are the shared method's, unchanged.
    /// </summary>
    private static IReadOnlyList<ScoredCandidate> Rank(IEnumerable<Ranked> candidates) =>
        candidates
            .OrderByDescending(c => c.C.Score)
            .ThenByDescending(c => c.OwnTitle)
            .ThenByDescending(c => c.Votes)
            .DistinctBy(c => c.C.Metadata.ExternalId)
            .Take(10)
            .Select(c => c.C)
            .ToList();

    /// <summary>
    /// TMDB's ScoreCandidate, unchanged (PLUGIN_IMDB.md §5.2): normalised title equal +60 or
    /// contains +30; year equal +20, ±1 +10, otherwise −10; PreciseName equal +15 or contains +5.
    /// <paramref name="candidateTitle"/> is the spelling that matched.
    /// </summary>
    internal static (int Score, string Reason) ScoreCandidate(MediaSearchContext ctx, string candidateTitle, int? candidateYear)
    {
        var score = 0;
        var reasons = new List<string>();

        var cn = TitleText.Normalize(candidateTitle);
        var rawQuery = ctx.AltTitles is { Count: > 0 } ? ctx.AltTitles[0] : ctx.Name;
        var qn = TitleText.Normalize(TitleText.StripYearSuffix(rawQuery, out _));

        if (string.Equals(cn, qn, StringComparison.Ordinal))
        {
            score += 60;
            reasons.Add("title exact");
        }
        else if (cn.Contains(qn, StringComparison.Ordinal) || qn.Contains(cn, StringComparison.Ordinal))
        {
            score += 30;
            reasons.Add("title contains");
        }

        if (ctx.Year.HasValue && candidateYear.HasValue)
        {
            var diff = Math.Abs(ctx.Year.Value - candidateYear.Value);
            if (diff == 0)      { score += 20; reasons.Add("year exact"); }
            else if (diff == 1) { score += 10; reasons.Add("year ±1"); }
            else                { score -= 10; reasons.Add("year mismatch"); }
        }

        if (!string.IsNullOrEmpty(ctx.PreciseName))
        {
            var pn = ctx.PreciseName.Trim();
            var ct = candidateTitle.Trim();
            if (string.Equals(pn, ct, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
                reasons.Add("precise name exact");
            }
            else if (ct.Contains(pn, StringComparison.OrdinalIgnoreCase) || pn.Contains(ct, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
                reasons.Add("precise name contains");
            }
        }

        return (score, reasons.Count > 0 ? string.Join(", ", reasons) : "no signals");
    }

    /// <summary>Lightweight search result; Chronicle fetches the full record by id for the
    /// winner (GetByIdAsync).</summary>
    private static MediaMetadata Candidate(TitleRow t, int votes) => new()
    {
        ExternalId = new ImdbId(ImdbIdKind.Title, t.Id).ToExternalId(),
        Source     = ImdbMapper.Source,
        Title      = t.PrimaryTitle,
        Year       = t.StartYear,
        RuntimeMinutes = t.Runtime,
        Genres     = [.. t.Genres],
        ExtendedData = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["titleFormat"] = t.Type, ["votes"] = votes, ["originalTitle"] = t.OriginalTitle ?? t.PrimaryTitle,
        }),
    };

    // ── Seasons & episodes ────────────────────────────────────────────────────

    /// <summary>Season: the parent show's IMDb id plus the season number. Needs the show matched first.</summary>
    private static IReadOnlyList<ScoredCandidate> SearchSeason(ImdbIndexReader index, MediaSearchContext context)
    {
        if (ParentShow(context) is not { } showId || context.ItemNumber is not { } season) return [];
        if (index.GetTitle(showId) is not { } show || !ImdbMediaTypes.IsSeries(show)) return [];
        var episodes = index.GetEpisodes(showId, season);
        return episodes.Count == 0
            ? []
            : [new ScoredCandidate(ImdbMapper.MapSeason(show, season, episodes), 100, "season number match")];
    }

    /// <summary>
    /// Episode: by show + season + episode number. When IMDb has no episode at that number, the
    /// item's title is compared against the show's unnumbered episodes (21% of IMDb's episodes
    /// have no number) with the shared scoring; unnumbered episodes are never assigned by
    /// position (PLUGIN_IMDB.md §10.1).
    /// </summary>
    private static IReadOnlyList<ScoredCandidate> SearchEpisode(ImdbIndexReader index, MediaSearchContext context)
    {
        if (ParentShow(context) is not { } showId) return [];
        var season = ParentSeason(context);

        if (season is not null && context.ItemNumber is { } number
            && index.FindEpisode(showId, season.Value, number) is { } episodeId
            && index.GetTitle(episodeId) is { } episode)
        {
            return [new ScoredCandidate(Candidate(episode, index.GetRating(episodeId)?.Votes ?? 0), 100,
                $"S{season:D2}E{number:D2} match")];
        }

        if (string.IsNullOrWhiteSpace(context.Name)) return [];
        var wanted = TitleText.Normalize(context.Name);
        return index.GetEpisodes(showId, unplacedOnly: true)
            .Where(e => TitleText.Normalize(e.Title) == wanted)
            .Select(e =>
            {
                var (score, reason) = ScoreCandidate(context with { AltTitles = null }, e.Title, e.Year);
                var t = index.GetTitle(e.Id)!;
                return new ScoredCandidate(Candidate(t, e.Votes ?? 0), score, reason + ", unnumbered episode title match");
            })
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
    }

    /// <summary>The show's title number from the parent's stored IMDb id: a season's parent is the
    /// show ("tt0903747"), an episode's parent is the season ("tt0903747/season:5").</summary>
    private static int? ParentShow(MediaSearchContext context) =>
        ImdbIds.TitleNumber(context.KnownExternalIds?.GetValueOrDefault("parent_" + ImdbMapper.Source));

    private static int? ParentSeason(MediaSearchContext context) =>
        ImdbIds.TryParse(context.KnownExternalIds?.GetValueOrDefault("parent_" + ImdbMapper.Source), out var id, out _)
        && id.Kind == ImdbIdKind.Season ? id.Season : null;

    // ── Get by id ─────────────────────────────────────────────────────────────

    public Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var id = ImdbIds.Parse(externalId);
        using var index = OpenIndex();

        MediaMetadata result = id.Kind switch
        {
            ImdbIdKind.Person => index.GetName(id.Number) is { } n
                ? ImdbMapper.MapPerson(index, n)
                : throw new ImdbIdMissingException(id.ToExternalId()),
            // A season that isn't there is a numbering difference, not a deleted title: let
            // Chronicle clear the id and look again (KeyNotFoundException).
            ImdbIdKind.Season => index.GetTitle(id.Number) is { } show && ImdbMediaTypes.IsSeries(show)
                                 && index.GetEpisodes(id.Number, id.Season) is { Count: > 0 } eps
                ? ImdbMapper.MapSeason(show, id.Season!.Value, eps)
                : throw new KeyNotFoundException($"IMDb has no episodes numbered in season {id.Season} of {ImdbIds.FormatTitle(id.Number)}."),
            _ => index.GetTitle(id.Number) is { } t
                ? (index.GetEpisodeRef(t.Id) is { } ep ? ImdbMapper.MapEpisode(index, t, ep) : ImdbMapper.MapTitle(index, t))
                : throw new ImdbIdMissingException(id.ToExternalId()),
        };
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ProviderEpisodeSummary>> GetEpisodeListAsync(
        string showExternalId, int seasonNumber, CancellationToken ct = default)
    {
        // An empty list (never an exception) means "nothing from this provider": callers move on.
        if (!ImdbIds.TryParse(showExternalId, out var id, out _) || id.Kind != ImdbIdKind.Title
            || _store?.OpenReader() is not { } index)
            return Task.FromResult<IReadOnlyList<ProviderEpisodeSummary>>([]);

        using (index)
        {
            IReadOnlyList<ProviderEpisodeSummary> list = index.GetEpisodes(id.Number, seasonNumber)
                .Where(e => e.Episode is not null)
                .Select(e => new ProviderEpisodeSummary(e.Episode!.Value, e.Title))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<ProviderPersonCredit>> GetPersonCreditsAsync(string personExternalId, CancellationToken ct = default)
    {
        if (!ImdbIds.TryParse(personExternalId, out var id, out _) || id.Kind != ImdbIdKind.Person)
            return Task.FromResult<IReadOnlyList<ProviderPersonCredit>>([]);

        using var index = OpenIndex();
        return Task.FromResult(ImdbMapper.MapPersonCredits(index, index.GetPersonCredits(id.Number)));
    }

    /// <summary>IMDb's datasets have no images.</summary>
    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default) =>
        throw new NotSupportedException("IMDb's datasets contain no images.");

    /// <summary>Healthy once an index exists and matches this version's schema (§10.4).</summary>
    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var reader = _store?.OpenReader();
            return Task.FromResult(reader is not null);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Task.FromResult(false);
        }
    }
}
