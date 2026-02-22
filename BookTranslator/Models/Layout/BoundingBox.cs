namespace BookTranslator.Models.Layout;

public sealed record BoundingBox(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Top => Y + Height;
}