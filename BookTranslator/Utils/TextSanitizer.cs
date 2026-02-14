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
}