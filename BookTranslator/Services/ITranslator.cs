namespace BookTranslator.Services;

public interface ITranslator
{
    Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string cacheKey,
        CancellationToken ct);
}