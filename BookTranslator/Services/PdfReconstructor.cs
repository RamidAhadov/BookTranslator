using BookTranslator.Models.Layout;
using BookTranslator.Options;
using BookTranslator.Utils;
using System.Text;
using iText.IO.Font;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Path = System.IO.Path;

namespace BookTranslator.Services;

public sealed class PdfReconstructor
{
    private readonly TranslationOptions _translation;
    private readonly FontOptions _fontOptions;
    private readonly ILogger<PdfReconstructor> _log;

    public PdfReconstructor(
        IOptions<TranslationOptions> translation,
        IOptions<FontOptions> fontOptions,
        ILogger<PdfReconstructor> log)
    {
        _translation = translation.Value;
        _fontOptions = fontOptions.Value;
        _log = log;
    }

    public Task ReconstructAsync(IReadOnlyList<PageObject> pages, string outputPath, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string regularPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.RegularFontStyle);
        string boldPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.BoldFontStyle);
        string italicPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", _fontOptions.ItalicFontStyle);

        using PdfWriter writer = new PdfWriter(outputPath);
        using PdfDocument pdf = new PdfDocument(writer);

        PdfFont regularFont = PdfFontFactory.CreateFont(
            regularPath,
            PdfEncodings.IDENTITY_H,
            PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        PdfFont boldFont = PdfFontFactory.CreateFont(
            boldPath,
            PdfEncodings.IDENTITY_H,
            PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        PdfFont italicFont = PdfFontFactory.CreateFont(
            italicPath,
            PdfEncodings.IDENTITY_H,
            PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

        foreach (PageObject pageObject in pages.OrderBy(x => x.PageNumber))
        {
            ct.ThrowIfCancellationRequested();

            float sourcePageWidth = pageObject.SourcePageWidth > 0 ? pageObject.SourcePageWidth : pageObject.Width;
            float sourcePageHeight = pageObject.SourcePageHeight > 0 ? pageObject.SourcePageHeight : pageObject.Height;

            PageSize pageSize = new PageSize(sourcePageWidth, sourcePageHeight);
            PdfPage page = pdf.AddNewPage(pageSize);
            if (pageObject.Rotation is 0 or 90 or 180 or 270)
                page.SetRotation(pageObject.Rotation);

            PdfCanvas canvas = new PdfCanvas(page);

            foreach (ImageBlock image in pageObject.ImageBlocks)
                DrawImage(canvas, image, sourcePageHeight);

            IEnumerable<TextBlock> orderedText = pageObject.TextBlocks
                .OrderBy(x => x.BoundingBox.Y)
                .ThenBy(x => x.BoundingBox.X);

            foreach (TextBlock textBlock in orderedText)
            {
                PdfFont font = SelectFont(textBlock.Style, regularFont, boldFont, italicFont);
                DrawTextBlock(canvas, textBlock, font, sourcePageHeight);
            }

            _log.LogInformation(
                "Reconstructed page {Page}: textBlocks={TextCount}, imageBlocks={ImageCount}",
                pageObject.PageNumber,
                pageObject.TextBlocks.Count,
                pageObject.ImageBlocks.Count);
        }

        pdf.Close();
        return Task.CompletedTask;
    }

    private void DrawImage(PdfCanvas canvas, ImageBlock image, float sourcePageHeight)
    {
        try
        {
            ImageData data = ImageDataFactory.Create(image.ImageBytes);
            Rectangle rect = ToRectangle(image.BoundingBox, sourcePageHeight);
            canvas.AddImageFittedIntoRectangle(data, rect, false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to draw image block {BlockId}", image.BlockId);
        }
    }

    private void DrawTextBlock(PdfCanvas canvas, TextBlock block, PdfFont font, float sourcePageHeight)
    {
        BoundingBox box = block.BoundingBox;
        if (box.Width <= 0 || box.Height <= 0)
            return;

        float bottomY = sourcePageHeight - box.Y - box.Height;
        if (bottomY + box.Height < 0)
            return;

        string text = TextSanitizer.CleanPdfArtifacts(block.TranslatedText);
        if (string.IsNullOrWhiteSpace(text))
            text = TextSanitizer.CleanPdfArtifacts(block.OriginalText);

        if (string.IsNullOrWhiteSpace(text))
            return;

        float leading = MathF.Max(1.05f, _fontOptions.LeadingMultiplier);
        float requestedSize = block.Style.FontSize > 0 ? block.Style.FontSize : _fontOptions.FontSize;
        float minSize = MathF.Max(4f, _translation.MinAutoFontSize);
        float step = MathF.Max(0.1f, _translation.FontScaleStep);

        float finalSize = _translation.EnableDynamicFontScaling
            ? FindFittingFontSize(font, text, box.Width, box.Height, requestedSize, minSize, step, leading)
            : requestedSize;

        List<string> lines = WrapText(font, text, finalSize, box.Width);
        float lineHeight = finalSize * leading;
        int maxLines = Math.Max(1, (int)MathF.Floor(box.Height / lineHeight));
        if (lines.Count > maxLines)
            lines = lines.Take(maxLines).ToList();

        if (_translation.ClearOriginalTextArea)
        {
            canvas.SaveState();
            canvas.SetFillColor(ColorConstants.WHITE);
            canvas.Rectangle(box.X, bottomY, box.Width, box.Height);
            canvas.Fill();
            canvas.RestoreState();
        }

        DeviceRgb color = ToDeviceRgb(block.Style);

        canvas.BeginText();
        canvas.SetFontAndSize(font, finalSize);
        canvas.SetFillColor(color);

        float y = bottomY + box.Height - finalSize;

        foreach (string line in lines)
        {
            if (y < bottomY)
                break;

            canvas.SetTextMatrix(box.X, y);
            canvas.ShowText(line);
            y -= lineHeight;
        }

        canvas.EndText();
    }

    private static float FindFittingFontSize(
        PdfFont font,
        string text,
        float width,
        float height,
        float requestedSize,
        float minSize,
        float step,
        float leading)
    {
        float size = requestedSize;

        while (size >= minSize)
        {
            List<string> lines = WrapText(font, text, size, width);
            float neededHeight = lines.Count * size * leading;

            if (neededHeight <= height)
                return size;

            size -= step;
        }

        return minSize;
    }

    private static List<string> WrapText(PdfFont font, string text, float fontSize, float maxWidth)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] paragraphs = normalized.Split('\n');

        List<string> lines = new();

        foreach (string paragraph in paragraphs)
        {
            string trimmed = paragraph.Trim();
            if (trimmed.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string current = string.Empty;

            foreach (string word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (MeasureWidth(font, candidate, fontSize) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                    lines.Add(current);

                if (MeasureWidth(font, word, fontSize) <= maxWidth)
                {
                    current = word;
                    continue;
                }

                foreach (string piece in BreakLongWord(font, word, fontSize, maxWidth))
                    lines.Add(piece);

                current = string.Empty;
            }

            if (current.Length > 0)
                lines.Add(current);
        }

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }

    private static IEnumerable<string> BreakLongWord(PdfFont font, string word, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(word))
            yield break;

        StringBuilder current = new();

        foreach (char c in word)
        {
            string next = current.ToString() + c;
            if (current.Length > 0 && MeasureWidth(font, next, fontSize) > maxWidth)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(c);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static float MeasureWidth(PdfFont font, string text, float fontSize)
    {
        float baseWidth = font.GetWidth(text);
        return (baseWidth / 1000f) * fontSize;
    }

    private static PdfFont SelectFont(StyleInfo style, PdfFont regular, PdfFont bold, PdfFont italic)
    {
        if (style.Bold)
            return bold;

        if (style.Italic)
            return italic;

        return regular;
    }

    private static DeviceRgb ToDeviceRgb(StyleInfo style)
    {
        int r = (int)MathF.Round(Clamp(style.R) * 255f);
        int g = (int)MathF.Round(Clamp(style.G) * 255f);
        int b = (int)MathF.Round(Clamp(style.B) * 255f);

        return new DeviceRgb(r, g, b);
    }

    private static float Clamp(float value)
    {
        if (value < 0f) return 0f;
        if (value > 1f) return 1f;
        return value;
    }

    private static Rectangle ToRectangle(BoundingBox box, float sourcePageHeight)
    {
        float y = sourcePageHeight - box.Y - box.Height;
        return new Rectangle(box.X, y, box.Width, box.Height);
    }
}
