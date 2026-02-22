namespace BookTranslator.Models.Layout;

public sealed class TextBlock : PageBlock
{
    public TextBlock(string blockId, BoundingBox boundingBox, string originalText, StyleInfo style)
        : base(blockId, boundingBox)
    {
        OriginalText = originalText;
        TranslatedText = originalText;
        Style = style;
    }

    public string OriginalText { get; }
    public string TranslatedText { get; set; }
    public StyleInfo Style { get; }
}