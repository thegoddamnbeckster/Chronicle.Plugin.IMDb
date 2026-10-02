namespace Chronicle.Plugin.IMDb;

/// <summary>
/// The plugin's own log file, <c>{data_dir}/imdb-sync.log</c>. Chronicle doesn't hand plugins a
/// logger, and a sync is long enough (tens of minutes) that its progress, file sizes, row counts
/// and timings need to be somewhere a user can look. Kept to the last ~1 MB.
/// </summary>
internal sealed class ImdbSyncLog(string dataDir)
{
    private const long MaxBytes = 1 << 20;
    private readonly object _gate = new();

    public string Path => System.IO.Path.Combine(dataDir, "imdb-sync.log");

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    var text = File.ReadAllText(Path);
                    File.WriteAllText(Path, text[(text.Length / 2)..]);
                }
                File.AppendAllText(Path, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
