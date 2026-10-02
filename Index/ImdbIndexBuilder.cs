using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chronicle.Plugin.IMDb.Index;

/// <summary>The seven dataset files a build reads, already downloaded.</summary>
internal sealed record DatasetFiles(
    string Basics, string Akas, string Crew, string Episode, string Principals, string Ratings, string Names);

/// <summary>
/// Builds a complete index file from the downloaded TSVs (PLUGIN_IMDB.md §4.1/§4.2). Writes to a
/// file nobody reads yet; <see cref="ImdbIndexStore"/> swaps it in afterwards, so a failed or
/// cancelled build never touches the working index.
///
/// Load order matters: titles first (their kept/episode sets decide what every later file keeps),
/// then episodes, ratings, alternate titles, crew, principals and finally people, who are only
/// filtered when the scope drops titles or credits (so nobody is lost with the default settings).
/// </summary>
internal sealed class ImdbIndexBuilder(ImdbScope scope, Action<string> log)
{
    private const string EpisodeType = "tvEpisode";
    private const int CommitEvery = 500_000;

    private readonly Bits _keptTitles = new();
    private readonly Bits _episodes   = new();
    private readonly Bits _people     = new();
    private readonly Dictionary<string, int> _types      = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _categories = new(StringComparer.Ordinal);

    private long _duplicateKeys;

    public Dictionary<string, long> RowCounts { get; } = new(StringComparer.Ordinal);

    public void Build(DatasetFiles files, string outputPath, IReadOnlyDictionary<string, string> sourceStamps,
        CancellationToken ct)
    {
        if (File.Exists(outputPath)) File.Delete(outputPath);
        var total = Stopwatch.StartNew();

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = outputPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString());
        db.Open();
        Exec(db, "PRAGMA page_size = 8192");
        Exec(db, "PRAGMA journal_mode = OFF");
        Exec(db, "PRAGMA synchronous = OFF");
        Exec(db, "PRAGMA locking_mode = EXCLUSIVE");
        Exec(db, "PRAGMA cache_size = -524288"); // 512 MB
        Exec(db, ImdbSchema.Tables);

        Step("titles",     () => LoadTitles(db, files.Basics, ct));
        Step("episodes",   () => LoadEpisodes(db, files.Episode, ct));
        Step("ratings",    () => LoadRatings(db, files.Ratings, ct));
        Step("akas",       () => LoadAkas(db, files.Akas, ct));
        Step("crew",       () => LoadCrew(db, files.Crew, ct));
        Step("principals", () => LoadPrincipals(db, files.Principals, ct));
        Step("names",      () => LoadNames(db, files.Names, ct));

        Step("lookups", () =>
        {
            InsertLookup(db, "title_types", _types);
            InsertLookup(db, "categories", _categories);
            return _types.Count + _categories.Count;
        });
        Step("search dedupe", () => Exec(db,
            "DELETE FROM search_names WHERE rowid NOT IN (SELECT MIN(rowid) FROM search_names GROUP BY title_id, norm)"));
        Step("indexes", () => { Exec(db, ImdbSchema.Indexes); return 0; });
        // Sampled statistics: enough for the planner to pick the right index, without reading
        // every row of a multi-GB file.
        Step("analyze", () => { Exec(db, "PRAGMA analysis_limit = 1000"); Exec(db, "ANALYZE"); return 0; });

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema_version"] = ImdbSchema.Version.ToString(CultureInfo.InvariantCulture),
            ["scope"]          = scope.Fingerprint,
            ["built_at"]       = DateTimeOffset.UtcNow.ToString("O"),
        };
        foreach (var (k, v) in sourceStamps) meta["source:" + k] = v;
        foreach (var (k, v) in RowCounts) meta["rows:" + k] = v.ToString(CultureInfo.InvariantCulture);
        using (var tx = db.BeginTransaction())
        {
            using var cmd = Prepare(db, tx, "INSERT INTO meta (key, value) VALUES ($k, $v)", "$k", "$v");
            foreach (var (k, v) in meta) Run(cmd, k, v);
            tx.Commit();
        }

        // Leave the file in a normal, shareable state for the read-only readers.
        Exec(db, "PRAGMA locking_mode = NORMAL");
        Exec(db, "PRAGMA journal_mode = DELETE");
        db.Close();
        log($"IMDb index built in {total.Elapsed:hh\\:mm\\:ss}: {new FileInfo(outputPath).Length / 1048576.0:N0} MB");

        void Step(string name, Func<long> work)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var duplicatesBefore = _duplicateKeys;
            var rows = work();
            RowCounts[name] = rows;
            log($"IMDb index: {name} done, {rows:N0} rows in {sw.Elapsed:mm\\:ss}" +
                (_duplicateKeys > duplicatesBefore ? $" ({_duplicateKeys - duplicatesBefore:N0} repeated keys skipped)" : ""));
        }
    }

    // ── Loaders ───────────────────────────────────────────────────────────────

    private long LoadTitles(SqliteConnection db, string path, CancellationToken ct)
    {
        // tconst titleType primaryTitle originalTitle isAdult startYear endYear runtimeMinutes genres
        using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Basics]);
        return Load(db, tsv, ct,
            ["INSERT OR IGNORE INTO titles VALUES ($id,$type,$pt,$ot,$adult,$sy,$ey,$rt,$g)",
             "INSERT INTO search_names (norm, raw, title_id) VALUES ($n,$r,$id)"],
            (cmds, r) =>
            {
                if (r.Id(0) is not { } id || r[1] is not { } type || r[2] is not { } primary) return false;
                var adult = r[4] == "1";
                if (adult && !scope.IncludeAdult) return false;
                if (!scope.KeepsTitleType(type)) return false;

                var original = r[3] is { } o && o != primary ? o : null;
                if (Run(cmds[0], id, TypeId(type), primary, original, adult ? 1 : 0, r.Int(5), r.Int(6), r.Int(7), r[8]) == 0)
                    return Duplicate();
                _keptTitles.Set(id);

                if (type == EpisodeType)
                {
                    _episodes.Set(id);
                }
                else
                {
                    AddSearchName(cmds[1], id, primary);
                    if (original is not null) AddSearchName(cmds[1], id, original);
                }
                return true;
            });
    }

    private long LoadEpisodes(SqliteConnection db, string path, CancellationToken ct)
    {
        // tconst parentTconst seasonNumber episodeNumber
        using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Episode]);
        return Load(db, tsv, ct, ["INSERT OR IGNORE INTO episodes VALUES ($id,$p,$s,$e)"], (cmds, r) =>
        {
            if (r.Id(0) is not { } id || r.Id(1) is not { } parent) return false;
            if (!_keptTitles.Get(id) || !_keptTitles.Get(parent)) return false;
            return Run(cmds[0], id, parent, r.Int(2), r.Int(3)) == 1 || Duplicate();
        });
    }

    private long LoadRatings(SqliteConnection db, string path, CancellationToken ct)
    {
        using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Ratings]);
        return Load(db, tsv, ct, ["INSERT OR IGNORE INTO ratings VALUES ($id,$r,$v)"], (cmds, r) =>
        {
            if (r.Id(0) is not { } id || !_keptTitles.Get(id)) return false;
            if (!double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)) return false;
            return Run(cmds[0], id, rating, r.Int(2) ?? 0) == 1 || Duplicate();
        });
    }

    private long LoadAkas(SqliteConnection db, string path, CancellationToken ct)
    {
        // titleId ordering title region language types attributes isOriginalTitle
        using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Akas]);
        return Load(db, tsv, ct,
            ["INSERT OR IGNORE INTO akas VALUES ($id,$o,$t,$reg,$lang,$types,$attr,$orig)",
             "INSERT INTO search_names (norm, raw, title_id) VALUES ($n,$r,$id)"],
            (cmds, r) =>
            {
                if (r.Id(0) is not { } id || r[2] is not { } title || !_keptTitles.Get(id)) return false;
                var isOriginal = r[7] == "1";
                if (!isOriginal && !AkaInScope(r[3], r[4])) return false;
                if (Run(cmds[0], id, r.Int(1) ?? 0, title, r[3], r[4], r[5], r[6], isOriginal ? 1 : 0) == 0)
                    return Duplicate();
                if (!_episodes.Get(id)) AddSearchName(cmds[1], id, title);
                return true;
            });
    }

    // Region filter: the alternate title's region must be one of the chosen ones. Language
    // filter: its language must be one of the chosen ones, or unrecorded (IMDb leaves language
    // empty on most rows, where the region already says it).
    private bool AkaInScope(string? region, string? language) =>
        (scope.AkaRegions is null || (region is not null && scope.AkaRegions.Contains(region)))
        && (scope.AkaLanguages is null || language is null || scope.AkaLanguages.Contains(language));

    private long LoadCrew(SqliteConnection db, string path, CancellationToken ct)
    {
        // tconst directors writers (comma-separated nconst lists)
        using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Crew]);
        return Load(db, tsv, ct, ["INSERT OR IGNORE INTO crew VALUES ($t,$role,$o,$p)"], (cmds, r) =>
        {
            if (r.Id(0) is not { } id || !KeepsCredits(id)) return false;
            var any = false;
            any |= AddCrew(cmds[0], id, r[1], ImdbSchema.RoleDirector);
            any |= AddCrew(cmds[0], id, r[2], ImdbSchema.RoleWriter);
            return any;
        });
    }

    private bool AddCrew(SqliteCommand cmd, int titleId, string? people, int role)
    {
        if (people is null) return false;
        var ordering = 0;
        foreach (var nm in people.Split(','))
        {
            if (nm.Length <= 2 || !int.TryParse(nm.AsSpan(2), out var personId)) continue;
            _people.Set(personId);
            if (Run(cmd, titleId, role, ordering++, personId) == 0) Duplicate();
        }
        return ordering > 0;
    }

    private long LoadPrincipals(SqliteConnection db, string path, CancellationToken ct)
    {
        // tconst ordering nconst category job characters
        using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Principals]);
        return Load(db, tsv, ct, ["INSERT OR IGNORE INTO principals VALUES ($t,$o,$p,$c,$j,$ch)"], (cmds, r) =>
        {
            if (r.Id(0) is not { } id || r.Id(2) is not { } personId || r[3] is not { } category) return false;
            if (!KeepsCredits(id)) return false;
            _people.Set(personId);
            return Run(cmds[0], id, r.Int(1) ?? 0, personId, CategoryId(category), r[4], r[5]) == 1 || Duplicate();
        });
    }

    private long LoadNames(SqliteConnection db, string path, CancellationToken ct)
    {
        // nconst primaryName birthYear deathYear primaryProfession knownForTitles
        using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Names]);
        return Load(db, tsv, ct, ["INSERT OR IGNORE INTO names VALUES ($id,$n,$b,$d,$p,$k)"], (cmds, r) =>
        {
            if (r.Id(0) is not { } id || r[1] is not { } name) return false;
            if (scope.DropsTitlesOrCredits && !_people.Get(id)) return false;
            return Run(cmds[0], id, name, r.Int(2), r.Int(3), r[4], KnownFor(r[5])) == 1 || Duplicate();
        });
    }

    private bool KeepsCredits(int titleId) =>
        _keptTitles.Get(titleId) && (scope.EpisodeCredits || !_episodes.Get(titleId));

    private static string? KnownFor(string? list) =>
        list is null ? null : string.Join(",", list.Split(',')
            .Select(t => t.Length > 2 && int.TryParse(t.AsSpan(2), out var n) ? n : 0)
            .Where(n => n > 0));

    private static void AddSearchName(SqliteCommand cmd, int titleId, string raw)
    {
        var norm = TitleText.Normalize(raw);
        if (norm.Length > 0) Run(cmd, norm, raw, titleId);
    }

    private int TypeId(string name) => Lookup(_types, name);
    private int CategoryId(string name) => Lookup(_categories, name);

    private static int Lookup(Dictionary<string, int> map, string name)
    {
        if (!map.TryGetValue(name, out var id))
            map[name] = id = map.Count + 1;
        return id;
    }

    private static void InsertLookup(SqliteConnection db, string table, Dictionary<string, int> map)
    {
        using var tx = db.BeginTransaction();
        using var cmd = Prepare(db, tx, $"INSERT INTO {table} (id, name) VALUES ($id, $name)", "$id", "$name");
        foreach (var (name, id) in map) Run(cmd, id, name);
        tx.Commit();
    }

    // ── Bulk-load plumbing ────────────────────────────────────────────────────

    /// <summary>Streams every row of <paramref name="tsv"/> through <paramref name="row"/> with the
    /// given prepared statements, committing in batches. Returns how many rows were kept.</summary>
    private long Load(SqliteConnection db, TsvReader tsv, CancellationToken ct, string[] sql,
        Func<SqliteCommand[], TsvReader, bool> row)
    {
        long kept = 0, sinceCommit = 0;
        var tx = db.BeginTransaction();
        var cmds = sql.Select(s => Prepare(db, tx, s)).ToArray();
        try
        {
            while (tsv.Read())
            {
                if (row(cmds, tsv)) kept++;
                if (++sinceCommit < CommitEvery) continue;

                ct.ThrowIfCancellationRequested();
                tx.Commit();
                tx.Dispose();
                tx = db.BeginTransaction();
                foreach (var c in cmds) c.Transaction = tx;
                sinceCommit = 0;
            }
            tx.Commit();
        }
        finally
        {
            foreach (var c in cmds) c.Dispose();
            tx.Dispose();
        }

        if (tsv.RowsSkipped > 0)
            log($"IMDb index: skipped {tsv.RowsSkipped:N0} malformed rows in {tsv.FileName}");
        return kept;
    }

    private static SqliteCommand Prepare(SqliteConnection db, SqliteTransaction tx, string sql, params string[] names)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        // Parameter names are the $tokens in the VALUES list, in order.
        var tokens = names.Length > 0 ? names : ParameterNames(sql);
        foreach (var n in tokens) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
        cmd.Prepare();
        return cmd;
    }

    private static string[] ParameterNames(string sql)
    {
        var values = sql[sql.LastIndexOf("VALUES", StringComparison.Ordinal)..];
        return [.. System.Text.RegularExpressions.Regex.Matches(values, @"\$\w+").Select(m => m.Value)];
    }

    /// <summary>Executes a prepared insert; returns rows written (0 when INSERT OR IGNORE skipped it).</summary>
    private static int Run(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Every table is keyed on IMDb's own ids (and ordering), which IMDb publishes as unique.
    /// Should a file ever repeat a key, the first row wins and the repeat is skipped and counted
    /// rather than aborting the build; the count is logged so it's visible.
    /// </summary>
    private bool Duplicate()
    {
        _duplicateKeys++;
        return false;
    }

    private static long Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Growable bit set over IMDb's numeric ids (tt/nm numbers stay under ~10^8).</summary>
    private sealed class Bits
    {
        private ulong[] _words = new ulong[1 << 16];

        public void Set(int i)
        {
            var w = i >> 6;
            if (w >= _words.Length) Array.Resize(ref _words, Math.Max(w + 1, _words.Length * 2));
            _words[w] |= 1UL << (i & 63);
        }

        public bool Get(int i)
        {
            var w = i >> 6;
            return i >= 0 && w < _words.Length && (_words[w] & (1UL << (i & 63))) != 0;
        }
    }
}
