using System.Text.RegularExpressions;
using BookTranslator.Options;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class BasicChunkValidator : IChunkValidator
{
    private static readonly Regex TaggedLinePattern =
        new(@"^\s*<(H1|H2|P|CODE)>\s*\S", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CodeTagTitleLikePattern =
        new(@"^\s*<CODE>\s*[A-Za-z][A-Za-z0-9 &\-/]{1,80}\s*\(\d{1,4}\)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SplitLettersNoisePattern =
        new(@"(?:\b[\p{L}]\s+){3,}[\p{L}]\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            return (false, "Output format invalid: some non-empty lines do not start with <H1>/<H2>/<P>/<CODE>.");

        if (LooksMostlyEnglish(output))
            return (false, "Output appears to contain too much untranslated English text.");

        if (HasInvalidCodeTagUsage(output))
            return (false, "Output format invalid: title-like lines tagged as <CODE>.");

        if (HasBrokenWordSpacingNoise(output))
            return (false, "Output contains broken word spacing artifacts (e.g., split letters or x-noise).");

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
        string plain = Regex.Replace(output, @"</?(H1|H2|P|CODE)>", " ", RegexOptions.IgnoreCase);
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

    private static bool HasInvalidCodeTagUsage(string output)
    {
        foreach (string rawLine in output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (CodeTagTitleLikePattern.IsMatch(line))
                return true;
        }

        return false;
    }

    private static bool HasBrokenWordSpacingNoise(string output)
    {
        foreach (string rawLine in output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("<CODE>", StringComparison.OrdinalIgnoreCase))
                continue;

            if (SplitLettersNoisePattern.IsMatch(line))
                return true;

            int xNoise = Regex.Matches(line, @"\s[xX]\s").Count;
            if (xNoise >= 2)
                return true;
        }

        return false;
    }
}
