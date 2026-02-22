using System.Text;
using System.Text.RegularExpressions;

namespace BookTranslator.Utils;

public static class TextSanitizer
{
    private static readonly Regex TaggedLineParts =
        new(@"^<(?<tag>H1|H2|P|CODE)>\s*(?<text>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SpacedLettersPattern =
        new(@"\b(?:[\p{L}]\s+){2,}[\p{L}]\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IsolatedXNoisePattern =
        new(@"(?<=[\p{L}])\s+[xX]\s+(?=[\p{L}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ArtifactTokenPattern =
        new(@"(^|\s)(ptg|x)(?=\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex JoinedXArtifactPattern =
        new(@"\b[xX](?=[\p{Lu}]{4,}\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MultiSpacePattern =
        new(@"[ \t]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LowerUpperBoundaryPattern =
        new(@"(?<=[\p{Ll}])(?=[\p{Lu}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PunctuationSpacingPattern =
        new(@"\s+([,.;:!?])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OpenBracketSpacingPattern =
        new(@"([(\[{])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CloseBracketSpacingPattern =
        new(@"\s+([)\]}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string CleanPdfArtifacts(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = new string(normalized.Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').ToArray());

        normalized = SpacedLettersPattern.Replace(normalized, m => m.Value.Replace(" ", ""));
        normalized = IsolatedXNoisePattern.Replace(normalized, " ");
        normalized = ArtifactTokenPattern.Replace(normalized, " ");
        normalized = JoinedXArtifactPattern.Replace(normalized, "");
        normalized = SplitLikelyConcatenatedWords(normalized);
        normalized = PunctuationSpacingPattern.Replace(normalized, "$1");
        normalized = OpenBracketSpacingPattern.Replace(normalized, "$1");
        normalized = CloseBracketSpacingPattern.Replace(normalized, "$1");
        normalized = MultiSpacePattern.Replace(normalized, " ");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");

        return normalized.Trim();
    }

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
            line = Regex.Replace(line, @"<\s*code\s*>", "<CODE>", RegexOptions.IgnoreCase);
            line = Regex.Replace(line, @"</\s*(h1|h2|p|code)\s*>", "", RegexOptions.IgnoreCase).Trim();

            if (!Regex.IsMatch(line, @"^<(H1|H2|P|CODE)>", RegexOptions.IgnoreCase))
                line = "<P> " + line;

            line = NormalizeLineContent(line);

            sb.AppendLine(line);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeLineContent(string line)
    {
        Match m = TaggedLineParts.Match(line);
        if (!m.Success)
            return line;

        string tag = m.Groups["tag"].Value.ToUpperInvariant();
        string text = m.Groups["text"].Value;

        if (tag != "CODE")
        {
            text = IsolatedXNoisePattern.Replace(text, " ");
            text = SpacedLettersPattern.Replace(text, mm => mm.Value.Replace(" ", ""));
            text = MultiSpacePattern.Replace(text, " ").Trim();
        }

        return $"<{tag}> {text}".TrimEnd();
    }

    private static string SplitLikelyConcatenatedWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string[] tokens = text.Split(' ', StringSplitOptions.None);
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token.Length < 10)
                continue;

            if (!token.Any(char.IsLower) || !token.Any(char.IsUpper))
                continue;

            int boundaryCount = LowerUpperBoundaryPattern.Matches(token).Count;
            if (boundaryCount == 0)
                continue;

            if (boundaryCount == 1 && token.Length < 12)
                continue;

            tokens[i] = LowerUpperBoundaryPattern.Replace(token, " ");
        }

        return string.Join(" ", tokens);
    }
}
