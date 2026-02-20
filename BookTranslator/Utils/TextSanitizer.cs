using System.Text.RegularExpressions;
using System.Text;

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

        return NormalizeStructuredLines(s.Trim());
    }

    private static string NormalizeStructuredLines(string s)
    {
        StringBuilder sb = new StringBuilder();
        string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.Append('\n');
                continue;
            }

            line = Regex.Replace(line, @"<\s*h1\s*>", "<H1>", RegexOptions.IgnoreCase);
            line = Regex.Replace(line, @"<\s*h2\s*>", "<H2>", RegexOptions.IgnoreCase);
            line = Regex.Replace(line, @"<\s*p\s*>", "<P>", RegexOptions.IgnoreCase);
            line = Regex.Replace(line, @"</\s*(h1|h2|p)\s*>", "", RegexOptions.IgnoreCase).Trim();

            if (!Regex.IsMatch(line, @"^<(H1|H2|P)>", RegexOptions.IgnoreCase))
                line = "<P> " + line;

            sb.AppendLine(line);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }
}
