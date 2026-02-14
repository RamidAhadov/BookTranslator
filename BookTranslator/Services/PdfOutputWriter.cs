using System.Text.RegularExpressions;
using BookTranslator.Options;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Hyphenation;
using iText.Layout.Properties;
using Microsoft.Extensions.Options;
using Path = System.IO.Path;

namespace BookTranslator.Services;

public sealed class PdfOutputWriter : IOutputWriter
{
    private readonly string _fontPath;

    public PdfOutputWriter(IOptions<TranslationOptions> topt)
    {
        string fontStyle = $"{topt.Value.FontStyle}.{topt.Value.FontStyleExtension}";
        _fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fontStyle);
    }

    public Task WriteAsync(string content, string path, CancellationToken ct)
    {
        var pageSize = PageSize.LETTER;

        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        using var doc = new Document(pdf, pageSize);

        var font = PdfFontFactory.CreateFont(
            _fontPath,
            PdfEncodings.IDENTITY_H,
            PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED
        );

        const float fontSize = 10.5f;
        const float leadingMultiplier = 1.2f;

        const float top = 72f;
        const float bottom = 72f;
        const float left = 79.2f;
        const float right = 64.8f;

        doc.SetMargins(top, right, bottom, left);
        doc.SetFont(font);
        doc.SetFontSize(fontSize);

        var hyphenation = new HyphenationConfig("en", "US", 3, 3);

        const float firstLineIndent = 18f;

        const float spaceBefore = 0f;
        const float spaceAfter = 0f;

        var paragraphs = splitParagraphs(content);

        foreach (var p in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(p))
                continue;

            var para = new Paragraph(p.Trim())
                .SetTextAlignment(TextAlignment.JUSTIFIED)
                .SetHyphenation(hyphenation)
                .SetFirstLineIndent(firstLineIndent)
                .SetMarginTop(spaceBefore)
                .SetMarginBottom(spaceAfter)
                .SetMultipliedLeading(leadingMultiplier);

            doc.Add(para);
        }

        doc.Close();
        return Task.CompletedTask;
    }

    private static string[] splitParagraphs(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<string>();

        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        return Regex.Split(normalized, @"\n\s*\n+");
    }
}