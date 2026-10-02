using System.IO.Compression;
using System.Text;

namespace Chronicle.Plugin.IMDb.Index;

/// <summary>
/// Streams one IMDb dataset file (<c>.tsv.gz</c>) row by row. IMDb's TSVs are tab-separated with
/// no quoting at all (titles can contain bare double quotes), <c>\N</c> for null, and a header
/// row, so they're split on tabs only; a CSV parser would mangle them.
/// </summary>
internal sealed class TsvReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly string?[] _fields;

    /// <param name="expectedHeader">IMDb's column names, tab-separated. A file whose header row
    /// differs is refused: either the download is damaged (a truncated gzip just reads as empty)
    /// or IMDb changed the file's layout, and loading it either way would build a wrong index.</param>
    public TsvReader(string gzPath, string expectedHeader)
    {
        var file = new FileStream(gzPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        _reader = new StreamReader(new GZipStream(file, CompressionMode.Decompress), Encoding.UTF8, false, 1 << 20);
        FileName = Path.GetFileName(gzPath);
        Header = _reader.ReadLine() ?? string.Empty;
        if (Header != expectedHeader)
        {
            _reader.Dispose();
            throw new InvalidDataException(
                $"{FileName} doesn't start with IMDb's expected columns ({expectedHeader.Replace('\t', ' ')}); " +
                $"found '{(Header.Length > 200 ? Header[..200] : Header)}'. The download may be damaged or IMDb changed the format.");
        }
        _fields = new string?[expectedHeader.Split('\t').Length];
    }

    public string Header { get; }

    public string FileName { get; }

    /// <summary>Fields of the current row; <c>null</c> where the file has <c>\N</c>.</summary>
    public IReadOnlyList<string?> Fields => _fields;

    /// <summary>Rows read so far (header excluded), including skipped malformed ones.</summary>
    public long RowsRead { get; private set; }

    /// <summary>Rows skipped because they had the wrong number of columns.</summary>
    public long RowsSkipped { get; private set; }

    public bool Read()
    {
        while (_reader.ReadLine() is { } line)
        {
            RowsRead++;
            if (Split(line))
                return true;
            RowsSkipped++;
        }
        return false;
    }

    public string? this[int i] => _fields[i];

    public int? Int(int i) => _fields[i] is { } s && int.TryParse(s, out var v) ? v : null;

    /// <summary>Numeric part of a "tt…"/"nm…" id column.</summary>
    public int? Id(int i) => _fields[i] is { Length: > 2 } s && int.TryParse(s.AsSpan(2), out var v) ? v : null;

    private bool Split(string line)
    {
        var start = 0;
        for (var col = 0; col < _fields.Length; col++)
        {
            var tab = col == _fields.Length - 1 ? -1 : line.IndexOf('\t', start);
            if (col < _fields.Length - 1 && tab < 0) return false;
            var end = tab < 0 ? line.Length : tab;
            var value = line[start..end];
            _fields[col] = value == @"\N" ? null : value;
            start = end + 1;
        }
        return true;
    }

    public void Dispose() => _reader.Dispose();
}
