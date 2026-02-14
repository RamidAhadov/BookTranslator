using System.Text.RegularExpressions;

namespace BookTranslator.Utils;

public static class PdfTextPreprocessor
{
    public static string Preprocess(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        // Normalize newlines
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");

        // Remove control chars except \n \t
        s = new string(s.Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').ToArray());

        // Fix hyphenation across line breaks: "certifi-\ncate" or "cer‐\ntificate"
        s = Regex.Replace(s, @"(\w)[‐-–-]\n(\w)", "$1$2");

        // Keep paragraph breaks, but join single newlines inside paragraphs
        // \n\n stays; single \n becomes space
        s = Regex.Replace(s, @"(?<!\n)\n(?!\n)", " ");

        // Collapse 3+ newlines to 2 (optional, keeps paragraph structure clean)
        s = Regex.Replace(s, @"\n{3,}", "\n\n");

        // Cleanup extra spaces
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        
        s = Regex.Replace(s, @"(?<=\D)(\d{1,3})(?=[A-Za-zƏÖÜĞİŞÇəöüğişç])", "$1 ");
        s = Regex.Replace(s, @"^(\d{1,3})(?=[A-Za-zƏÖÜĞİŞÇəöüğişç])", "$1 ", RegexOptions.Multiline);
        
        s = Regex.Replace(s, @"([.!?])(?=[A-ZƏÖÜĞİŞÇ])", "$1 ");

        return s.Trim();
    }
}