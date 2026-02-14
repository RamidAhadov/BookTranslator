namespace BookTranslator.Options;

public class FontOptions
{
    public string FontStyle { get; set; }
    public string FontStyleExtension { get; set; }
    public float FontSize { get; set; }
    public float LeadingMultiplier { get; set; }
    public Margins Margins { get; set; }
    public float FirstLineIndent { get; set; }
    public float SpaceBefore { get; set; }
    public float SpaceAfter { get; set; }
}

public class Margins
{
    public float Top { get; set; }
    public float Bottom { get; set; }
    public float Left { get; set; }
    public float Right { get; set; }
}