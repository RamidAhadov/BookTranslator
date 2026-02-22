namespace BookTranslator.Models.Layout;

public sealed class ImageBlock : PageBlock
{
    public ImageBlock(string blockId, BoundingBox boundingBox, byte[] imageBytes, string mimeType)
        : base(blockId, boundingBox)
    {
        ImageBytes = imageBytes;
        MimeType = mimeType;
    }

    public byte[] ImageBytes { get; }
    public string MimeType { get; }
}