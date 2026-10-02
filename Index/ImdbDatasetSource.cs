using System.Net;

namespace Chronicle.Plugin.IMDb.Index;

/// <summary>
/// Downloads IMDb's non-commercial dataset files (https://datasets.imdbws.com/). These files are
/// the only IMDb data the plugin uses: IMDb licenses them for personal, non-commercial use and
/// forbids scraping imdb.com itself (PLUGIN_IMDB.md §1).
/// </summary>
internal sealed class ImdbDatasetSource(HttpClient http, string baseUrl = ImdbDatasetSource.DefaultBaseUrl)
{
    public const string DefaultBaseUrl = "https://datasets.imdbws.com/";

    public const string Basics = "title.basics", Akas = "title.akas", Crew = "title.crew",
        Episode = "title.episode", Principals = "title.principals", Ratings = "title.ratings",
        Names = "name.basics";

    public static readonly string[] AllFiles = [Basics, Akas, Crew, Episode, Principals, Ratings, Names];

    /// <summary>Each file's header row, as published (checked live 2026-10-02).</summary>
    public static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        [Basics]     = "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres",
        [Akas]       = "titleId\tordering\ttitle\tregion\tlanguage\ttypes\tattributes\tisOriginalTitle",
        [Crew]       = "tconst\tdirectors\twriters",
        [Episode]    = "tconst\tparentTconst\tseasonNumber\tepisodeNumber",
        [Principals] = "tconst\tordering\tnconst\tcategory\tjob\tcharacters",
        [Ratings]    = "tconst\taverageRating\tnumVotes",
        [Names]      = "nconst\tprimaryName\tbirthYear\tdeathYear\tprimaryProfession\tknownForTitles",
    };

    /// <summary>IMDb's Last-Modified for a file, as text (what the index stores to tell whether a
    /// rebuild is needed). Null when the server doesn't say.</summary>
    public async Task<string?> GetStampAsync(string file, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, Url(file));
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return Stamp(resp);
    }

    /// <summary>Downloads <paramref name="file"/> into <paramref name="dir"/>, streaming to a
    /// <c>.part</c> file that's renamed only once complete. Returns its path and Last-Modified.
    /// A copy already on disk with the same Last-Modified (kept with <c>keep_downloads</c>) is
    /// reused instead of downloaded again.</summary>
    public async Task<(string Path, string? Stamp, long Bytes)> DownloadAsync(
        string file, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, file + ".tsv.gz");
        var stampFile = target + ".stamp";

        using var resp = await http.GetAsync(Url(file), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"IMDb no longer publishes {file}.tsv.gz at {baseUrl}. The existing index is kept.");
        resp.EnsureSuccessStatusCode();
        var stamp = Stamp(resp);

        if (stamp is not null && File.Exists(target) && File.Exists(stampFile)
            && await File.ReadAllTextAsync(stampFile, ct).ConfigureAwait(false) == stamp)
            return (target, stamp, new FileInfo(target).Length);

        var part = target + ".part";
        await using (var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            await body.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);

        File.Move(part, target, overwrite: true);
        if (stamp is not null) await File.WriteAllTextAsync(stampFile, stamp, ct).ConfigureAwait(false);
        else if (File.Exists(stampFile)) File.Delete(stampFile);
        return (target, stamp, new FileInfo(target).Length);
    }

    private string Url(string file) => $"{baseUrl.TrimEnd('/')}/{file}.tsv.gz";

    private static string? Stamp(HttpResponseMessage resp) =>
        resp.Content.Headers.LastModified?.ToString("R") ?? resp.Headers.ETag?.Tag;
}
