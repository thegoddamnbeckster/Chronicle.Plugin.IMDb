using System.Text.RegularExpressions;

namespace Chronicle.Plugin.IMDb;

/// <summary>What an IMDb id or link points at.</summary>
internal enum ImdbIdKind { Title, Season, Person }

/// <summary>A parsed IMDb id: a title (<c>tt</c>), a season of a show, or a person (<c>nm</c>).</summary>
internal readonly record struct ImdbId(ImdbIdKind Kind, int Number, int? Season = null)
{
    /// <summary>The id in the form Chronicle stores for this plugin: <c>imdb:tt0133093</c>,
    /// <c>imdb:tt0903747/season:5</c>, <c>imdb:nm0000206</c>.</summary>
    public string ToExternalId() => Kind switch
    {
        ImdbIdKind.Season => $"imdb:{ImdbIds.FormatTitle(Number)}/season:{Season}",
        ImdbIdKind.Person => $"imdb:{ImdbIds.FormatName(Number)}",
        _                 => $"imdb:{ImdbIds.FormatTitle(Number)}",
    };
}

/// <summary>
/// Parsing and formatting for IMDb ids.
///
/// Ids are stored the way Chronicle already stores IMDb ids from every other plugin: with an
/// <c>imdb:</c> prefix, which MetadataEnrichmentService strips into the shared <c>imdb</c> row of
/// media_external_ids (so TMDB's cross-reference <c>tt0133093</c> and this plugin's own match are
/// the same row). Seasons have no IMDb entity, so they're addressed as the show plus a number.
///
/// Fix Match accepts any of the link shapes in PLUGIN_IMDB.md §10.5; everything after the id
/// (sub-pages, query strings, language prefixes) is ignored.
/// </summary>
internal static partial class ImdbIds
{
    public static string FormatTitle(int n) => $"tt{n:D7}";
    public static string FormatName(int n)  => $"nm{n:D7}";

    [GeneratedRegex(@"^(?:imdb:)?(tt|nm)(\d{1,10})(?:/season:(\d{1,4}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareIdRe();

    [GeneratedRegex(@"/(title|name)/(tt|nm)(\d{1,10})(?=[/?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlIdRe();

    [GeneratedRegex(@"/(list|user|company|event|search|find|keyword|interest|chart|news|poll|gallery)(?=[/?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex RejectedUrlRe();

    /// <summary>
    /// Parses an id or an imdb.com link. Throws <see cref="ArgumentException"/> with a message the
    /// user can act on when the input isn't something that identifies one title or one person.
    /// </summary>
    public static ImdbId Parse(string input)
    {
        if (TryParse(input, out var id, out var error))
            return id;
        throw new ArgumentException(error);
    }

    public static bool TryParse(string? input, out ImdbId id, out string error)
    {
        id = default;
        error = string.Empty;
        var s = input?.Trim() ?? string.Empty;
        if (s.Length == 0)
        {
            error = "Enter an IMDb link or id, e.g. https://www.imdb.com/title/tt0133093/ or tt0133093.";
            return false;
        }

        if (s.Contains("://", StringComparison.Ordinal) || s.Contains("imdb.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(s.Contains("://", StringComparison.Ordinal) ? s : "https://" + s, UriKind.Absolute, out var uri)
                || !(uri.Host.Equals("imdb.com", StringComparison.OrdinalIgnoreCase)
                     || uri.Host.EndsWith(".imdb.com", StringComparison.OrdinalIgnoreCase)))
            {
                error = $"'{s}' is not an imdb.com link.";
                return false;
            }

            var m = UrlIdRe().Match(uri.AbsolutePath);
            if (m.Success && IsConsistent(m.Groups[1].Value, m.Groups[2].Value)
                && int.TryParse(m.Groups[3].Value, out var n) && n > 0)
            {
                id = new ImdbId(IsTitle(m.Groups[2].Value) ? ImdbIdKind.Title : ImdbIdKind.Person, n);
                return true;
            }

            error = RejectedUrlRe().IsMatch(uri.AbsolutePath)
                ? "That IMDb link is a list, user, company, event or search page, which doesn't identify a single title or person. Open the title's (or person's) own page and paste that link."
                : $"Couldn't find a title (tt…) or person (nm…) id in '{s}'.";
            return false;
        }

        var b = BareIdRe().Match(s);
        if (b.Success && int.TryParse(b.Groups[2].Value, out var num) && num > 0)
        {
            var title = IsTitle(b.Groups[1].Value);
            if (b.Groups[3].Success)
            {
                if (!title)
                {
                    error = $"'{s}' combines a person id with a season.";
                    return false;
                }
                id = new ImdbId(ImdbIdKind.Season, num, int.Parse(b.Groups[3].Value));
                return true;
            }
            id = new ImdbId(title ? ImdbIdKind.Title : ImdbIdKind.Person, num);
            return true;
        }

        error = $"'{s}' is not an IMDb id. Use a link such as https://www.imdb.com/title/tt0133093/ or an id such as tt0133093 or nm0000206.";
        return false;
    }

    /// <summary>Numeric part of a stored or cross-referenced title id ("tt0133093", "imdb:tt0133093",
    /// "tt0903747/season:5"), or null when it isn't one.</summary>
    public static int? TitleNumber(string? raw) =>
        TryParse(raw, out var id, out _) && id.Kind is ImdbIdKind.Title or ImdbIdKind.Season ? id.Number : null;

    private static bool IsTitle(string prefix) => prefix.Equals("tt", StringComparison.OrdinalIgnoreCase);

    // "/title/nm…" or "/name/tt…" is a malformed link, not something to guess at.
    private static bool IsConsistent(string segment, string prefix) =>
        segment.Equals("title", StringComparison.OrdinalIgnoreCase) == IsTitle(prefix);
}
