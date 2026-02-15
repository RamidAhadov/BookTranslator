namespace BookTranslator.Options;

public class FontOptions
{
    public string PageSize { get; set; }
    public string BoldFontStyle { get; set; }
    public string RegularFontStyle { get; set; }
    public string ItalicFontStyle { get; set; }
    public string CodeFontStyle { get; set; }
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