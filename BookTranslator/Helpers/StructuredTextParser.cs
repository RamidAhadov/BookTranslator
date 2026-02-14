using System.Text.RegularExpressions;
using BookTranslator.Models;

namespace BookTranslator.Helpers;

public static class StructuredTextParser
{
    private static readonly Regex TagLine =
        new(@"^\s*<(H1|H2|P)>\s*(.*?)\s*</\1>\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<StructuredBlock> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<StructuredBlock>();

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n');

        var blocks = new List<StructuredBlock>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var m = TagLine.Match(line);
            if (!m.Success)
            {
                blocks.Add(new StructuredBlock(BlockKind.P, line.Trim()));
                continue;
            }

            var tag = m.Groups[1].Value;
            var text = m.Groups[2].Value;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var kind = tag switch
            {
                "H1" => BlockKind.H1,
                "H2" => BlockKind.H2,
                _ => BlockKind.P
            };

            blocks.Add(new StructuredBlock(kind, text.Trim()));
        }

        return blocks;
    }
}