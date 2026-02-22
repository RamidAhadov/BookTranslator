namespace BookTranslator.Models.Layout;

public sealed class PageObject
{
    public required int PageNumber { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public float SourcePageWidth { get; init; }
    public float SourcePageHeight { get; init; }
    public int Rotation { get; init; }
    public required byte[] SourcePagePdfBytes { get; init; }

    public List<TextBlock> TextBlocks { get; init; } = new();
    public List<ImageBlock> ImageBlocks { get; init; } = new();
}
