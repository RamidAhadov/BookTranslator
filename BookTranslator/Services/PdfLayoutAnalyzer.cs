using BookTranslator.Models.Layout;
using BookTranslator.Options;
using BookTranslator.Utils;
using iText.IO.Image;
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
                includeImages: _translation.IncludeImagesInPdf,
                includeInvisibleTextLayer: _translation.IncludeInvisibleTextLayer,
                useInvisibleTextAsFallbackOnly: _translation.UseInvisibleTextAsFallbackOnly,
                invisibleFallbackMinVisibleTextChars: _translation.InvisibleFallbackMinVisibleTextChars,
                invisibleFallbackMinVisibleFragments: _translation.InvisibleFallbackMinVisibleFragments,
                minVisibleCharsPerFragmentForStrongLayer: _translation.MinVisibleCharsPerFragmentForStrongLayer,
                mergeLinesIntoParagraphs: _translation.MergeLinesIntoParagraphs,
                minImageWidth: _translation.MinPdfImageDisplayWidth,
                minImageHeight: _translation.MinPdfImageDisplayHeight,
                suppressBackgroundPageImages: _translation.SuppressBackgroundPageImages,
                backgroundImageMinPageCoverage: _translation.BackgroundImageMinPageCoverage,
                maxKeptImageCoverageOnTextPages: _translation.MaxKeptImageCoverageOnTextPages,
                backgroundImageMinTextBlocks: _translation.BackgroundImageMinTextBlocks,
                backgroundImageMinTextChars: _translation.BackgroundImageMinTextChars,
                backgroundImageEdgeTolerance: _translation.BackgroundImageEdgeTolerance,
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
        private readonly bool _includeImages;
        private readonly bool _includeInvisibleTextLayer;
        private readonly bool _useInvisibleTextAsFallbackOnly;
        private readonly int _invisibleFallbackMinVisibleTextChars;
        private readonly int _invisibleFallbackMinVisibleFragments;
        private readonly float _minVisibleCharsPerFragmentForStrongLayer;
        private readonly bool _mergeLinesIntoParagraphs;
        private readonly float _minImageWidth;
        private readonly float _minImageHeight;
        private readonly bool _suppressBackgroundPageImages;
        private readonly float _backgroundImageMinPageCoverage;
        private readonly float _maxKeptImageCoverageOnTextPages;
        private readonly int _backgroundImageMinTextBlocks;
        private readonly int _backgroundImageMinTextChars;
        private readonly float _backgroundImageEdgeTolerance;
        private readonly ILogger _log;
        private readonly Dictionary<string, TextFragment> _visibleTextFragmentsBySignature = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TextFragment> _invisibleTextFragmentsBySignature = new(StringComparer.Ordinal);
        private readonly List<ImageBlock> _imageBlocks = new();

        private int _imageIndex;

        public LayoutEventListener(
            int pageNumber,
            Rectangle viewport,
            float sourcePageWidth,
            float sourcePageHeight,
            bool includeImages,
            bool includeInvisibleTextLayer,
            bool useInvisibleTextAsFallbackOnly,
            int invisibleFallbackMinVisibleTextChars,
            int invisibleFallbackMinVisibleFragments,
            float minVisibleCharsPerFragmentForStrongLayer,
            bool mergeLinesIntoParagraphs,
            float minImageWidth,
            float minImageHeight,
            bool suppressBackgroundPageImages,
            float backgroundImageMinPageCoverage,
            float maxKeptImageCoverageOnTextPages,
            int backgroundImageMinTextBlocks,
            int backgroundImageMinTextChars,
            float backgroundImageEdgeTolerance,
            ILogger log)
        {
            _pageNumber = pageNumber;
            _viewportX = viewport.GetX();
            _viewportY = viewport.GetY();
            _sourcePageWidth = sourcePageWidth;
            _sourcePageHeight = sourcePageHeight;
            _includeImages = includeImages;
            _includeInvisibleTextLayer = includeInvisibleTextLayer;
            _useInvisibleTextAsFallbackOnly = useInvisibleTextAsFallbackOnly;
            _invisibleFallbackMinVisibleTextChars = Math.Max(0, invisibleFallbackMinVisibleTextChars);
            _invisibleFallbackMinVisibleFragments = Math.Max(0, invisibleFallbackMinVisibleFragments);
            _minVisibleCharsPerFragmentForStrongLayer = Math.Max(0.5f, minVisibleCharsPerFragmentForStrongLayer);
            _mergeLinesIntoParagraphs = mergeLinesIntoParagraphs;
            _minImageWidth = Math.Max(0f, minImageWidth);
            _minImageHeight = Math.Max(0f, minImageHeight);
            _suppressBackgroundPageImages = suppressBackgroundPageImages;
            _backgroundImageMinPageCoverage = Math.Clamp(backgroundImageMinPageCoverage, 0f, 1f);
            _maxKeptImageCoverageOnTextPages = Math.Clamp(maxKeptImageCoverageOnTextPages, 0f, 1f);
            _backgroundImageMinTextBlocks = Math.Max(0, backgroundImageMinTextBlocks);
            _backgroundImageMinTextChars = Math.Max(0, backgroundImageMinTextChars);
            _backgroundImageEdgeTolerance = Math.Max(0f, backgroundImageEdgeTolerance);
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
            List<TextFragment> effectiveFragments = BuildEffectiveTextFragments();
            List<TextBlock> blocks = MergeTextFragments(effectiveFragments);
            List<ImageBlock> images = FilterImageBlocks(blocks, effectiveFragments.Count);

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
                ImageBlocks = images
            };
        }

        private void HandleTextEvent(IEventData data)
        {
            if (data is not TextRenderInfo textInfo)
                return;

            int renderMode = GetTextRenderMode(textInfo);
            bool isInvisible = renderMode is 3 or 7;
            if (isInvisible && !_includeInvisibleTextLayer)
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
            if (IsLikelyNoiseGlyph(text, box, style))
                return;

            TextFragment fragment = new(text, box, style, isInvisible);
            if (isInvisible)
            {
                AddOrReplaceTextFragment(_invisibleTextFragmentsBySignature, fragment);
            }
            else
            {
                AddOrReplaceTextFragment(_visibleTextFragmentsBySignature, fragment);
            }
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

        private List<ImageBlock> FilterImageBlocks(IReadOnlyList<TextBlock> textBlocks, int textFragmentCount)
        {
            if (!_includeImages || _imageBlocks.Count == 0)
                return new List<ImageBlock>();

            if (!_suppressBackgroundPageImages)
                return _imageBlocks;

            int textBlockCount = textBlocks.Count;
            int textCharCount = textBlocks.Sum(x => TextSanitizer.CleanPdfArtifacts(x.OriginalText).Length);
            bool hasEnoughText =
                textFragmentCount >= _backgroundImageMinTextBlocks ||
                textBlockCount >= _backgroundImageMinTextBlocks ||
                textCharCount >= _backgroundImageMinTextChars;
            bool hasAnyText = textFragmentCount > 0 || textBlockCount > 0 || textCharCount > 0;

            float pageArea = MathF.Max(1f, _sourcePageWidth * _sourcePageHeight);
            float pageAspect = _sourcePageHeight <= 0 ? 1f : _sourcePageWidth / _sourcePageHeight;
            List<ImageBlock> kept = new(_imageBlocks.Count);
            int dropped = 0;

            foreach (ImageBlock image in _imageBlocks)
            {
                BoundingBox box = image.BoundingBox;
                float area = MathF.Max(0f, box.Width * box.Height);
                float coverage = area / pageArea;

                bool nearFullWidth = box.X <= _backgroundImageEdgeTolerance &&
                                     (_sourcePageWidth - box.Right) <= _backgroundImageEdgeTolerance;
                bool nearFullHeight = box.Y <= _backgroundImageEdgeTolerance &&
                                      (_sourcePageHeight - box.Top) <= _backgroundImageEdgeTolerance;
                bool nearPageFrame = nearFullWidth && nearFullHeight;

                float imageAspect = box.Height <= 0 ? 1f : box.Width / box.Height;
                bool aspectClose = MathF.Abs(imageAspect - pageAspect) <= 0.08f;

                bool tooLargeForTextPage =
                    hasAnyText &&
                    _maxKeptImageCoverageOnTextPages > 0f &&
                    coverage >= _maxKeptImageCoverageOnTextPages;

                bool likelyBackgroundByTextDensity =
                    hasEnoughText &&
                    coverage >= _backgroundImageMinPageCoverage &&
                    (aspectClose || nearPageFrame);

                // OCR PDFs often have only a thin visible text layer plus a full-page image.
                // If we keep that image, English source text remains visible in output.
                bool likelyBackgroundByAnyTextLayer =
                    !hasEnoughText &&
                    hasAnyText &&
                    coverage >= 0.82f &&
                    (aspectClose || nearPageFrame);

                bool likelyBackground = tooLargeForTextPage || likelyBackgroundByTextDensity || likelyBackgroundByAnyTextLayer;

                if (likelyBackground)
                {
                    dropped++;
                    continue;
                }

                kept.Add(image);
            }

            if (dropped > 0)
            {
                _log.LogInformation(
                    "Page {Page}: suppressed {Dropped} likely background page image(s). Kept={Kept}, TextFragments={TextFragments}, TextBlocks={TextBlocks}, TextChars={TextChars}",
                    _pageNumber,
                    dropped,
                    kept.Count,
                    textFragmentCount,
                    textBlockCount,
                    textCharCount);
            }

            return kept;
        }

        private List<TextBlock> MergeTextFragments(IReadOnlyList<TextFragment> fragments)
        {
            List<TextFragment> ordered = fragments
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

            List<MergedLine> paragraphs = _mergeLinesIntoParagraphs
                ? MergeLinesToParagraphs(merged)
                : merged;

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

            return DeduplicateOverlappingBlocks(result);
        }

        private List<TextFragment> BuildEffectiveTextFragments()
        {
            if (_visibleTextFragmentsBySignature.Count == 0)
            {
                return _includeInvisibleTextLayer
                    ? _invisibleTextFragmentsBySignature.Values.ToList()
                    : new List<TextFragment>();
            }

            if (!_includeInvisibleTextLayer || _invisibleTextFragmentsBySignature.Count == 0)
                return _visibleTextFragmentsBySignature.Values.ToList();

            int visibleFragments = _visibleTextFragmentsBySignature.Count;
            int invisibleFragments = _invisibleTextFragmentsBySignature.Count;
            int visibleChars = CountTextChars(_visibleTextFragmentsBySignature.Values);
            int invisibleChars = CountTextChars(_invisibleTextFragmentsBySignature.Values);
            float visibleAvgCharsPerFragment = ComputeAverageCharsPerFragment(visibleChars, visibleFragments);
            float invisibleAvgCharsPerFragment = ComputeAverageCharsPerFragment(invisibleChars, invisibleFragments);

            bool visibleLayerStrongByChars = visibleChars >= _invisibleFallbackMinVisibleTextChars;
            bool visibleLayerStrongByDensity =
                visibleFragments >= _invisibleFallbackMinVisibleFragments &&
                visibleAvgCharsPerFragment >= _minVisibleCharsPerFragmentForStrongLayer;
            bool visibleLayerStrong = visibleLayerStrongByChars || visibleLayerStrongByDensity;

            bool visibleLayerLooksDegenerate =
                visibleAvgCharsPerFragment < MathF.Max(1.1f, _minVisibleCharsPerFragmentForStrongLayer * 0.72f);
            bool invisibleClearlyDominates =
                invisibleChars >= Math.Max((int)MathF.Ceiling(visibleChars * 2.8f), visibleChars + 220) &&
                invisibleFragments >= Math.Max(24, visibleFragments * 2);

            if (visibleLayerStrong && visibleLayerLooksDegenerate && invisibleClearlyDominates)
                return _invisibleTextFragmentsBySignature.Values.ToList();

            if (visibleLayerStrong)
                return _visibleTextFragmentsBySignature.Values.ToList();

            if (_useInvisibleTextAsFallbackOnly)
            {
                bool invisibleClearlyBetterByChars =
                    invisibleChars >= Math.Max((int)MathF.Ceiling(visibleChars * 1.35f), visibleChars + 80);
                bool invisibleClearlyBetterByFragments =
                    invisibleFragments >= Math.Max(12, visibleFragments + 20);
                bool visibleLayerLooksSparse =
                    visibleChars <= Math.Max(_invisibleFallbackMinVisibleTextChars / 2, visibleFragments + 10) &&
                    visibleLayerLooksDegenerate;

                bool invisibleClearlyBetter =
                    (invisibleClearlyBetterByChars && invisibleClearlyBetterByFragments) ||
                    (visibleLayerLooksSparse &&
                     invisibleChars > visibleChars + 40 &&
                     invisibleAvgCharsPerFragment >= visibleAvgCharsPerFragment);

                return invisibleClearlyBetter
                    ? _invisibleTextFragmentsBySignature.Values.ToList()
                    : _visibleTextFragmentsBySignature.Values.ToList();
            }

            return invisibleChars > visibleChars
                ? _invisibleTextFragmentsBySignature.Values.ToList()
                : _visibleTextFragmentsBySignature.Values.ToList();
        }

        private static int CountTextChars(IEnumerable<TextFragment> fragments)
        {
            int total = 0;
            foreach (TextFragment fragment in fragments)
                total += TextSanitizer.CleanPdfArtifacts(fragment.Text).Length;

            return total;
        }

        private static float ComputeAverageCharsPerFragment(int chars, int fragments)
        {
            if (chars <= 0 || fragments <= 0)
                return 0f;

            return (float)chars / fragments;
        }

        private static List<TextBlock> DeduplicateOverlappingBlocks(List<TextBlock> blocks)
        {
            if (blocks.Count <= 1)
                return blocks;

            List<TextBlock> kept = new(blocks.Count);

            foreach (TextBlock candidate in blocks)
            {
                int duplicateIndex = kept.FindIndex(existing =>
                    string.Equals(existing.OriginalText, candidate.OriginalText, StringComparison.Ordinal) &&
                    IsNearDuplicateBounds(existing.BoundingBox, candidate.BoundingBox));

                if (duplicateIndex < 0)
                {
                    kept.Add(candidate);
                    continue;
                }

                TextBlock existing = kept[duplicateIndex];
                float existingArea = existing.BoundingBox.Width * existing.BoundingBox.Height;
                float candidateArea = candidate.BoundingBox.Width * candidate.BoundingBox.Height;
                if (candidateArea > existingArea + 1f)
                    kept[duplicateIndex] = candidate;
            }

            return kept;
        }

        private static bool IsNearDuplicateBounds(BoundingBox a, BoundingBox b)
        {
            float left = MathF.Max(a.X, b.X);
            float right = MathF.Min(a.Right, b.Right);
            float top = MathF.Min(a.Top, b.Top);
            float bottom = MathF.Max(a.Y, b.Y);

            float intersectionWidth = right - left;
            float intersectionHeight = top - bottom;
            if (intersectionWidth <= 0 || intersectionHeight <= 0)
                return false;

            float intersectionArea = intersectionWidth * intersectionHeight;
            float smallerArea = MathF.Max(1f, MathF.Min(a.Width * a.Height, b.Width * b.Height));
            float overlapRatio = intersectionArea / smallerArea;

            if (overlapRatio < 0.72f)
                return false;

            return MathF.Abs(a.X - b.X) <= 4f &&
                   MathF.Abs(a.Y - b.Y) <= 4f;
        }

        private static bool CanMerge(MergedLine current, TextFragment next)
        {
            bool relaxForInvisible = current.ContainsInvisible || next.IsInvisible;
            if (!relaxForInvisible && !AreStylesCompatible(current.Style, next.Style))
                return false;

            float currentMidY = current.Box.Y + (current.Box.Height / 2f);
            float nextMidY = next.Box.Y + (next.Box.Height / 2f);
            float yDelta = MathF.Abs(currentMidY - nextMidY);
            float maxYDelta = relaxForInvisible
                ? MathF.Max(4f, current.Style.FontSize * 0.9f)
                : MathF.Max(2f, current.Style.FontSize * 0.45f);
            if (yDelta > maxYDelta)
                return false;

            float horizontalGap = next.Box.X - (current.Box.X + current.Box.Width);
            float maxGap = relaxForInvisible
                ? MathF.Max(64f, current.Style.FontSize * 7f)
                : MathF.Max(26f, current.Style.FontSize * 2.2f);
            return horizontalGap <= maxGap;
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
            bool relaxForInvisible = current.ContainsInvisible || next.ContainsInvisible;
            if (!relaxForInvisible && !AreStylesCompatible(current.Style, next.Style))
                return false;

            float currentBottom = current.Box.Y + current.Box.Height;
            float nextTop = next.Box.Y;
            float verticalGap = nextTop - currentBottom;
            if (verticalGap < -2f)
                return false;

            float maxVerticalGap = relaxForInvisible
                ? MathF.Max(22f, current.Style.FontSize * 2.4f)
                : MathF.Max(12f, current.Style.FontSize * 1.6f);
            if (verticalGap > maxVerticalGap)
                return false;

            float startDelta = MathF.Abs(current.Box.X - next.Box.X);
            float maxStartDelta = relaxForInvisible
                ? MathF.Max(80f, current.Style.FontSize * 6f)
                : MathF.Max(24f, current.Style.FontSize * 2f);
            return startDelta <= maxStartDelta;
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

            Vector p0 = new Vector(0, 0, 1).Cross(ctm);
            Vector p1 = new Vector(1, 0, 1).Cross(ctm);
            Vector p2 = new Vector(0, 1, 1).Cross(ctm);
            Vector p3 = new Vector(1, 1, 1).Cross(ctm);

            float minX = MathF.Min(MathF.Min(p0.Get(Vector.I1), p1.Get(Vector.I1)), MathF.Min(p2.Get(Vector.I1), p3.Get(Vector.I1)));
            float maxX = MathF.Max(MathF.Max(p0.Get(Vector.I1), p1.Get(Vector.I1)), MathF.Max(p2.Get(Vector.I1), p3.Get(Vector.I1)));
            float minY = MathF.Min(MathF.Min(p0.Get(Vector.I2), p1.Get(Vector.I2)), MathF.Min(p2.Get(Vector.I2), p3.Get(Vector.I2)));
            float maxY = MathF.Max(MathF.Max(p0.Get(Vector.I2), p1.Get(Vector.I2)), MathF.Max(p2.Get(Vector.I2), p3.Get(Vector.I2)));

            box = new BoundingBox(minX, minY, MathF.Max(0f, maxX - minX), MathF.Max(0f, maxY - minY));
            return box.Width > 0 && box.Height > 0;
        }

        private static int GetTextRenderMode(TextRenderInfo info)
        {
            try
            {
                return info.GetTextRenderMode();
            }
            catch
            {
                return 0;
            }
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

            float shiftedScore = ScoreCandidate(shifted.Value);
            float directScore = ScoreCandidate(direct.Value);
            if (MathF.Abs(shiftedScore - directScore) > 0.01f)
                return shiftedScore > directScore ? shifted : direct;

            float coverageDelta = shifted.Value.Coverage - direct.Value.Coverage;
            if (MathF.Abs(coverageDelta) > 0.0001f)
                return coverageDelta > 0 ? shifted : direct;

            if (MathF.Abs(_viewportX) > 0.001f || MathF.Abs(_viewportY) > 0.001f)
                return shifted;

            // Prefer direct on exact ties for zero-offset pages.
            return direct;
        }

        private float ScoreCandidate(TransformCandidate candidate)
        {
            float edgePad = 2f;
            float penalty = 0f;

            if (candidate.Left <= edgePad)
                penalty += 25f;
            if (candidate.Bottom <= edgePad)
                penalty += 20f;
            if ((_sourcePageWidth - (candidate.Left + candidate.Width)) <= edgePad)
                penalty += 25f;
            if ((_sourcePageHeight - candidate.Top) <= edgePad)
                penalty += 20f;

            return (candidate.Coverage * 1000f) - penalty;
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

        private static void AddOrReplaceTextFragment(IDictionary<string, TextFragment> target, TextFragment candidate)
        {
            string signature = BuildTextSignature(candidate.Text, candidate.Box);
            if (!target.TryGetValue(signature, out TextFragment existing))
            {
                target[signature] = candidate;
                return;
            }

            if (ShouldReplace(existing, candidate))
                target[signature] = candidate;
        }

        private static bool IsLikelyNoiseGlyph(string text, BoundingBox box, StyleInfo style)
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
                return false;

            if (trimmed.Length > 2)
                return IsLikelyTinyPunctuationNoise(trimmed, box, style);

            if (trimmed.Any(char.IsLetterOrDigit))
                return false;

            if (!trimmed.All(IsAllowedNoisePunctuation))
                return false;

            float maxWidth = MathF.Max(8f, style.FontSize * 1.1f);
            float maxHeight = MathF.Max(8f, style.FontSize * 1.2f);
            return box.Width <= maxWidth && box.Height <= maxHeight;
        }

        private static bool IsLikelyTinyPunctuationNoise(string trimmed, BoundingBox box, StyleInfo style)
        {
            if (trimmed.Length is < 3 or > 6)
                return false;

            if (!trimmed.All(ch => !char.IsLetterOrDigit(ch)))
                return false;

            if (!trimmed.All(IsAllowedNoisePunctuation))
                return false;

            float maxWidth = MathF.Max(14f, style.FontSize * 2.2f);
            float maxHeight = MathF.Max(8f, style.FontSize * 1.35f);
            return box.Width <= maxWidth && box.Height <= maxHeight;
        }

        private static bool IsAllowedNoisePunctuation(char ch)
        {
            return ch switch
            {
                '-' or '_' or '.' or ':' or ',' or ';' or '\'' or '`' or '\u2013' or '\u2014' or '\u00B7' => true,
                _ => false
            };
        }

        private static bool ShouldReplace(TextFragment current, TextFragment candidate)
        {
            if (current.IsInvisible && !candidate.IsInvisible)
                return true;

            if (!current.IsInvisible && candidate.IsInvisible)
                return false;

            float currentArea = current.Box.Width * current.Box.Height;
            float candidateArea = candidate.Box.Width * candidate.Box.Height;
            if (candidateArea > currentArea + 0.5f)
                return true;

            return candidate.Style.FontSize > current.Style.FontSize + 0.2f;
        }

        private static string BuildTextSignature(string text, BoundingBox box)
        {
            float x = Quantize(box.X, 1.25f);
            float y = Quantize(box.Y, 1.25f);
            float w = Quantize(box.Width, 1.25f);
            float h = Quantize(box.Height, 1.25f);

            return $"{text}|{x:0.##}|{y:0.##}|{w:0.##}|{h:0.##}";
        }

        private static float Quantize(float value, float step)
        {
            if (step <= 0f)
                return value;

            return MathF.Round(value / step) * step;
        }

        private byte[] TryReadImageBytes(PdfImageXObject image)
        {
            try
            {
                byte[] decoded = image.GetImageBytes(true);
                if (decoded.Length > 0 && ImageDataFactory.IsSupportedType(decoded))
                    return decoded;

                byte[] encoded = image.GetImageBytes(false);
                if (encoded.Length > 0 && ImageDataFactory.IsSupportedType(encoded))
                    return encoded;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Page {Page}: failed to decode image bytes.", _pageNumber);
            }

            try
            {
                byte[] fallback = image.GetImageBytes(true);
                if (fallback.Length > 0 && GuessMimeType(fallback) != "application/octet-stream")
                    return fallback;
            }
            catch
            {
                // ignored
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

        private readonly record struct TextFragment(string Text, BoundingBox Box, StyleInfo Style, bool IsInvisible);
        private readonly record struct TransformCandidate(
            float Left,
            float Bottom,
            float Width,
            float Height,
            float Top,
            float Coverage);

        private sealed class MergedLine
        {
            private MergedLine(BoundingBox box, string text, StyleInfo style, bool containsInvisible)
            {
                Box = box;
                Text = text;
                Style = style;
                ContainsInvisible = containsInvisible;
            }

            public BoundingBox Box { get; private set; }
            public string Text { get; private set; }
            public StyleInfo Style { get; }
            public bool ContainsInvisible { get; private set; }

            public static MergedLine From(TextFragment fragment)
            {
                return new MergedLine(fragment.Box, fragment.Text, fragment.Style, fragment.IsInvisible);
            }

            public MergedLine Clone()
            {
                return new MergedLine(Box, Text, Style, ContainsInvisible);
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
                ContainsInvisible = ContainsInvisible || fragment.IsInvisible;
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
                ContainsInvisible = ContainsInvisible || line.ContainsInvisible;
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

