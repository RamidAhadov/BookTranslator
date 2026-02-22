using BookTranslator.Models.Layout;

namespace BookTranslator.Services;

public interface ITranslatorService
{
    string ProviderName { get; }

    Task<IReadOnlyList<TranslatedTextItem>> TranslatePageAsync(
        PageObject page,
        string targetLanguage,
        CancellationToken ct);
}