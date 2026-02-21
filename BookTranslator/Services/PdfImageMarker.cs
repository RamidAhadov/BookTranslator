using System.Text.RegularExpressions;

namespace BookTranslator.Services;

public static class PdfImageMarker
{
    public static readonly Regex TokenRegex =
        new(@"__IMG_(\d{5})__", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string FromIndex(int index) => $"__IMG_{index:D5}__";
}
