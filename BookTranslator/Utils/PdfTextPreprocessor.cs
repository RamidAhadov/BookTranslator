using System.Text.RegularExpressions;

namespace BookTranslator.Utils;

public static class PdfTextPreprocessor
{
    public static string Preprocess(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        s = s.Replace("\r\n", "\n").Replace("\r", "\n");

        s = new string(s.Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').ToArray());

        // Join PDF line-break hyphenation artifacts.
        s = Regex.Replace(s, @"(\w)[\-‐‑–]\n(\w)", "$1$2");

        // Only join likely wrapped lines; keep structural line breaks for headings/TOC.
        s = Regex.Replace(s, @"(?<=[a-z0-9,;:])\n(?=[a-z])", " ");

        // Keep paragraph structure stable.
        s = Regex.Replace(s, @"\n{3,}", "\n\n");

        s = Regex.Replace(s, @"[ \t]{2,}", " ");

        // Common OCR/PDF cases where number is stuck to a word.
        s = Regex.Replace(s, @"(?<=\D)(\d{1,3})(?=[A-Za-zƏÖÜĞİŞÇəöüğışç])", "$1 ");
        s = Regex.Replace(s, @"^(\d{1,3})(?=[A-Za-zƏÖÜĞİŞÇəöüğışç])", "$1 ", RegexOptions.Multiline);

        s = Regex.Replace(s, @"([.!?])(?=[A-ZƏÖÜĞİŞÇ])", "$1 ");

        return s.Trim();
    }
}
