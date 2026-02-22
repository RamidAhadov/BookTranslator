namespace BookTranslator.Models.Layout;

public sealed record TranslatedTextItem(
    string BlockId,
    string OriginalText,
    string TranslatedText
);