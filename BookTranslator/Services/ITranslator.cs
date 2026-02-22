namespace BookTranslator.Services;

public interface ITranslator
{
    Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string cacheKey,
        string? modelOverride,
        string? attemptFingerprint,
        CancellationToken ct);
}
