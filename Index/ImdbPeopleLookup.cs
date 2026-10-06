using Microsoft.Data.Sqlite;

namespace Chronicle.Plugin.IMDb.Index;

/// <summary>
/// "Who is called this and was born then?" -- a small side index of every IMDb person with a known birth year,
/// keyed by normalised name and birth year, built once from the live index the first time it is needed.
///
/// The main index has no way to look a person up by name (a name search was never part of its design and adding
/// one would change its schema, which makes every installed index unreadable until a multi-hour re-download and
/// rebuild). This file sits beside it instead: nothing about the main index changes, an index swap just means a
/// fresh side file is built the next time a person is searched, and the old one is deleted then.
/// </summary>
internal static class ImdbPeopleLookup
{
    private static readonly object BuildGate = new();

    /// <summary>The side file for <paramref name="indexPath"/>. Named without the "imdb-" prefix so the index
    /// store's sweep of superseded index files never deletes a side file that is still current.</summary>
    internal static string SidecarPath(string indexPath) =>
        Path.Combine(Path.GetDirectoryName(indexPath)!, "people-" + Path.GetFileNameWithoutExtension(indexPath) + ".db");

    /// <summary>Ids of the people called <paramref name="normalizedName"/> born in <paramref name="birthYear"/>.</summary>
    public static IReadOnlyList<int> Find(ImdbIndexReader index, string normalizedName, int birthYear)
    {
        if (normalizedName.Length == 0) return [];
        var path = EnsureBuilt(index);

        using var db = Open(path, SqliteOpenMode.ReadOnly);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM people WHERE norm = $n AND birth = $y";
        cmd.Parameters.AddWithValue("$n", normalizedName);
        cmd.Parameters.AddWithValue("$y", birthYear);
        using var r = cmd.ExecuteReader();
        var ids = new List<int>();
        while (r.Read()) ids.Add(r.GetInt32(0));
        return ids;
    }

    private static string EnsureBuilt(ImdbIndexReader index)
    {
        var path = SidecarPath(index.Path);
        if (File.Exists(path)) return path;

        lock (BuildGate)
        {
            if (File.Exists(path)) return path;

            // Built beside the final name and moved into place, so a half-built file (a crash, a stopped API)
            // is never mistaken for a finished one.
            var tmp = path + ".building";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var db = Open(tmp, SqliteOpenMode.ReadWriteCreate))
            {
                Exec(db, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; " +
                         "CREATE TABLE people (norm TEXT NOT NULL, birth INTEGER NOT NULL, id INTEGER NOT NULL);");
                using (var tx = db.BeginTransaction())
                {
                    using var insert = db.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = "INSERT INTO people (norm, birth, id) VALUES ($n, $b, $i)";
                    var n = insert.Parameters.Add("$n", SqliteType.Text);
                    var b = insert.Parameters.Add("$b", SqliteType.Integer);
                    var i = insert.Parameters.Add("$i", SqliteType.Integer);
                    foreach (var (id, name, birth) in index.EnumerateNamesWithBirthYear())
                    {
                        var norm = TitleText.NormalizePersonName(name);
                        if (norm.Length == 0) continue;
                        n.Value = norm; b.Value = birth; i.Value = id;
                        insert.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                Exec(db, "CREATE INDEX ix_people ON people (norm, birth);");
            }
            SqliteConnection.ClearAllPools();
            File.Move(tmp, path, overwrite: true);

            // Side files of earlier indexes are no use any more.
            foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "people-*.db"))
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return path;
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 300,
        }.ToString());
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
