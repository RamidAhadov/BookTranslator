using BookTranslator.Models.Layout;
using BookTranslator.Options;
using BookTranslator.Utils;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class PdfLayoutAnalyzer : IPdfLayoutAnalyzer
{
    private readonly TranslationOptions _translation;
    private readonly ILogger<PdfLayoutAnalyzer> _log;

    public PdfLayoutAnalyzer(IOptions<TranslationOptions> translation, ILogger<PdfLayoutAnalyzer> log)
    {
        _translation = translation.Value;
        _log = log;
    }

    public Task<IReadOnlyList<PageObject>> AnalyzeAsync(string pdfPath, CancellationToken ct)
    {
        List<PageObject> pages = new();

        using PdfReader reader = new PdfReader(pdfPath);
        using PdfDocument pdf = new PdfDocument(reader);

        for (int pageNum = 1; pageNum <= pdf.GetNumberOfPages(); pageNum++)
        {
            ct.ThrowIfCancellationRequested();

            PdfPage page = pdf.GetPage(pageNum);
            Rectangle viewport = page.GetCropBox() ?? page.GetPageSize();
            float sourceWidth = viewport.GetWidth();
            float sourceHeight = viewport.GetHeight();
            int rotation = NormalizeRotation(page.GetRotation());

            float viewWidth = IsQuarterTurn(rotation) ? sourceHeight : sourceWidth;
            float viewHeight = IsQuarterTurn(rotation) ? sourceWidth : sourceHeight;

            LayoutEventListener listener = new(
                pageNumber: pageNum,
                viewport: viewport,
                sourcePageWidth: sourceWidth,
                sourcePageHeight: sourceHeight,
                minImageWidth: _translation.MinPdfImageDisplayWidth,
                minImageHeight: _translation.MinPdfImageDisplayHeight,
                log: _log);

            new PdfCanvasProcessor(listener).ProcessPageContent(page);

            PageObject pageObject = listener.BuildPageObject(
                width: viewWidth,
                height: viewHeight,
                sourcePageWidth: sourceWidth,
                sourcePageHeight: sourceHeight,
                rotation: rotation,
                sourcePagePdfBytes: BuildSinglePagePdf(page));

            pages.Add(pageObject);
        }

        _log.LogInformation("Layout extraction completed. Pages={Pages}", pages.Count);
        return Task.FromResult<IReadOnlyList<PageObject>>(pages);
    }

    private static bool IsQuarterTurn(int rotation) => rotation is 90 or 270;

    private static int NormalizeRotation(int rotation)
    {
        int normalized = rotation % 360;
        if (normalized < 0)
            normalized += 360;

        return normalized;
    }

    private static byte[] BuildSinglePagePdf(PdfPage sourcePage)
    {
        using MemoryStream ms = new();
        using (PdfWriter writer = new PdfWriter(ms))
        using (PdfDocument onePage = new PdfDocument(writer))
        {
            PdfPage copied = sourcePage.CopyTo(onePage);
            onePage.AddPage(copied);
        }

        return ms.ToArray();
    }

    private sealed class LayoutEventListener : IEventListener
    {
        private readonly int _pageNumber;
        private readonly float _viewportX;
        private readonly float _viewportY;
        private readonly float _sourcePageWidth;
        private readonly float _sourcePageHeight;
        private readonly float _minImageWidth;
        private readonly float _minImageHeight;
        private readonly ILogger _log;
        private readonly List<TextFragment> _textFragments = new();
        private readonly List<ImageBlock> _imageBlocks = new();

        private int _imageIndex;

        public LayoutEventListener(
            int pageNumber,
            Rectangle viewport,
            float sourcePageWidth,
            float sourcePageHeight,
            float minImageWidth,
            float minImageHeight,
            ILogger log)
        {
            _pageNumber = pageNumber;
            _viewportX = viewport.GetX();
            _viewportY = viewport.GetY();
            _sourcePageWidth = sourcePageWidth;
            _sourcePageHeight = sourcePageHeight;
            _minImageWidth = Math.Max(0f, minImageWidth);
            _minImageHeight = Math.Max(0f, minImageHeight);
            _log = log;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            switch (type)
            {
                case EventType.RENDER_TEXT:
                    HandleTextEvent(data);
                    break;
                case EventType.RENDER_IMAGE:
                    HandleImageEvent(data);
                    break;
            }
        }

        public ICollection<EventType>? GetSupportedEvents() => null;

        public PageObject BuildPageObject(
            float width,
            float height,
            float sourcePageWidth,
            float sourcePageHeight,
            int rotation,
            byte[] sourcePagePdfBytes)
        {
            List<TextBlock> blocks = MergeTextFragments();

            return new PageObject
            {
                PageNumber = _pageNumber,
                Width = width,
                Height = height,
                SourcePageWidth = sourcePageWidth,
                SourcePageHeight = sourcePageHeight,
                Rotation = rotation,
                SourcePagePdfBytes = sourcePagePdfBytes,
                TextBlocks = blocks,
                ImageBlocks = _imageBlocks
            };
        }

        private void HandleTextEvent(IEventData data)
        {
            if (data is not TextRenderInfo textInfo)
                return;

            string raw = textInfo.GetText();
            string text = TextSanitizer.CleanPdfArtifacts(raw);

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!TryBuildTextBounds(textInfo, out BoundingBox rawBox))
                return;

            if (!TryTransformToLayoutBox(rawBox, out BoundingBox box))
                return;

            StyleInfo style = ExtractStyle(textInfo);
            _textFragments.Add(new TextFragment(text, box, style));
        }

        private void HandleImageEvent(IEventData data)
        {
            if (data is not ImageRenderInfo imageInfo)
                return;

            PdfImageXObject? image = imageInfo.GetImage();
            if (image is null)
                return;

            if (!TryBuildImageBounds(imageInfo, out BoundingBox rawBox))
                return;

            if (!TryTransformToLayoutBox(rawBox, out BoundingBox box))
                return;

            if (box.Width < _minImageWidth || box.Height < _minImageHeight)
                return;

            byte[] bytes = TryReadImageBytes(image);
            if (bytes.Length == 0)
                return;

            _imageIndex++;
            string blockId = $"p{_pageNumber:D4}_img{_imageIndex:D5}";
            string mimeType = GuessMimeType(bytes);

            _imageBlocks.Add(new ImageBlock(blockId, box, bytes, mimeType));
        }

        private List<TextBlock> MergeTextFragments()
        {
            List<TextFragment> ordered = _textFragments
                .OrderBy(f => f.Box.Y)
                .ThenBy(f => f.Box.X)
                .ToList();

            if (ordered.Count == 0)
                return new List<TextBlock>();

            List<MergedLine> merged = new();
            MergedLine? current = null;

            foreach (TextFragment fragment in ordered)
            {
                if (current is null)
                {
                    current = MergedLine.From(fragment);
                    continue;
                }

                if (CanMerge(current, fragment))
                {
                    current.Append(fragment);
                    continue;
                }

                merged.Add(current);
                current = MergedLine.From(fragment);
            }

            if (current is not null)
                merged.Add(current);

            List<MergedLine> paragraphs = MergeLinesToParagraphs(merged);

            List<TextBlock> result = new(paragraphs.Count);
            int textIndex = 0;

            foreach (MergedLine paragraph in paragraphs)
            {
                textIndex++;
                string cleaned = TextSanitizer.CleanPdfArtifacts(paragraph.Text);
                if (string.IsNullOrWhiteSpace(cleaned))
                    continue;

                string blockId = $"p{_pageNumber:D4}_txt{textIndex:D5}";
                result.Add(new TextBlock(blockId, paragraph.Box, cleaned, paragraph.Style));
            }

            return result;
        }

        private static bool CanMerge(MergedLine current, TextFragment next)
        {
            if (!AreStylesCompatible(current.Style, next.Style))
                return false;

            float currentMidY = current.Box.Y + (current.Box.Height / 2f);
            float nextMidY = next.Box.Y + (next.Box.Height / 2f);
            float yDelta = MathF.Abs(currentMidY - nextMidY);
            if (yDelta > MathF.Max(2f, current.Style.FontSize * 0.45f))
                return false;

            float horizontalGap = next.Box.X - (current.Box.X + current.Box.Width);
            return horizontalGap <= MathF.Max(18f, current.Style.FontSize * 1.4f);
        }

        private static List<MergedLine> MergeLinesToParagraphs(List<MergedLine> lines)
        {
            if (lines.Count == 0)
                return lines;

            List<MergedLine> paragraphs = new();
            MergedLine? current = null;

            foreach (MergedLine line in lines)
            {
                if (current is null)
                {
                    current = line.Clone();
                    continue;
                }

                if (CanJoinParagraph(current, line))
                {
                    current.AppendParagraph(line);
                    continue;
                }

                paragraphs.Add(current);
                current = line.Clone();
            }

            if (current is not null)
                paragraphs.Add(current);

            return paragraphs;
        }

        private static bool CanJoinParagraph(MergedLine current, MergedLine next)
        {
            if (!AreStylesCompatible(current.Style, next.Style))
                return false;

            float currentBottom = current.Box.Y + current.Box.Height;
            float nextTop = next.Box.Y;
            float verticalGap = nextTop - currentBottom;
            if (verticalGap < -2f)
                return false;

            if (verticalGap > MathF.Max(12f, current.Style.FontSize * 1.6f))
                return false;

            float startDelta = MathF.Abs(current.Box.X - next.Box.X);
            return startDelta <= MathF.Max(24f, current.Style.FontSize * 2f);
        }

        private static bool AreStylesCompatible(StyleInfo a, StyleInfo b)
        {
            if (!string.Equals(a.FontName, b.FontName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (MathF.Abs(a.FontSize - b.FontSize) > 0.8f)
                return false;

            if (MathF.Abs(a.R - b.R) > 0.1f || MathF.Abs(a.G - b.G) > 0.1f || MathF.Abs(a.B - b.B) > 0.1f)
                return false;

            return true;
        }

        private static StyleInfo ExtractStyle(TextRenderInfo textInfo)
        {
            string fontName = "Unknown";
            float fontSize = MathF.Max(6f, textInfo.GetFontSize());
            float r = 0f;
            float g = 0f;
            float b = 0f;

            try
            {
                var font = textInfo.GetFont();
                if (font is not null)
                {
                    fontName = font.GetFontProgram()?.ToString() ?? fontName;
                    if (string.IsNullOrWhiteSpace(fontName))
                        fontName = "Unknown";
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                Color fill = textInfo.GetGraphicsState().GetFillColor();
                float[] components = fill.GetColorValue();

                if (components.Length >= 3)
                {
                    r = Clamp01(components[0]);
                    g = Clamp01(components[1]);
                    b = Clamp01(components[2]);
                }
                else if (components.Length == 1)
                {
                    float gray = Clamp01(components[0]);
                    r = gray;
                    g = gray;
                    b = gray;
                }
            }
            catch
            {
                // ignored
            }

            bool bold = fontName.Contains("bold", StringComparison.OrdinalIgnoreCase);
            bool italic = fontName.Contains("italic", StringComparison.OrdinalIgnoreCase) ||
                          fontName.Contains("oblique", StringComparison.OrdinalIgnoreCase);

            return new StyleInfo(fontName, fontSize, r, g, b, bold, italic);
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        private static bool TryBuildTextBounds(TextRenderInfo info, out BoundingBox box)
        {
            box = new BoundingBox(0, 0, 0, 0);

            try
            {
                LineSegment asc = info.GetAscentLine();
                LineSegment desc = info.GetDescentLine();

                Vector a1 = asc.GetStartPoint();
                Vector a2 = asc.GetEndPoint();
                Vector d1 = desc.GetStartPoint();
                Vector d2 = desc.GetEndPoint();

                float minX = MathF.Min(MathF.Min(a1.Get(Vector.I1), a2.Get(Vector.I1)), MathF.Min(d1.Get(Vector.I1), d2.Get(Vector.I1)));
                float maxX = MathF.Max(MathF.Max(a1.Get(Vector.I1), a2.Get(Vector.I1)), MathF.Max(d1.Get(Vector.I1), d2.Get(Vector.I1)));
                float minY = MathF.Min(MathF.Min(a1.Get(Vector.I2), a2.Get(Vector.I2)), MathF.Min(d1.Get(Vector.I2), d2.Get(Vector.I2)));
                float maxY = MathF.Max(MathF.Max(a1.Get(Vector.I2), a2.Get(Vector.I2)), MathF.Max(d1.Get(Vector.I2), d2.Get(Vector.I2)));

                float width = MathF.Max(0f, maxX - minX);
                float height = MathF.Max(0f, maxY - minY);

                if (width <= 0f || height <= 0f)
                    return false;

                box = new BoundingBox(minX, minY, width, height);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildImageBounds(ImageRenderInfo info, out BoundingBox box)
        {
            Matrix ctm = info.GetImageCtm();

            float a = ctm.Get(Matrix.I11);
            float b = ctm.Get(Matrix.I12);
            float c = ctm.Get(Matrix.I21);
            float d = ctm.Get(Matrix.I22);

            float displayWidth = MathF.Sqrt(a * a + b * b);
            float displayHeight = MathF.Sqrt(c * c + d * d);

            float x = ctm.Get(Matrix.I31);
            float y = ctm.Get(Matrix.I32);

            box = new BoundingBox(x, y, MathF.Abs(displayWidth), MathF.Abs(displayHeight));
            return box.Width > 0 && box.Height > 0;
        }

        private bool TryTransformToLayoutBox(BoundingBox rawBox, out BoundingBox layoutBox)
        {
            layoutBox = new BoundingBox(0, 0, 0, 0);

            if (_sourcePageWidth <= 0 || _sourcePageHeight <= 0)
                return false;

            TransformCandidate? shifted = TryBuildCandidate(
                rawBox.X - _viewportX,
                rawBox.Y - _viewportY,
                rawBox.Width,
                rawBox.Height);

            TransformCandidate? direct = TryBuildCandidate(
                rawBox.X,
                rawBox.Y,
                rawBox.Width,
                rawBox.Height);

            TransformCandidate? chosen = ChooseCandidate(shifted, direct);
            if (chosen is null)
                return false;

            // Store Y in top-based coordinates so reconstruction can map back with:
            // outputBottomY = sourcePageHeight - inputTopY - height.
            float transformedY = _sourcePageHeight - chosen.Value.Top;
            layoutBox = new BoundingBox(chosen.Value.Left, transformedY, chosen.Value.Width, chosen.Value.Height);
            return true;
        }

        private TransformCandidate? ChooseCandidate(TransformCandidate? shifted, TransformCandidate? direct)
        {
            if (shifted is null) return direct;
            if (direct is null) return shifted;

            float coverageDelta = shifted.Value.Coverage - direct.Value.Coverage;
            if (MathF.Abs(coverageDelta) > 0.0001f)
                return coverageDelta > 0 ? shifted : direct;

            // If CropBox origin is offset and both candidates are equally valid,
            // prefer the offset-normalized coordinates to keep output aligned to the
            // visible page view.
            if (MathF.Abs(_viewportX) > 0.001f || MathF.Abs(_viewportY) > 0.001f)
                return shifted;

            return direct;
        }

        private TransformCandidate? TryBuildCandidate(float left, float bottom, float width, float height)
        {
            if (width <= 0f || height <= 0f)
                return null;

            float right = left + width;
            float top = bottom + height;

            float clippedLeft = MathF.Max(0f, left);
            float clippedBottom = MathF.Max(0f, bottom);
            float clippedRight = MathF.Min(_sourcePageWidth, right);
            float clippedTop = MathF.Min(_sourcePageHeight, top);

            if (clippedRight <= clippedLeft || clippedTop <= clippedBottom)
                return null;

            float clippedWidth = clippedRight - clippedLeft;
            float clippedHeight = clippedTop - clippedBottom;

            float rawArea = width * height;
            float clippedArea = clippedWidth * clippedHeight;
            float coverage = rawArea > 0f ? clippedArea / rawArea : 0f;

            return new TransformCandidate(
                clippedLeft,
                clippedBottom,
                clippedWidth,
                clippedHeight,
                clippedTop,
                coverage);
        }

        private byte[] TryReadImageBytes(PdfImageXObject image)
        {
            try
            {
                byte[] direct = image.GetImageBytes(true);
                if (direct.Length > 0)
                    return direct;
            }
            catch
            {
                // ignored
            }

            try
            {
                byte[] raw = image.GetPdfObject().GetBytes();
                if (raw.Length > 0)
                    return raw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Page {Page}: failed to read image bytes.", _pageNumber);
            }

            return Array.Empty<byte>();
        }

        private static string GuessMimeType(byte[] payload)
        {
            if (payload.Length >= 8 && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
                return "image/png";

            if (payload.Length >= 3 && payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF)
                return "image/jpeg";

            if (payload.Length >= 2 && payload[0] == 0x42 && payload[1] == 0x4D)
                return "image/bmp";

            if (payload.Length >= 4 &&
                ((payload[0] == 0x49 && payload[1] == 0x49 && payload[2] == 0x2A && payload[3] == 0x00) ||
                 (payload[0] == 0x4D && payload[1] == 0x4D && payload[2] == 0x00 && payload[3] == 0x2A)))
                return "image/tiff";

            return "application/octet-stream";
        }

        private readonly record struct TextFragment(string Text, BoundingBox Box, StyleInfo Style);
        private readonly record struct TransformCandidate(
            float Left,
            float Bottom,
            float Width,
            float Height,
            float Top,
            float Coverage);

        private sealed class MergedLine
        {
            private MergedLine(BoundingBox box, string text, StyleInfo style)
            {
                Box = box;
                Text = text;
                Style = style;
            }

            public BoundingBox Box { get; private set; }
            public string Text { get; private set; }
            public StyleInfo Style { get; }

            public static MergedLine From(TextFragment fragment)
            {
                return new MergedLine(fragment.Box, fragment.Text, fragment.Style);
            }

            public MergedLine Clone()
            {
                return new MergedLine(Box, Text, Style);
            }

            public void Append(TextFragment fragment)
            {
                float newX = MathF.Min(Box.X, fragment.Box.X);
                float newY = MathF.Min(Box.Y, fragment.Box.Y);
                float newRight = MathF.Max(Box.Right, fragment.Box.Right);
                float newTop = MathF.Max(Box.Top, fragment.Box.Top);

                bool addSpace = NeedsSpace(Text, fragment.Text);
                Text = addSpace ? $"{Text} {fragment.Text}" : Text + fragment.Text;
                Box = new BoundingBox(newX, newY, newRight - newX, newTop - newY);
            }

            public void AppendParagraph(MergedLine line)
            {
                float newX = MathF.Min(Box.X, line.Box.X);
                float newY = MathF.Min(Box.Y, line.Box.Y);
                float newRight = MathF.Max(Box.Right, line.Box.Right);
                float newTop = MathF.Max(Box.Top, line.Box.Top);

                bool addSpace = NeedsSpace(Text, line.Text);
                Text = addSpace ? $"{Text} {line.Text}" : Text + line.Text;
                Box = new BoundingBox(newX, newY, newRight - newX, newTop - newY);
            }

            private static bool NeedsSpace(string left, string right)
            {
                if (left.Length == 0 || right.Length == 0)
                    return false;

                char l = left[^1];
                char r = right[0];

                if (char.IsWhiteSpace(l) || char.IsWhiteSpace(r))
                    return false;

                if (char.IsPunctuation(l))
                    return true;

                return char.IsLetterOrDigit(l) && char.IsLetterOrDigit(r);
            }
        }
    }
}
