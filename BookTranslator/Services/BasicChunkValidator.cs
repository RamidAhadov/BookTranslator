using BookTranslator.Options;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class BasicChunkValidator : IChunkValidator
{
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
            return (false, "Output contains Unicode replacement character (�).");

        var lower = output.TrimStart().ToLowerInvariant();
        if (lower.StartsWith("here is") || lower.StartsWith("translation:") || lower.StartsWith("tərcümə:"))
            return (false, "Output contains extra commentary/prefix.");

        return (true, null);
    }
}