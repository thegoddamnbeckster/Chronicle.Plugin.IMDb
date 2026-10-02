using System.Globalization;
using System.Text.Json;
using Chronicle.Plugin.IMDb.Index;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.IMDb;

/// <summary>
/// Turns index rows into <see cref="MediaMetadata"/> (PLUGIN_IMDB.md §3, §6).
///
/// Everything IMDb has for a title is returned. Data Chronicle already has a field for goes in
/// the matching property (title, year, runtime, genres, cast, crew, rating); the rest goes in
/// <see cref="MediaMetadata.ExtendedData"/> under the key names the plugin's manifest declares in
/// <c>metadata_fields</c> (<c>originalTitle</c>, <c>alternateTitles</c>, <c>endYear</c>,
/// <c>titleFormat</c>, <c>isAdult</c>, …), so it's stored in IMDb's partition now and becomes a
/// precedence-governed field once Chronicle's field registry reads those declarations. A raw
/// copy of the principals rows is kept alongside, so no detail is lost in the mapping.
/// </summary>
internal static class ImdbMapper
{
    public const string Source = "imdb";

    /// <summary>IMDb's required credit line for its datasets (PLUGIN_IMDB.md §1).</summary>
    public const string Attribution =
        "Information courtesy of IMDb (https://www.imdb.com). Used with permission.";

    private static readonly HashSet<string> CastCategories =
        new(StringComparer.Ordinal) { "actor", "actress", "self", "archive_footage", "archive_sound" };

    private static readonly TextInfo Text = CultureInfo.InvariantCulture.TextInfo;

    // ── Titles (movies, shows, specials, music videos, games) ─────────────────

    public static MediaMetadata MapTitle(ImdbIndexReader index, TitleRow t)
    {
        var rating = index.GetRating(t.Id);
        var akas = index.GetAkas(t.Id);
        var principals = index.GetPrincipals(t.Id);
        var crew = index.GetCrew(t.Id);
        var ext = BaseExtended(index, t, rating, akas, principals);

        var isSeries = ImdbMediaTypes.IsSeries(t);
        if (isSeries)
        {
            // A series' title.crew is every director and writer of every episode (222 people
            // for Law & Order: SVU). Credited on the show, each becomes a Chronicle person record
            // the moment the show is matched, duplicating what each episode credits itself, and
            // that was most of the first full run's time. The show is credited with its own
            // principals only; the full list is kept here, as plain names and ids, so nothing is
            // dropped, and every person's IMDb filmography still lists all of it.
            ext["seriesCrew"] = crew.Select(c => new Dictionary<string, object?>
            {
                ["nconst"] = ImdbIds.FormatName(c.PersonId), ["name"] = c.Name,
                ["job"] = c.Role == ImdbSchema.RoleDirector ? "Director" : "Writer",
            }).ToList();

            var episodes = index.GetEpisodes(t.Id);
            ext["episodeCount"] = episodes.Count;
            ext["seasonCount"] = episodes.Where(e => e.Season is not null).Select(e => e.Season).Distinct().Count();
            ext["unplacedEpisodes"] = episodes
                .Where(e => e.Season is null || e.Episode is null)
                .Select(e => new Dictionary<string, object?>
                {
                    ["id"] = ImdbIds.FormatTitle(e.Id), ["title"] = e.Title, ["year"] = e.Year,
                    ["season"] = e.Season, ["episode"] = e.Episode,
                })
                .ToList();
        }

        if (t.IsMusicVideo)
        {
            // Music videos credit the performing artist as "self" (sometimes actor/actress).
            var performers = principals.Where(p => p.Category == "self").ToList();
            if (performers.Count == 0) performers = principals.Where(p => p.Category is "actor" or "actress").ToList();
            ext["artist"] = performers.Where(p => p.Name is not null)
                .Select(p => new Dictionary<string, object?> { ["name"] = p.Name, ["externalPersonId"] = PersonId(p.PersonId) })
                .ToList();
        }

        return new MediaMetadata
        {
            ExternalId     = new ImdbId(ImdbIdKind.Title, t.Id).ToExternalId(),
            Source         = Source,
            Title          = t.PrimaryTitle,
            Year           = t.StartYear,
            RuntimeMinutes = t.Runtime,
            Genres         = [.. t.Genres],
            Rating         = rating?.Rating,
            Cast           = MapCast(principals),
            Crew           = MapCrew(principals, isSeries ? [] : crew),
            // Only the original title, not every alternate title: Chronicle feeds stored
            // alternate names to every provider's search as extra queries, and a title with 70
            // regional names would mean up to 140 extra searches against rate-limited providers.
            // The full list is in alternateTitles.
            AlternateNames = t.OriginalTitle is { } o ? [o] : [],
            ExtendedData   = JsonSerializer.SerializeToElement(ext),
        };
    }

    // ── Seasons (computed: IMDb has no season entity) ─────────────────────────

    public static MediaMetadata MapSeason(TitleRow show, int season, IReadOnlyList<EpisodeRow> episodes)
    {
        var years = episodes.Where(e => e.Year is not null).Select(e => e.Year!.Value).ToList();
        var rated = episodes.Where(e => e.Rating is not null && e.Votes is > 0).ToList();
        var votes = rated.Sum(e => (long)e.Votes!.Value);

        var ext = new Dictionary<string, object?>
        {
            // The show's id, not under "ids": a season has no IMDb id of its own, and anything
            // reading ids.imdb would otherwise attach the show's id to the season.
            ["show"]         = ImdbIds.FormatTitle(show.Id),
            ["seasonNumber"] = season,
            ["episodeCount"] = episodes.Count,
            ["endYear"]      = years.Count > 0 ? years.Max() : null,
            ["attribution"]  = Attribution,
        };
        if (votes > 0)
        {
            // Chronicle's own calculation, not an IMDb number: IMDb doesn't rate seasons. Kept
            // out of MediaMetadata.Rating so it can never pass for one (variant "episodes-avg").
            var mean = rated.Sum(e => e.Rating!.Value * e.Votes!.Value) / votes;
            ext["ratings"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["key"] = Source, ["variant"] = "episodes-avg", ["value"] = Math.Round(mean, 2),
                    ["scale"] = 10, ["votes"] = votes, ["ratedEpisodes"] = rated.Count,
                },
            };
        }

        return new MediaMetadata
        {
            ExternalId = new ImdbId(ImdbIdKind.Season, show.Id, season).ToExternalId(),
            Source     = Source,
            Title      = $"Season {season}",
            Year       = years.Count > 0 ? years.Min() : null,
            ExtendedData = JsonSerializer.SerializeToElement(ext),
        };
    }

    // ── Episodes ──────────────────────────────────────────────────────────────

    public static MediaMetadata MapEpisode(ImdbIndexReader index, TitleRow t, EpisodeRef episode)
    {
        var meta = MapTitle(index, t);
        var ext = JsonSerializer.Deserialize<Dictionary<string, object?>>(meta.ExtendedData!.Value.GetRawText())!;
        ext["show"] = ImdbIds.FormatTitle(episode.Parent);
        ext["seasonNumber"] = episode.Season;
        ext["episodeNumber"] = episode.Episode;
        meta.ExtendedData = JsonSerializer.SerializeToElement(ext);
        return meta;
    }

    // ── People ────────────────────────────────────────────────────────────────

    public static MediaMetadata MapPerson(ImdbIndexReader index, NameRow n)
    {
        var knownFor = n.KnownFor
            .Select(id => index.GetTitle(id) is { } kt
                ? new Dictionary<string, object?>
                  {
                      ["id"] = ImdbIds.FormatTitle(id), ["title"] = kt.PrimaryTitle, ["year"] = kt.StartYear,
                      ["mediaType"] = ImdbMediaTypes.ChronicleTypeOf(kt),
                  }
                : new Dictionary<string, object?> { ["id"] = ImdbIds.FormatTitle(id) })
            .ToList();
        var professions = n.Professions.Select(Pretty).ToList();

        return new MediaMetadata
        {
            ExternalId = new ImdbId(ImdbIdKind.Person, n.Id).ToExternalId(),
            Source     = Source,
            Title      = n.Name,
            Tags       = professions,
            // Year-only dates are deliberately not sent as birthDate/deathDate: Chronicle parses
            // those as full dates, so "1964" would either be rejected or, if IMDb outranked TMDB
            // or Wikipedia for that field, push out a real date. They're kept as years here until
            // Chronicle stores partial-date precision (field registry design).
            ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["ids"]         = new Dictionary<string, object?> { [Source] = ImdbIds.FormatName(n.Id) },
                ["birthYear"]   = n.BirthYear,
                ["deathYear"]   = n.DeathYear,
                ["professions"] = professions,
                ["knownFor"]    = knownFor,
                ["attribution"] = Attribution,
            }),
        };
    }

    /// <summary>A person's filmography in the shape Chronicle stores (PersonFullCreditsService).
    /// Episode credits are listed once per show, the way TMDB lists them. ExternalId is the bare
    /// <c>tt…</c>, which is how media_external_ids holds IMDb ids, so titles already in the
    /// library link up.</summary>
    public static IReadOnlyList<ProviderPersonCredit> MapPersonCredits(ImdbIndexReader index, IReadOnlyList<PersonCreditRow> rows)
    {
        var parents = new Dictionary<int, TitleRow?>();
        var result = new List<ProviderPersonCredit>();
        var seen = new HashSet<(string, string)>();

        foreach (var row in rows)
        {
            var title = row.Title;
            if (row.ParentId is { } parentId)
            {
                if (!parents.TryGetValue(parentId, out var parent))
                    parents[parentId] = parent = index.GetTitle(parentId);
                if (parent is null) continue;
                title = parent;
            }

            var role = CreditRole(row.Role, row.Job);
            var id = ImdbIds.FormatTitle(title.Id);
            if (!seen.Add((id, role))) continue;
            result.Add(new ProviderPersonCredit(
                Source, id, ImdbMediaTypes.ChronicleTypeOf(title), title.PrimaryTitle, title.StartYear,
                PosterUrl: null, role, Characters(row.Characters) is { Count: > 0 } c ? string.Join(" / ", c) : null));
        }
        return result;
    }

    // ── Shared pieces ─────────────────────────────────────────────────────────

    private static Dictionary<string, object?> BaseExtended(ImdbIndexReader index, TitleRow t, RatingRow? rating,
        IReadOnlyList<AkaRow> akas, IReadOnlyList<PrincipalRow> principals)
    {
        var ext = new Dictionary<string, object?>
        {
            ["ids"]             = new Dictionary<string, object?> { [Source] = ImdbIds.FormatTitle(t.Id) },
            ["originalTitle"]   = t.OriginalTitle ?? t.PrimaryTitle,
            ["alternateTitles"] = akas.Select(a => new Dictionary<string, object?>
            {
                ["title"] = a.Title, ["region"] = a.Region, ["language"] = a.Language,
                ["types"] = SplitMulti(a.Types), ["attributes"] = SplitMulti(a.Attributes),
                ["isOriginal"] = a.IsOriginal, ["ordering"] = a.Ordering,
            }).ToList(),
            ["endYear"]     = t.EndYear,
            ["titleFormat"] = t.Type,
            ["isAdult"]     = t.IsAdult,
            ["principals"]  = principals.Select(p => new Dictionary<string, object?>
            {
                ["ordering"] = p.Ordering, ["nconst"] = ImdbIds.FormatName(p.PersonId), ["name"] = p.Name,
                ["category"] = p.Category, ["job"] = p.Job, ["characters"] = Characters(p.Characters),
            }).ToList(),
            ["attribution"] = Attribution,
        };

        if (rating is not null)
        {
            ext["votes"] = rating.Votes;
            ext["ratings"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["key"] = Source, ["value"] = rating.Rating, ["scale"] = 10, ["votes"] = rating.Votes,
                    ["url"] = $"https://www.imdb.com/title/{ImdbIds.FormatTitle(t.Id)}/",
                },
            };
            ext["ratingFetchedAt"] = index.MetaValue("ratings_updated_at") ?? index.MetaValue("built_at");
        }
        return ext;
    }

    private static List<CastMember> MapCast(IReadOnlyList<PrincipalRow> principals) =>
        principals
            .Where(p => CastCategories.Contains(p.Category) && p.Name is not null)
            .Select(p => new CastMember(
                p.Name!,
                // One string while Chronicle's credits hold a single character per row (multiple
                // characters per credit is a pending core change); the separate names are in
                // the raw principals copy.
                Characters(p.Characters) is { Count: > 0 } c ? string.Join(" / ", c) : null,
                PersonId(p.PersonId)))
            .ToList();

    private static List<CrewMember> MapCrew(IReadOnlyList<PrincipalRow> principals, IReadOnlyList<CrewRow> crew)
    {
        var result = new List<CrewMember>();
        var credited = new HashSet<(int, string)>();

        foreach (var p in principals)
        {
            if (CastCategories.Contains(p.Category) || p.Name is null) continue;
            result.Add(new CrewMember(p.Name, CreditRole(p.Category, p.Job), PersonId(p.PersonId)));
            credited.Add((p.PersonId, p.Category));
        }

        // title.crew has the complete director/writer lists; principals only the top-billed few.
        foreach (var c in crew)
        {
            var category = c.Role == ImdbSchema.RoleDirector ? "director" : "writer";
            if (c.Name is null || !credited.Add((c.PersonId, category))) continue;
            result.Add(new CrewMember(c.Name, Pretty(category), PersonId(c.PersonId)));
        }
        return result;
    }

    /// <summary>Role label for a credit: "Actor" for acting (TMDB's label), else IMDb's job when
    /// it has one ("executive producer" → "Executive Producer"), else the category.</summary>
    private static string CreditRole(string category, string? job) => category switch
    {
        "actor" or "actress" => "Actor",
        _ when !string.IsNullOrWhiteSpace(job) && category is not ("self" or "archive_footage" or "archive_sound") => Pretty(job),
        _ => Pretty(category),
    };

    /// <summary>The "{source}:{id}" form PersonResolutionService keys people on.</summary>
    private static string PersonId(int nconst) => $"{Source}:{ImdbIds.FormatName(nconst)}";

    private static string Pretty(string raw)
    {
        var words = raw.Replace('_', ' ').Trim().ToLowerInvariant();
        var titled = Text.ToTitleCase(words);
        // Keep short joining words lowercase ("Director of Photography").
        return string.Join(' ', titled.Split(' ').Select((w, i) =>
            i > 0 && w is "Of" or "And" or "The" or "For" or "By" or "To" or "A" or "In" ? w.ToLowerInvariant() : w));
    }

    private static IReadOnlyList<string> Characters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return [json]; }
    }

    // title.akas separates multiple types/attributes with the 0x02 control character.
    private static List<string> SplitMulti(string? value) =>
        value is null ? [] : [.. value.Split('\u0002', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
