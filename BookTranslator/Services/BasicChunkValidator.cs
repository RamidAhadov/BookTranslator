using BookTranslator.Options;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace BookTranslator.Services;

public sealed class BasicChunkValidator : IChunkValidator
{
    private static readonly Regex TaggedLinePattern =
        new(@"^\s*<(H1|H2|P)>\s*\S", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "of", "to", "in", "for", "is", "on", "with", "as", "that", "by", "from", "or",
        "be", "are", "at", "an", "this", "it", "not", "was", "were", "can", "if", "into"
    };

    private readonly TranslationOptions _opt;

    public BasicChunkValidator(IOptions<TranslationOptions> opt)
    {
        _opt = opt.Value;
    }

    public (bool ok, string? reason) Validate(string input, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (false, "Empty output.");

        if (output.Length < _opt.MinOutputChars)
            return (false, $"Output too short (<{_opt.MinOutputChars}).");

        var ratio = (double)output.Length / Math.Max(1, input.Length);
        if (ratio < _opt.MinOutputToInputRatio)
            return (false, $"Output/input length ratio too small ({ratio:0.00} < {_opt.MinOutputToInputRatio:0.00}).");

        if (output.Contains('\uFFFD'))
            return (false, "Output contains Unicode replacement character.");

        if (!AllNonEmptyLinesTagged(output))
            return (false, "Output format invalid: some non-empty lines do not start with <H1>/<H2>/<P>.");

        if (LooksMostlyEnglish(output))
            return (false, "Output appears to contain too much untranslated English text.");

        var lower = output.TrimStart().ToLowerInvariant();
        if (lower.StartsWith("here is") || lower.StartsWith("translation:") || lower.StartsWith("tercume:"))
            return (false, "Output contains extra commentary/prefix.");

        return (true, null);
    }

    private static bool AllNonEmptyLinesTagged(string output)
    {
        foreach (string rawLine in output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (!TaggedLinePattern.IsMatch(line))
                return false;
        }

        return true;
    }

    private static bool LooksMostlyEnglish(string output)
    {
        string plain = Regex.Replace(output, @"</?(H1|H2|P)>", " ", RegexOptions.IgnoreCase);
        MatchCollection words = Regex.Matches(plain, @"[A-Za-z']{2,}");
        if (words.Count < 80)
            return false;

        int stopWordHits = 0;
        foreach (Match m in words)
        {
            if (EnglishStopWords.Contains(m.Value))
                stopWordHits++;
        }

        double ratio = (double)stopWordHits / words.Count;
        return ratio >= 0.20;
    }
}
