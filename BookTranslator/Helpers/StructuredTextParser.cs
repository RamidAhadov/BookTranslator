using System.Text;
using System.Text.RegularExpressions;
using BookTranslator.Models;

namespace BookTranslator.Helpers;

public static class StructuredTextParser
{
    private static readonly Regex OpeningTagLine =
        new(@"^\s*(?<bullet>[-*]\s+)?<(?<tag>H1|H2|P|CODE)>\s*(?<text>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ClosingTagLine =
        new(@"^\s*</(H1|H2|P|CODE)>\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex InlineTagCleanup =
        new(@"</?(H1|H2|P|CODE)>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<StructuredBlock> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<StructuredBlock>();

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<StructuredBlock>(lines.Length);
        var current = new StringBuilder();
        BlockKind? currentKind = null;

        void FlushCurrent()
        {
            if (currentKind is null)
                return;

            var text = InlineTagCleanup.Replace(current.ToString(), "").Trim();
            if (!string.IsNullOrWhiteSpace(text))
                blocks.Add(new StructuredBlock(currentKind.Value, text));

            current.Clear();
            currentKind = null;
        }

        static BlockKind ParseKind(string tag) => tag.ToUpperInvariant() switch
        {
            "H1" => BlockKind.H1,
            "H2" => BlockKind.H2,
            "CODE" => BlockKind.Code,
            _ => BlockKind.P
        };

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var closeMatch = ClosingTagLine.Match(line);
            if (closeMatch.Success)
            {
                if (currentKind is not null && ParseKind(closeMatch.Groups[1].Value) == currentKind.Value)
                    FlushCurrent();
                continue;
            }

            var openMatch = OpeningTagLine.Match(line);
            if (openMatch.Success)
            {
                FlushCurrent();

                string tag = openMatch.Groups["tag"].Value;
                BlockKind kind = ParseKind(tag);
                string bullet = openMatch.Groups["bullet"].Success ? "- " : string.Empty;
                string text = openMatch.Groups["text"].Value;

                string closeTag = $"</{tag.ToUpperInvariant()}>";
                int closeIndex = text.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
                if (closeIndex >= 0)
                {
                    string inline = (bullet + text[..closeIndex]).Trim();
                    if (!string.IsNullOrWhiteSpace(inline))
                        blocks.Add(new StructuredBlock(kind, inline));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    blocks.Add(new StructuredBlock(kind, (bullet + text).Trim()));
                    continue;
                }

                currentKind = kind;
                current.Clear();
                continue;
            }

            if (currentKind is not null)
            {
                if (current.Length > 0)
                    current.Append(' ');

                current.Append(line);
            }
            else
            {
                string cleaned = InlineTagCleanup.Replace(line, "").Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                    blocks.Add(new StructuredBlock(BlockKind.P, cleaned));
            }
        }

        FlushCurrent();
        return blocks;
    }
}
