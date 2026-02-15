using System.Text.RegularExpressions;

namespace BookTranslator.Utils;

public static class TextSanitizer
{
    public static string Normalize(string text, bool stripExtraBlankLines)
    {
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        if (stripExtraBlankLines)
            text = Regex.Replace(text, @"\n{4,}", "\n\n\n");

        return text.Trim();
    }
    
    public static string SanitizeModelOutput(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        s = s.Replace("\r\n", "\n").Replace('\r', '\n');

        // Remove markdown fences when the model wraps output as ```html ... ```
        s = Regex.Replace(s, @"^\s*```[a-zA-Z]*\s*\n", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\n\s*```\s*$", "", RegexOptions.Singleline);

        return s.Trim();
    }
}
