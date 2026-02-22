namespace BookTranslator.Models.Layout;

public sealed record StyleInfo(
    string FontName,
    float FontSize,
    float R,
    float G,
    float B,
    bool Bold,
    bool Italic
);