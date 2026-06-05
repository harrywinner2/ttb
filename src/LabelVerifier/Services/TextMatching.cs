using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LabelVerifier.Services;

/// <summary>
/// Normalisation + fuzzy comparison so trivial formatting differences don't read
/// as violations — Dave's "STONE'S THROW" vs "Stone's Throw" example. The warning
/// check deliberately does NOT use this; that one must stay strict.
/// </summary>
public static partial class TextMatching
{
    /// <summary>
    /// Lower-cases, strips accents, straightens curly quotes, collapses whitespace
    /// and drops surrounding punctuation so two human-equivalent strings compare equal.
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim().ToLowerInvariant();
        s = s.Replace('’', '\'').Replace('‘', '\'')   // curly → straight apostrophes
             .Replace('“', '"').Replace('”', '"')
             .Replace('–', '-').Replace('—', '-');    // en/em dash → hyphen
        s = StripDiacritics(s);
        s = PunctRegex().Replace(s, " ");                        // punctuation → space
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s;
    }

    /// <summary>0..1 similarity based on Levenshtein distance over normalised strings.</summary>
    public static double Similarity(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 && nb.Length == 0) return 1.0;
        if (na.Length == 0 || nb.Length == 0) return 0.0;
        if (na == nb) return 1.0;
        int dist = Levenshtein(na, nb);
        int max = Math.Max(na.Length, nb.Length);
        return 1.0 - (double)dist / max;
    }

    /// <summary>True if <paramref name="needle"/> appears within <paramref name="haystack"/> after normalisation.</summary>
    public static bool NormalizedContains(string? haystack, string? needle)
    {
        var nh = Normalize(haystack);
        var nn = Normalize(needle);
        return nn.Length > 0 && nh.Contains(nn);
    }

    /// <summary>Pulls the first alcohol-by-volume percentage out of a free-form string.</summary>
    public static double? ParseAbv(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = AbvRegex().Match(s);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
            return pct;
        return null;
    }

    /// <summary>Pulls a "NN Proof" value if present (proof should be 2× ABV for spirits).</summary>
    public static double? ParseProof(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = ProofRegex().Match(s);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pr))
            return pr;
        return null;
    }

    private static string StripDiacritics(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex PunctRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(\d{1,2}(?:\.\d+)?)\s*%")]
    private static partial Regex AbvRegex();

    [GeneratedRegex(@"(\d{2,3}(?:\.\d+)?)\s*proof", RegexOptions.IgnoreCase)]
    private static partial Regex ProofRegex();
}
