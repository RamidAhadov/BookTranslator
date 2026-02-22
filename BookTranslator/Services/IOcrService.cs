namespace BookTranslator.Services;

public interface IOcrService
{
    bool IsEnabled { get; }

    string? ExtractTextFromImages(IReadOnlyList<byte[]> images);
}
