using Chronicle.Plugin.IMDb.Index;

namespace Chronicle.Plugin.IMDb;

/// <summary>
/// How IMDb's eleven title types map onto Chronicle media types (PLUGIN_IMDB.md §5.3). The
/// search for an item is restricted to the IMDb types for its media type, so a movie never
/// matches a same-named series and vice versa.
/// </summary>
internal static class ImdbMediaTypes
{
    public const string Movies = "movies", Tv = "tv", Anime = "anime", AnimeMovies = "anime_movies",
        MusicVideos = "music_videos", Game = "game", People = "people", FanEdits = "fanedits";

    /// <summary>Film-like types. "video" is in here, but a video whose genres include Music is a
    /// music video, not a movie (<see cref="Accepts"/> checks that).</summary>
    private static readonly string[] MovieTypes = ["movie", "tvMovie", "short", "tvShort", "video", "tvSpecial"];
    private static readonly string[] SeriesTypes = ["tvSeries", "tvMiniSeries", "tvPilot"];
    private static readonly string[] MusicVideoTypes = ["video"];
    private static readonly string[] GameTypes = ["videoGame"];

    private enum Family { None, Movie, Series, MusicVideo, Game, Any }

    private static Family FamilyOf(string? mediaTypeName) => mediaTypeName?.ToLowerInvariant() switch
    {
        null => Family.Any,
        Movies or "movie" or FanEdits or AnimeMovies => Family.Movie,
        Tv or Anime => Family.Series,
        var n when n.StartsWith("tv ", StringComparison.Ordinal) => Family.Series,
        MusicVideos => Family.MusicVideo,
        Game => Family.Game,
        _ => Family.None,
    };

    /// <summary>IMDb types to search for a Chronicle media type. Null = every non-episode type
    /// (a caller that didn't say); empty = this media type isn't one IMDb covers.</summary>
    public static IReadOnlyCollection<string>? SearchTypes(string? mediaTypeName) => FamilyOf(mediaTypeName) switch
    {
        Family.Movie      => MovieTypes,
        Family.Series     => SeriesTypes,
        Family.MusicVideo => MusicVideoTypes,
        Family.Game       => GameTypes,
        Family.Any        => null,
        _                 => [],
    };

    /// <summary>Whether a title can be the match for an item of <paramref name="mediaTypeName"/>.
    /// Episodes are never matched directly at the root level.</summary>
    public static bool Accepts(string? mediaTypeName, TitleRow title) => FamilyOf(mediaTypeName) switch
    {
        Family.Movie      => MovieTypes.Contains(title.Type) && !title.IsMusicVideo,
        Family.Series     => SeriesTypes.Contains(title.Type),
        Family.MusicVideo => title.IsMusicVideo,
        Family.Game       => GameTypes.Contains(title.Type),
        Family.Any        => title.Type != "tvEpisode",
        _                 => false,
    };

    /// <summary>The Chronicle media type a title belongs to, for a person's filmography.</summary>
    public static string ChronicleTypeOf(TitleRow title) =>
        title.IsMusicVideo ? MusicVideos
        : GameTypes.Contains(title.Type) ? Game
        : SeriesTypes.Contains(title.Type) || title.Type == "tvEpisode" ? Tv
        : Movies;

    public static bool IsSeries(TitleRow title) => SeriesTypes.Contains(title.Type);

    /// <summary>Whether a Chronicle media type is show/season/episode shaped.</summary>
    public static bool IsSeriesType(string? mediaTypeName) => FamilyOf(mediaTypeName) == Family.Series;
}
