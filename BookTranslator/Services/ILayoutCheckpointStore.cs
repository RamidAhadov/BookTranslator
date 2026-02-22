using BookTranslator.Models.Layout;

namespace BookTranslator.Services;

public sealed record LayoutCheckpointReadResult(bool Hit, IReadOnlyList<TranslatedTextItem> Items);

public interface ILayoutCheckpointStore
{
    Task InitializeAsync(
        string sourcePdfPath,
        string targetLanguage,
        string providerName,
        CancellationToken ct);

    Task<LayoutCheckpointReadResult> TryReadPageAsync(
        int pageNumber,
        string pageFingerprint,
        CancellationToken ct);

    Task SavePageSuccessAsync(
        int pageNumber,
        string pageFingerprint,
        IReadOnlyList<TranslatedTextItem> items,
        CancellationToken ct);

    Task SavePageFailureAsync(
        int pageNumber,
        string pageFingerprint,
        string error,
        CancellationToken ct);
}
