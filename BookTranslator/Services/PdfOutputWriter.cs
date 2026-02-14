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
    private readonly FontOptions _fontOptions;

    public PdfOutputWriter(IOptions<FontOptions> fontOptions)
    {
        _fontOptions = fontOptions.Value;
    }

    public Task WriteAsync(string content, string path, CancellationToken ct)
    {
        var pageSize = PageSize.LETTER;
        string fontStyle = $"{_fontOptions.FontStyle}.{_fontOptions.FontStyleExtension}";
        string fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fontStyle);

        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        using var doc = new Document(pdf, pageSize);

        var font = PdfFontFactory.CreateFont(
            fontPath,
            PdfEncodings.IDENTITY_H,
            PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED
        );

        Margins margins = _fontOptions.Margins;

        doc.SetMargins(margins.Top, margins.Right, margins.Bottom, margins.Left);
        doc.SetFont(font);
        doc.SetFontSize(_fontOptions.FontSize);

        var hyphenation = new HyphenationConfig("en", "US", 3, 3);

        var paragraphs = splitParagraphs(content);

        foreach (var p in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(p))
                continue;

            var para = new Paragraph(p.Trim())
                .SetTextAlignment(TextAlignment.JUSTIFIED)
                .SetHyphenation(hyphenation)
                .SetFirstLineIndent(_fontOptions.FirstLineIndent)
                .SetMarginTop(_fontOptions.SpaceBefore)
                .SetMarginBottom(_fontOptions.SpaceAfter)
                .SetMultipliedLeading(_fontOptions.LeadingMultiplier);

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