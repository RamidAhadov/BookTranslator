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

        // normalize newlines
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');

        // remove any standalone closing tags
        s = Regex.Replace(s, @"(?m)^\s*</(P|H1|H2)>\s*$", "");

        // if the model appended a trailing closing tag without newline
        s = Regex.Replace(s, @"\s*</(P|H1|H2)>\s*$", "");

        return s.Trim();
    }
}