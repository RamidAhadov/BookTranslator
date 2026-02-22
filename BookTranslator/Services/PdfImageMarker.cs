using System.Text.RegularExpressions;

namespace BookTranslator.Services;

public static class PdfImageMarker
{
    public static readonly Regex TokenRegex =
        new(@"__IMG_(\d{5})__", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly Regex RelaxedTokenRegex =
        new(@"(?:__\s*IMG[_\-\s]*0*(\d{1,5})\s*__|[<\[]\s*IMG[_\-\s]*0*(\d{1,5})\s*__\s*[>\]])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string FromIndex(int index) => $"__IMG_{index:D5}__";

    public static string NormalizeCorruptedMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        return RelaxedTokenRegex.Replace(text, m =>
        {
            int idx = ExtractIndex(m);
            return idx > 0 ? FromIndex(idx) : m.Value;
        });
    }

    private static int ExtractIndex(Match match)
    {
        for (int i = 1; i < match.Groups.Count; i++)
        {
            Group g = match.Groups[i];
            if (!g.Success || string.IsNullOrWhiteSpace(g.Value))
                continue;

            if (int.TryParse(g.Value, out int idx) && idx > 0)
                return idx;
        }

        return 0;
    }
}
