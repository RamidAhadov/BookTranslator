using BookTranslator.Options;
using BookTranslator.Helpers;
using BookTranslator.Models;
using System.Text.RegularExpressions;
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
    private static readonly Regex TocDotsPattern =
        new(@"\.{2,}\s*\d+\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericOnlyPattern =
        new(@"^[\d\s\.,:/\-()]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LooksLikeTocItemPattern =
        new(@"^\s*(\d+(\.\d+)*)?\s*[A-Za-z].*\s\d+\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FontOptions _fontOptions;

    public PdfOutputWriter(IOptions<FontOptions> fontOptions)
    {
        _fontOptions = fontOptions.Value;
    }

    public Task WriteAsync(string content, string path, CancellationToken ct)
    {
        PageSize pageSize = EnumParser.ParsePageSize(_fontOptions.PageSize);
        Margins margins = _fontOptions.Margins;

        string regularPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.RegularFontStyle);
        string boldPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.BoldFontStyle);

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
            BlockKind effectiveKind = GetEffectiveKind(b);

            switch (effectiveKind)
            {
                case BlockKind.H1:
                    para = new Paragraph()
                        .Add(new Text(b.Text).SetFont(bold))
                        .SetFontSize(_fontOptions.FontSize * 1.35f)
                        .SetTextAlignment(TextAlignment.CENTER)
                        .SetFirstLineIndent(0)
                        .SetMarginTop(_fontOptions.SpaceBefore + 6)
                        .SetMarginBottom(_fontOptions.SpaceAfter + 8);
                    break;

                case BlockKind.H2:
                    para = new Paragraph()
                        .Add(new Text(b.Text).SetFont(bold))
                        .SetFontSize(_fontOptions.FontSize * 1.12f)
                        .SetFirstLineIndent(0)
                        .SetMarginTop(_fontOptions.SpaceBefore + 4)
                        .SetMarginBottom(_fontOptions.SpaceAfter + 4);
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

    private static BlockKind GetEffectiveKind(StructuredBlock block)
    {
        string text = block.Text.Trim();

        if (block.Kind is BlockKind.H1 or BlockKind.H2)
        {
            if (text.StartsWith("- ", StringComparison.Ordinal))
                return BlockKind.P;

            if (string.IsNullOrWhiteSpace(text) || text.Length > 120)
                return BlockKind.P;

            if (NumericOnlyPattern.IsMatch(text))
                return BlockKind.P;

            if (TocDotsPattern.IsMatch(text) || LooksLikeTocItemPattern.IsMatch(text))
                return BlockKind.P;

            if (text.Contains("table of contents", StringComparison.OrdinalIgnoreCase))
                return BlockKind.H2;
        }

        return block.Kind;
    }
}
