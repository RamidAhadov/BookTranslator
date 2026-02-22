namespace BookTranslator.Services;

public sealed class NullOcrService : IOcrService
{
    public bool IsEnabled => false;

    public string? ExtractTextFromImages(IReadOnlyList<byte[]> images) => null;
}
