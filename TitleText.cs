using System.Text.RegularExpressions;

namespace Chronicle.Plugin.IMDb;

/// <summary>
/// Title normalisation shared by the index build and the search scoring, so "equal" means the
/// same thing to both (PLUGIN_IMDB.md §10.10). Identical to TmdbMetadataProvider.Normalize: strip
/// <c>: - , . '</c>, collapse whitespace, lowercase. Copied rather than referenced, the same way
/// every plugin carries its own copy (plugins depend only on the Chronicle.Plugins contract).
/// </summary>
internal static partial class TitleText
{
    [GeneratedRegex(@"[:\-,\.']")]
    private static partial Regex PunctuationRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRe();

    /// <summary>Trailing " (YYYY)", as in file-scanner folder names. Same pattern TMDB strips.</summary>
    [GeneratedRegex(@"\s*\((\d{4})\)\s*$")]
    public static partial Regex YearSuffixRe();

    public static string Normalize(string s) =>
        WhitespaceRe().Replace(PunctuationRe().Replace(s.Trim(), " "), " ").Trim().ToLowerInvariant();

    /// <summary>Removes a trailing "(YYYY)" and returns the year it held, if any. The year is
    /// read digit by digit because \d also matches non-ASCII digits (see TMDB's TryParseDigits).</summary>
    public static string StripYearSuffix(string title, out int? year)
    {
        year = null;
        var m = YearSuffixRe().Match(title);
        if (!m.Success) return title;

        var value = 0;
        foreach (var c in m.Groups[1].Value)
        {
            var d = System.Globalization.CharUnicodeInfo.GetDecimalDigitValue(c);
            if (d < 0) return title[..m.Index].Trim();
            value = value * 10 + d;
        }
        year = value;
        return title[..m.Index].Trim();
    }
}
