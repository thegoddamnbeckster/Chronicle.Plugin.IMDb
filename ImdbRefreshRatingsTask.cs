using System.Diagnostics;
using System.Globalization;
using Chronicle.Plugin.IMDb.Index;
using Chronicle.Plugins;

namespace Chronicle.Plugin.IMDb;

/// <summary>
/// "Refresh IMDb Ratings" (<c>refresh-imdb-ratings</c>): replaces the scores and vote counts in
/// the live index from IMDb's small daily ratings file (~9 MB), without a full rebuild
/// (PLUGIN_IMDB.md §4.2). Library items pick the new numbers up the next time IMDb metadata is
/// re-read for them (Re-sync All Metadata, which is a local read for this plugin).
/// </summary>
public sealed class ImdbRefreshRatingsTask : IPluginTask
{
    private ImdbIndexStore? _store;
    private ImdbDatasetSource? _source;
    private ImdbSyncLog? _log;

    public string TaskId => "refresh-imdb-ratings";

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        var dataDir = ImdbScope.DataDirectory(settings)
            ?? throw new InvalidOperationException(
                $"ImdbRefreshRatingsTask requires '{IPluginTask.DataDirectorySettingsKey}' in settings.");
        _store = new ImdbIndexStore(dataDir);
        _source = new ImdbDatasetSource(ImdbSyncDatasetsTask.CreateHttpClient());
        _log = new ImdbSyncLog(dataDir);
    }

    internal void ConfigureForTesting(string dataDir, ImdbDatasetSource source)
    {
        _store = new ImdbIndexStore(dataDir);
        _source = source;
        _log = new ImdbSyncLog(dataDir);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_store is null || _source is null || _log is null)
            throw new InvalidOperationException("ImdbRefreshRatingsTask is not configured. Call Configure() first.");

        using var syncLock = _store.TryLock();
        if (syncLock is null)
        {
            _log.Write("Ratings: skipped, a sync is running.");
            return;
        }

        using var db = _store.OpenWritable();
        if (db is null)
        {
            _log.Write("Ratings: skipped, no index yet (run Sync IMDb Datasets first).");
            return;
        }

        var stamp = await _source.GetStampAsync(ImdbDatasetSource.Ratings, ct).ConfigureAwait(false);
        if (stamp is not null && (Meta(db, "ratings_source") ?? Meta(db, "source:" + ImdbDatasetSource.Ratings)) == stamp)
        {
            _log.Write("Ratings: already current.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(_store.DataDir, "downloads");
        var (path, fileStamp, _) = await _source.DownloadAsync(ImdbDatasetSource.Ratings, dir, ct).ConfigureAwait(false);

        long rows = 0;
        await Task.Run(() =>
        {
            // One transaction: readers keep seeing the old scores until it commits.
            using var tx = db.BeginTransaction();
            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM ratings";
                del.ExecuteNonQuery();
            }

            using var ins = db.CreateCommand();
            ins.Transaction = tx;
            // Only titles the index holds (the scope settings may have left some out).
            ins.CommandText = "INSERT INTO ratings SELECT $id, $r, $v WHERE EXISTS (SELECT 1 FROM titles WHERE id = $id)";
            var pId = ins.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pR = ins.Parameters.Add("$r", Microsoft.Data.Sqlite.SqliteType.Real);
            var pV = ins.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Integer);
            ins.Prepare();

            using var tsv = new TsvReader(path, ImdbDatasetSource.Headers[ImdbDatasetSource.Ratings]);
            while (tsv.Read())
            {
                if (tsv.Id(0) is not { } id
                    || !double.TryParse(tsv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
                    continue;
                pId.Value = id;
                pR.Value = rating;
                pV.Value = tsv.Int(2) ?? 0;
                rows += ins.ExecuteNonQuery();
                if ((tsv.RowsRead & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            }

            SetMeta(db, tx, "ratings_updated_at", DateTimeOffset.UtcNow.ToString("O"));
            if (fileStamp is not null) SetMeta(db, tx, "ratings_source", fileStamp);
            tx.Commit();
        }, ct).ConfigureAwait(false);

        try { File.Delete(path); File.Delete(path + ".stamp"); }
        catch (IOException) { }

        _log.Write($"Ratings: {rows:N0} scores refreshed in {sw.Elapsed:mm\\:ss}.");
    }

    private static string? Meta(Microsoft.Data.Sqlite.SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx,
        string key, string value)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
