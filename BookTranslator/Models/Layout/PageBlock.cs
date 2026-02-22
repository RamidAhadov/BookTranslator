namespace BookTranslator.Models.Layout;

public abstract class PageBlock
{
    protected PageBlock(string blockId, BoundingBox boundingBox)
    {
        BlockId = blockId;
        BoundingBox = boundingBox;
    }

    public string BlockId { get; }
    public BoundingBox BoundingBox { get; }
}