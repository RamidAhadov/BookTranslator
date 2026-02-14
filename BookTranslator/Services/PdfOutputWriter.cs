using BookTranslator.Options;
using BookTranslator.Helpers;
using BookTranslator.Models;
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
        PageSize pageSize = EnumParser.ParsePageSize(_fontOptions.PageSize);
        Margins margins = _fontOptions.Margins;

        string regularPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts",
            $"{_fontOptions.ParagraphFontStyle}");

        string boldPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts",
            $"{_fontOptions.HeaderFontStyle}");

        using PdfWriter writer = new PdfWriter(path);
        using PdfDocument pdf = new PdfDocument(writer);
        using Document doc = new Document(pdf, pageSize);

        PdfFont regular = PdfFontFactory.CreateFont(
            regularPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        PdfFont bold = PdfFontFactory.CreateFont(
            boldPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        doc.SetMargins(margins.Top, margins.Right, margins.Bottom, margins.Left);

        HyphenationConfig hyphenation = new HyphenationConfig("en", "US", 3, 3);

        IReadOnlyList<StructuredBlock> blocks = StructuredTextParser.Parse(content);

        foreach (StructuredBlock b in blocks)
        {
            Paragraph para;

            switch (b.Kind)
            {
                case BlockKind.H1:
                    para = new Paragraph()
                        .Add(new Text(b.Text).SetFont(bold))
                        .SetFontSize(_fontOptions.FontSize * 1.6f)
                        .SetTextAlignment(TextAlignment.CENTER)
                        .SetFirstLineIndent(0)
                        .SetMarginTop(_fontOptions.SpaceBefore + 8)
                        .SetMarginBottom(_fontOptions.SpaceAfter + 10);
                    break;

                case BlockKind.H2:
                    para = new Paragraph()
                        .Add(new Text(b.Text).SetFont(bold))
                        .SetFontSize(_fontOptions.FontSize * 1.3f)
                        .SetFirstLineIndent(0)
                        .SetMarginTop(_fontOptions.SpaceBefore + 6)
                        .SetMarginBottom(_fontOptions.SpaceAfter + 6);
                    break;

                default:
                    para = new Paragraph(b.Text)
                        .SetFont(regular)
                        .SetFontSize(_fontOptions.FontSize)
                        .SetHyphenation(hyphenation)
                        .SetTextAlignment(TextAlignment.JUSTIFIED)
                        .SetFirstLineIndent(_fontOptions.FirstLineIndent)
                        .SetMultipliedLeading(_fontOptions.LeadingMultiplier);
                    break;
            }

            doc.Add(para);
        }

        doc.Close();
        return Task.CompletedTask;
    }
}
