using System.Diagnostics;
using Chronicle.Plugin.IMDb.Index;
using Chronicle.Plugins;

namespace Chronicle.Plugin.IMDb;

/// <summary>
/// "Sync IMDb Datasets" (<c>sync-imdb-datasets</c>): downloads IMDb's dataset files and builds a
/// fresh local index, then swaps it in (PLUGIN_IMDB.md §4.2, §10.4).
///
/// Does nothing when the live index is already built from the files IMDb is currently publishing,
/// with the current schema and scope settings. Otherwise it downloads all seven files and builds
/// a complete new index; the live one stays in use until the new one is finished, so a failed,
/// cancelled or out-of-space build leaves everything as it was.
/// </summary>
public sealed class ImdbSyncDatasetsTask : IPluginTask
{
    /// <summary>A new index is built beside the live one, so a sync needs room for the downloads
    /// (~2 GB) and a whole new index (~10 GB measured, 2026-10-02) plus working room.</summary>
    internal const long RequiredFreeBytes = 15L * 1024 * 1024 * 1024;

    private ImdbIndexStore? _store;
    private ImdbScope _scope = ImdbScope.Everything;
    private ImdbDatasetSource? _source;
    private ImdbSyncLog? _log;
    private long _requiredFreeBytes = RequiredFreeBytes;

    public string TaskId => "sync-imdb-datasets";

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        var dataDir = ImdbScope.DataDirectory(settings)
            ?? throw new InvalidOperationException(
                $"ImdbSyncDatasetsTask requires '{IPluginTask.DataDirectorySettingsKey}' in settings.");
        _store = new ImdbIndexStore(dataDir);
        _scope = ImdbScope.FromSettings(settings);
        _source = new ImdbDatasetSource(CreateHttpClient());
        _log = new ImdbSyncLog(dataDir);
    }

    internal void ConfigureForTesting(string dataDir, ImdbScope scope, ImdbDatasetSource source, long requiredFreeBytes = 0)
    {
        _store = new ImdbIndexStore(dataDir);
        _scope = scope;
        _source = source;
        _log = new ImdbSyncLog(dataDir);
        _requiredFreeBytes = requiredFreeBytes;
    }

    internal static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Chronicle/1.0 (+https://github.com/thegoddamnbeckster/Chronicle)");
        return http;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_store is null || _source is null || _log is null)
            throw new InvalidOperationException("ImdbSyncDatasetsTask is not configured. Call Configure() first.");

        using var syncLock = _store.TryLock()
            ?? throw new InvalidOperationException("Another IMDb sync is already running.");

        var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in ImdbDatasetSource.AllFiles)
            stamps[file] = await _source.GetStampAsync(file, ct).ConfigureAwait(false) ?? string.Empty;

        if (IsUpToDate(stamps))
        {
            _log.Write("Sync: index is already built from IMDb's current files; nothing to do.");
            _store.DeleteSuperseded();
            return;
        }

        EnsureFreeSpace();
        var total = Stopwatch.StartNew();
        _log.Write($"Sync: starting (scope: {_scope.Fingerprint}).");

        var downloaded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in ImdbDatasetSource.AllFiles)
        {
            var sw = Stopwatch.StartNew();
            var (path, stamp, bytes) = await _source.DownloadAsync(file, _store.DownloadDir, ct).ConfigureAwait(false);
            downloaded[file] = path;
            if (stamp is not null) stamps[file] = stamp;
            _log.Write($"Sync: {file} {bytes / 1048576.0:N0} MB ready in {sw.Elapsed:mm\\:ss}.");
        }

        var output = _store.NewBuildPath();
        try
        {
            var builder = new ImdbIndexBuilder(_scope, _log.Write);
            await Task.Run(() => builder.Build(
                new DatasetFiles(downloaded[ImdbDatasetSource.Basics], downloaded[ImdbDatasetSource.Akas],
                    downloaded[ImdbDatasetSource.Crew], downloaded[ImdbDatasetSource.Episode],
                    downloaded[ImdbDatasetSource.Principals], downloaded[ImdbDatasetSource.Ratings],
                    downloaded[ImdbDatasetSource.Names]),
                output, stamps, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write($"Sync: build failed, the existing index is unchanged: {ex.GetType().Name}: {ex.Message}");
            TryDelete(output);
            throw;
        }

        _store.Activate(output);
        if (!_scope.KeepDownloads)
            foreach (var path in downloaded.Values)
            {
                TryDelete(path);
                TryDelete(path + ".stamp");
            }

        _log.Write($"Sync: finished in {total.Elapsed:hh\\:mm\\:ss}; index {new FileInfo(output).Length / 1048576.0:N0} MB.");
    }

    private bool IsUpToDate(IReadOnlyDictionary<string, string> stamps)
    {
        using var reader = _store!.OpenReader();
        if (reader is null) return false;
        if (reader.MetaValue("scope") != _scope.Fingerprint) return false;
        // The ratings refresh updates title.ratings in place, so it's judged by its own stamp.
        return stamps.All(s => s.Value.Length > 0 &&
            (reader.MetaValue("source:" + s.Key) == s.Value
             || (s.Key == ImdbDatasetSource.Ratings && reader.MetaValue("ratings_source") == s.Value)));
    }

    private void EnsureFreeSpace()
    {
        if (_requiredFreeBytes <= 0) return;
        var root = Path.GetPathRoot(Path.GetFullPath(_store!.DataDir));
        if (string.IsNullOrEmpty(root)) return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < _requiredFreeBytes)
            throw new IOException(
                $"Not enough free disk space for an IMDb sync: {free / 1073741824.0:N1} GB free on {root}, " +
                $"about {_requiredFreeBytes / 1073741824.0:N0} GB needed. The existing index is unchanged.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
