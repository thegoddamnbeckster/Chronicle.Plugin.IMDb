using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chronicle.Plugin.IMDb.Index;

internal sealed record TitleRow(
    int Id, string Type, string PrimaryTitle, string? OriginalTitle, bool IsAdult,
    int? StartYear, int? EndYear, int? Runtime, IReadOnlyList<string> Genres)
{
    public bool IsMusicVideo => Type == "video" && Genres.Contains("Music");
}

internal sealed record RatingRow(double Rating, int Votes);

internal sealed record AkaRow(int Ordering, string Title, string? Region, string? Language, string? Types,
    string? Attributes, bool IsOriginal);

/// <summary>One title.principals row with the person's name. <c>Characters</c> is IMDb's JSON
/// array text (<c>["Neo"]</c>).</summary>
internal sealed record PrincipalRow(int Ordering, int PersonId, string? Name, string Category, string? Job, string? Characters);

/// <summary>One director or writer from title.crew.</summary>
internal sealed record CrewRow(int PersonId, string? Name, int Role, int Ordering);

internal sealed record EpisodeRef(int Id, int Parent, int? Season, int? Episode);

internal sealed record EpisodeRow(int Id, int? Season, int? Episode, string Title, int? Year, int? Runtime,
    double? Rating, int? Votes);

internal sealed record NameRow(int Id, string Name, int? BirthYear, int? DeathYear,
    IReadOnlyList<string> Professions, IReadOnlyList<int> KnownFor);

/// <summary>A search match: the spelling that matched (<c>Raw</c>) and the title it belongs to.</summary>
internal sealed record SearchHit(string Raw, TitleRow Title, int Votes);

/// <summary>A person's credit on one title, with the parent show when the title is an episode.</summary>
internal sealed record PersonCreditRow(TitleRow Title, int? ParentId, string Role, string? Job, string? Characters);

/// <summary>
/// Read-only queries against one index file. Opened per provider call and disposed straight
/// after (no pooling), so a finished sync's new index is picked up on the very next lookup and the
/// old file can be deleted (see <see cref="ImdbIndexStore"/>).
/// </summary>
internal sealed class ImdbIndexReader : IDisposable
{
    /// <summary>Upper bound on full-text matches examined per query. Only reached by very short or
    /// very common phrases ("the"), where exact-title lookups still find the right title.</summary>
    private const int FtsScanLimit = 5000;

    private const string TitleColumns =
        "t.id, tt.name, t.primary_title, t.original_title, t.is_adult, t.start_year, t.end_year, t.runtime, t.genres";

    private readonly SqliteConnection _db;
    private Dictionary<string, string>? _meta;

    private ImdbIndexReader(SqliteConnection db) => _db = db;

    public static ImdbIndexReader Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 60,
        }.ToString());
        db.Open();
        return new ImdbIndexReader(db);
    }

    public IReadOnlyDictionary<string, string> Meta => _meta ??= Query(
        "SELECT key, value FROM meta", [], r => (r.GetString(0), r.GetString(1)))
        .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

    public int SchemaVersion =>
        Meta.TryGetValue("schema_version", out var v) && int.TryParse(v, out var n) ? n : 0;

    // ── Titles ────────────────────────────────────────────────────────────────

    public TitleRow? GetTitle(int id) => Query(
        $"SELECT {TitleColumns} FROM titles t JOIN title_types tt ON tt.id = t.type WHERE t.id = $id",
        [("$id", id)], ReadTitle).FirstOrDefault();

    public RatingRow? GetRating(int id) => Query(
        "SELECT rating, votes FROM ratings WHERE id = $id", [("$id", id)],
        r => new RatingRow(r.GetDouble(0), r.GetInt32(1))).FirstOrDefault();

    public IReadOnlyList<AkaRow> GetAkas(int id) => Query(
        "SELECT ordering, title, region, language, types, attributes, is_original FROM akas WHERE title_id = $id ORDER BY ordering",
        [("$id", id)],
        r => new AkaRow(r.GetInt32(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), r.GetInt32(6) == 1));

    public IReadOnlyList<PrincipalRow> GetPrincipals(int titleId) => Query(
        """
        SELECT p.ordering, p.person_id, n.name, c.name, p.job, p.characters
        FROM principals p JOIN categories c ON c.id = p.category LEFT JOIN names n ON n.id = p.person_id
        WHERE p.title_id = $id ORDER BY p.ordering
        """,
        [("$id", titleId)],
        r => new PrincipalRow(r.GetInt32(0), r.GetInt32(1), Str(r, 2), r.GetString(3), Str(r, 4), Str(r, 5)));

    public IReadOnlyList<CrewRow> GetCrew(int titleId) => Query(
        """
        SELECT c.person_id, n.name, c.role, c.ordering
        FROM crew c LEFT JOIN names n ON n.id = c.person_id
        WHERE c.title_id = $id ORDER BY c.role, c.ordering
        """,
        [("$id", titleId)],
        r => new CrewRow(r.GetInt32(0), Str(r, 1), r.GetInt32(2), r.GetInt32(3)));

    // ── Episodes ──────────────────────────────────────────────────────────────

    public EpisodeRef? GetEpisodeRef(int episodeId) => Query(
        "SELECT id, parent, season, episode FROM episodes WHERE id = $id", [("$id", episodeId)],
        r => new EpisodeRef(r.GetInt32(0), r.GetInt32(1), Int(r, 2), Int(r, 3))).FirstOrDefault();

    /// <summary>Episodes of a show, optionally one season; <paramref name="season"/> = null and
    /// <paramref name="unplacedOnly"/> = true returns the ones IMDb hasn't numbered (§10.1).</summary>
    public IReadOnlyList<EpisodeRow> GetEpisodes(int showId, int? season = null, bool unplacedOnly = false)
    {
        var where = unplacedOnly ? " AND (e.season IS NULL OR e.episode IS NULL)"
                  : season is not null ? " AND e.season = $s" : string.Empty;
        return Query(
            $"""
            SELECT e.id, e.season, e.episode, t.primary_title, t.start_year, t.runtime, r.rating, r.votes
            FROM episodes e JOIN titles t ON t.id = e.id LEFT JOIN ratings r ON r.id = e.id
            WHERE e.parent = $p{where}
            ORDER BY e.season, e.episode, t.start_year, e.id
            """,
            [("$p", showId), ("$s", season)],
            r => new EpisodeRow(r.GetInt32(0), Int(r, 1), Int(r, 2), r.GetString(3), Int(r, 4), Int(r, 5),
                r.IsDBNull(6) ? null : r.GetDouble(6), Int(r, 7)));
    }

    public int? FindEpisode(int showId, int season, int episode) => Query(
        "SELECT id FROM episodes WHERE parent = $p AND season = $s AND episode = $e ORDER BY id LIMIT 1",
        [("$p", showId), ("$s", season), ("$e", episode)], r => (int?)r.GetInt32(0)).FirstOrDefault();

    // ── People ────────────────────────────────────────────────────────────────

    public NameRow? GetName(int id) => Query(
        "SELECT id, name, birth_year, death_year, professions, known_for FROM names WHERE id = $id",
        [("$id", id)],
        r => new NameRow(r.GetInt32(0), r.GetString(1), Int(r, 2), Int(r, 3),
            Split(Str(r, 4)),
            [.. Split(Str(r, 5)).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0)]))
        .FirstOrDefault();

    /// <summary>Every principal and crew credit for a person, from the by-person indexes.</summary>
    public IReadOnlyList<PersonCreditRow> GetPersonCredits(int personId)
    {
        var principals = Query(
            $"""
            SELECT {TitleColumns}, e.parent, c.name, p.job, p.characters
            FROM principals p
            JOIN titles t ON t.id = p.title_id JOIN title_types tt ON tt.id = t.type
            JOIN categories c ON c.id = p.category
            LEFT JOIN episodes e ON e.id = p.title_id
            WHERE p.person_id = $id
            """,
            [("$id", personId)],
            r => new PersonCreditRow(ReadTitle(r), Int(r, 9), r.GetString(10), Str(r, 11), Str(r, 12)));

        var crew = Query(
            $"""
            SELECT {TitleColumns}, e.parent, c.role
            FROM crew c
            JOIN titles t ON t.id = c.title_id JOIN title_types tt ON tt.id = t.type
            LEFT JOIN episodes e ON e.id = c.title_id
            WHERE c.person_id = $id
            """,
            [("$id", personId)],
            r => new PersonCreditRow(ReadTitle(r), Int(r, 9),
                r.GetInt32(10) == ImdbSchema.RoleDirector ? "director" : "writer", null, null));

        return [.. principals, .. crew];
    }

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Titles whose primary, original or alternate title equals <paramref name="normalized"/>
    /// (exact) or contains it as a phrase (full-text), restricted to <paramref name="types"/> and,
    /// when given, <paramref name="year"/>. Most-voted first, at most <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string normalized, IReadOnlyCollection<string>? types, int? year, int limit)
    {
        if (normalized.Length == 0) return [];

        var filter = new System.Text.StringBuilder();
        var args = new List<(string, object?)> { ("$n", normalized), ("$lim", limit) };
        if (types is { Count: > 0 })
        {
            var names = types.Select((t, i) => { args.Add(($"$t{i}", t)); return $"$t{i}"; });
            filter.Append($" AND tt.name IN ({string.Join(",", names)})");
        }
        if (year is not null)
        {
            filter.Append(" AND t.start_year = $y");
            args.Add(("$y", year));
        }

        var select = $"SELECT s.raw, {TitleColumns}, COALESCE(r.votes, 0) AS votes";
        var joins = "JOIN titles t ON t.id = s.title_id JOIN title_types tt ON tt.id = t.type LEFT JOIN ratings r ON r.id = t.id";

        var exact = Query(
            $"{select} FROM search_names s {joins} WHERE s.norm = $n{filter} ORDER BY votes DESC LIMIT $lim",
            args, ReadHit);

        args.Add(("$q", "\"" + normalized.Replace("\"", "\"\"") + "\""));
        var phrase = Query(
            $"""
            {select}
            FROM (SELECT rowid FROM search_fts WHERE search_fts MATCH $q LIMIT {FtsScanLimit}) m
            JOIN search_names s ON s.rowid = m.rowid {joins}
            WHERE 1 = 1{filter}
            ORDER BY votes DESC LIMIT $lim
            """,
            args, ReadHit);

        return [.. exact, .. phrase];
    }

    private static SearchHit ReadHit(SqliteDataReader r) =>
        new(r.GetString(0), ReadTitle(r, 1), r.GetInt32(10));

    // ── Ratings refresh support ───────────────────────────────────────────────

    public string? MetaValue(string key) => Meta.GetValueOrDefault(key);

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static TitleRow ReadTitle(SqliteDataReader r) => ReadTitle(r, 0);

    private static TitleRow ReadTitle(SqliteDataReader r, int o) => new(
        r.GetInt32(o), r.GetString(o + 1), r.GetString(o + 2), Str(r, o + 3), r.GetInt32(o + 4) == 1,
        Int(r, o + 5), Int(r, o + 6), Int(r, o + 7), Split(Str(r, o + 8)));

    private List<T> Query<T>(string sql, IEnumerable<(string Name, object? Value)> args, Func<SqliteDataReader, T> map)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            if (sql.Contains(name, StringComparison.Ordinal))
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static IReadOnlyList<string> Split(string? s) =>
        s is null ? [] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose() => _db.Dispose();
}
