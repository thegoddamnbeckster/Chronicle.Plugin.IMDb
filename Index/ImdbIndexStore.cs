using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chronicle.Plugin.IMDb.Index;

/// <summary>
/// Where the index lives inside the plugin's data folder, and how a new build is swapped in.
///
/// Each build gets its own file (<c>imdb-20261002T030000-1a2b3c.db</c>) and <c>current-index.txt</c>
/// names the live one. Swapping is a rewrite of that small pointer file (temp file + move), not a
/// rename of the database: on Windows a file another connection has open can't be renamed, and
/// the provider may be mid-search when a sync finishes. Readers open the file the pointer names
/// on each call, so they move to the new index on their next lookup. Superseded files are deleted
/// once nothing has them open (retried after every sync).
/// </summary>
internal sealed class ImdbIndexStore(string dataDir)
{
    private const string PointerFile = "current-index.txt";
    private const string LockFile    = "sync.lock";

    public string DataDir => dataDir;
    public string DownloadDir => Path.Combine(dataDir, "downloads");

    /// <summary>Path of the live index, or null when none has been built yet.</summary>
    public string? CurrentPath
    {
        get
        {
            try
            {
                var pointer = Path.Combine(dataDir, PointerFile);
                if (!File.Exists(pointer)) return null;
                var name = File.ReadAllText(pointer).Trim();
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) return null;
                var path = Path.Combine(dataDir, name);
                return File.Exists(path) ? path : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    public string NewBuildPath() =>
        Path.Combine(dataDir,
            $"imdb-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}.db");

    /// <summary>Opens the live index read-only, or returns null when there is none or it was built
    /// by a different schema version (PLUGIN_IMDB.md §10.4: an incompatible file is never read).</summary>
    public ImdbIndexReader? OpenReader()
    {
        var path = CurrentPath;
        if (path is null) return null;
        var reader = ImdbIndexReader.Open(path);
        if (reader.SchemaVersion == ImdbSchema.Version) return reader;
        reader.Dispose();
        return null;
    }

    /// <summary>Points readers at <paramref name="builtPath"/> and deletes older index files.</summary>
    public void Activate(string builtPath)
    {
        var tmp = Path.Combine(dataDir, PointerFile + ".tmp");
        File.WriteAllText(tmp, Path.GetFileName(builtPath));
        File.Move(tmp, Path.Combine(dataDir, PointerFile), overwrite: true);
        DeleteSuperseded();
    }

    /// <summary>Best-effort removal of every index file that isn't the live one, including
    /// half-built files left by a cancelled or crashed build. A file still open by a reader just
    /// stays until the next sync.</summary>
    public void DeleteSuperseded()
    {
        var current = CurrentPath is { } p ? Path.GetFileName(p) : null;
        foreach (var file in Directory.EnumerateFiles(dataDir, "imdb-*.db*"))
        {
            var name = Path.GetFileName(file);
            if (current is not null && (name == current || name == current + "-journal")) continue;
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Takes the one-sync-at-a-time lock (the full sync and the ratings refresh share it). Returns
    /// null when another run holds it. The lock is an open handle, so a crashed run releases it
    /// automatically; there's no stale lock file to clean up.
    /// </summary>
    public IDisposable? TryLock()
    {
        Directory.CreateDirectory(dataDir);
        try
        {
            return new FileStream(Path.Combine(dataDir, LockFile), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
    }

    /// <summary>Opens the live index for the in-place ratings update.</summary>
    public SqliteConnection? OpenWritable()
    {
        var path = CurrentPath;
        if (path is null) return null;
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 300,
        }.ToString());
        c.Open();
        return c;
    }
}
