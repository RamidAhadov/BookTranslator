using BookTranslator.Options;
using BookTranslator.Helpers;
using BookTranslator.Models;
using System.Text.RegularExpressions;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Xobject;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Hyphenation;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging;
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
    private readonly TranslationOptions _translationOptions;
    private readonly ILogger<PdfOutputWriter> _log;

    public PdfOutputWriter(
        IOptions<FontOptions> fontOptions,
        IOptions<TranslationOptions> translationOptions,
        ILogger<PdfOutputWriter> log)
    {
        _fontOptions = fontOptions.Value;
        _translationOptions = translationOptions.Value;
        _log = log;
    }

    public Task WriteAsync(string content, string path, string sourcePdfPath, CancellationToken ct)
    {
        PageSize pageSize = EnumParser.ParsePageSize(_fontOptions.PageSize);
        Margins margins = _fontOptions.Margins;
        float contentWidth = pageSize.GetWidth() - margins.Left - margins.Right;
        float contentHeight = pageSize.GetHeight() - margins.Top - margins.Bottom;

        string regularPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.RegularFontStyle);
        string boldPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.BoldFontStyle);
        string codePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.CodeFontStyle);

        using PdfWriter writer = new PdfWriter(path);
        using PdfDocument pdf = new PdfDocument(writer);
        using Document doc = new Document(pdf, pageSize);

        PdfFont regular = PdfFontFactory.CreateFont(
            regularPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        PdfFont bold = PdfFontFactory.CreateFont(
            boldPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        PdfFont code = PdfFontFactory.CreateFont(
            codePath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        doc.SetMargins(margins.Top, margins.Right, margins.Bottom, margins.Left);

        HyphenationConfig hyphenation = new HyphenationConfig("en", "US", 3, 3);

        content = PdfImageMarker.NormalizeCorruptedMarkers(content);

        if (!_translationOptions.IncludeImagesInPdf)
            content = PdfImageMarker.TokenRegex.Replace(content, string.Empty);

        IReadOnlyList<StructuredBlock> blocks = StructuredTextParser.Parse(content);
        Dictionary<int, PdfImageXObjectAsset> imagesByIndex = _translationOptions.IncludeImagesInPdf
            ? loadImagesByIndex(sourcePdfPath, pdf)
            : new Dictionary<int, PdfImageXObjectAsset>();

        foreach (StructuredBlock b in blocks)
        {
            BlockKind effectiveKind = GetEffectiveKind(b);
            var matches = PdfImageMarker.TokenRegex.Matches(b.Text);

            if (matches.Count == 0)
            {
                doc.Add(buildParagraph(effectiveKind, b.Text, regular, bold, code, hyphenation));
                continue;
            }

            int cursor = 0;

            foreach (Match m in matches)
            {
                string before = b.Text[cursor..m.Index].Trim();
                if (!string.IsNullOrWhiteSpace(before))
                    doc.Add(buildParagraph(effectiveKind, before, regular, bold, code, hyphenation));

                int imageIndex = int.Parse(m.Groups[1].Value);
                if (imagesByIndex.TryGetValue(imageIndex, out PdfImageXObjectAsset? imageAsset))
                {
                    var image = new Image(imageAsset.XObject);

                    float targetWidth = imageAsset.DisplayWidth > 0 ? imageAsset.DisplayWidth : image.GetImageWidth();
                    float targetHeight = imageAsset.DisplayHeight > 0 ? imageAsset.DisplayHeight : image.GetImageHeight();

                    float widthScale = targetWidth > contentWidth ? contentWidth / targetWidth : 1f;
                    float heightScale = targetHeight > contentHeight ? contentHeight / targetHeight : 1f;
                    float scale = MathF.Min(widthScale, heightScale);

                    targetWidth *= scale;
                    targetHeight *= scale;

                    image.SetAutoScale(false);
                    image.SetWidth(targetWidth);
                    image.SetHeight(targetHeight);
                    image.SetMarginTop(_fontOptions.SpaceBefore + 2);
                    image.SetMarginBottom(_fontOptions.SpaceAfter + 4);
                    doc.Add(image);
                }
                else
                {
                    _log.LogWarning("Image marker {Marker} not found in source PDF image list.", m.Value);
                }

                cursor = m.Index + m.Length;
            }

            string after = b.Text[cursor..].Trim();
            if (!string.IsNullOrWhiteSpace(after))
                doc.Add(buildParagraph(effectiveKind, after, regular, bold, code, hyphenation));
        }

        doc.Close();
        return Task.CompletedTask;
    }

    private Paragraph buildParagraph(BlockKind kind, string text, PdfFont regular, PdfFont bold, PdfFont code, HyphenationConfig hyphenation)
    {
        switch (kind)
        {
            case BlockKind.H1:
                return new Paragraph()
                    .Add(new Text(text).SetFont(bold))
                    .SetFontSize(_fontOptions.FontSize * 1.35f)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetFirstLineIndent(0)
                    .SetMarginTop(_fontOptions.SpaceBefore + 6)
                    .SetMarginBottom(_fontOptions.SpaceAfter + 8);

            case BlockKind.H2:
                return new Paragraph()
                    .Add(new Text(text).SetFont(bold))
                    .SetFontSize(_fontOptions.FontSize * 1.12f)
                    .SetFirstLineIndent(0)
                    .SetMarginTop(_fontOptions.SpaceBefore + 4)
                    .SetMarginBottom(_fontOptions.SpaceAfter + 4);
            case BlockKind.Code:
                return new Paragraph(text)
                    .SetFont(code)
                    .SetFontSize(_fontOptions.FontSize * 0.95f)
                    .SetTextAlignment(TextAlignment.LEFT)
                    .SetFirstLineIndent(0)
                    .SetMultipliedLeading(_fontOptions.LeadingMultiplier);

            default:
                return new Paragraph(text)
                    .SetFont(regular)
                    .SetFontSize(_fontOptions.FontSize)
                    .SetHyphenation(hyphenation)
                    .SetTextAlignment(TextAlignment.JUSTIFIED)
                    .SetFirstLineIndent(_fontOptions.FirstLineIndent)
                    .SetMultipliedLeading(_fontOptions.LeadingMultiplier);
        }
    }

    private Dictionary<int, PdfImageXObjectAsset> loadImagesByIndex(string sourcePdfPath, PdfDocument targetPdf)
    {
        IReadOnlyList<PdfImageXObjectAsset> images = PdfContentFlowExtractor.ExtractImagesInOrder(
            sourcePdfPath,
            targetPdf,
            deduplicatePdfImages: _translationOptions.DeduplicatePdfImages,
            maxOccurrencesPerImageSignature: _translationOptions.MaxOccurrencesPerImageSignature,
            maxPdfImagesPerPage: _translationOptions.MaxPdfImagesPerPage,
            minPdfImageDisplayWidth: _translationOptions.MinPdfImageDisplayWidth,
            minPdfImageDisplayHeight: _translationOptions.MinPdfImageDisplayHeight,
            overlayMergeDistancePx: _translationOptions.OverlayMergeDistancePx,
            log: _log);
        Dictionary<int, PdfImageXObjectAsset> result = new Dictionary<int, PdfImageXObjectAsset>(images.Count);

        foreach (PdfImageXObjectAsset img in images)
        {
            result[img.Index] = img;
        }

        return result;
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
