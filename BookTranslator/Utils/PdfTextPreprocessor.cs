using System.Text.RegularExpressions;

namespace BookTranslator.Utils;

public static class PdfTextPreprocessor
{
    private static readonly Regex SpacedUppercaseHeadingLinePattern =
        new(@"^(?:[A-ZƏÖÜĞİŞÇ]\s+){3,}[A-ZƏÖÜĞİŞÇ]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MissingWordBoundaryPattern =
        new(@"(?<=[a-zəöüğışç0-9])(?=[A-ZƏÖÜĞİŞÇ][a-zəöüğışç]{2,})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Preprocess(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        s = s.Replace("\r\n", "\n").Replace("\r", "\n");

        s = new string(s.Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').ToArray());

        // Join PDF line-break hyphenation artifacts.
        s = Regex.Replace(s, @"(\w)[\-â€â€‘â€“]\n(\w)", "$1$2");

        // Only join likely wrapped lines; keep structural line breaks for headings/TOC.
        s = Regex.Replace(s, @"(?<=[a-z0-9,;:])\n(?=[a-z])", " ");

        // Keep paragraph structure stable.
        s = Regex.Replace(s, @"\n{3,}", "\n\n");

        s = Regex.Replace(s, @"[ \t]{2,}", " ");

        // Common OCR/PDF cases where number is stuck to a word.
        s = Regex.Replace(s, @"(?<=\D)(\d{1,3})(?=[A-Za-zƏÖÜĞİŞÇəöüğışç])", "$1 ");
        s = Regex.Replace(s, @"^(\d{1,3})(?=[A-Za-zƏÖÜĞİŞÇəöüğışç])", "$1 ", RegexOptions.Multiline);

        s = Regex.Replace(s, @"([.!?])(?=[A-ZƏÖÜĞİŞÇ])", "$1 ");

        s = NormalizePerLine(s);

        return s.Trim();
    }

    private static string NormalizePerLine(string s)
    {
        string[] lines = s.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length == 0)
                continue;

            string trimmed = line.Trim();

            // Example: "D E S I G N P A T T E R N S" -> "DESIGNPATTERNS"
            if (SpacedUppercaseHeadingLinePattern.IsMatch(trimmed))
            {
                lines[i] = Regex.Replace(trimmed, @"\s+", "");
                continue;
            }

            if (LooksLikeCodeOrUrl(trimmed))
                continue;

            // Example: "Design PatternsHow can ..." -> "Design Patterns How can ..."
            lines[i] = MissingWordBoundaryPattern.Replace(line, " ");
        }

        return string.Join('\n', lines);
    }

    private static bool LooksLikeCodeOrUrl(string line)
    {
        if (line.Contains("://", StringComparison.Ordinal) || line.Contains("www.", StringComparison.OrdinalIgnoreCase))
            return true;

        return Regex.IsMatch(line, @"[{}();=<>\[\]`]|=>|::|\\|/");
    }
}
