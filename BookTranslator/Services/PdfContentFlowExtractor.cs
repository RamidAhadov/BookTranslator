using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using Microsoft.Extensions.Logging;

namespace BookTranslator.Services;

public sealed record PdfImageXObjectAsset(int Index, PdfImageXObject XObject, float DisplayWidth, float DisplayHeight);

public static class PdfContentFlowExtractor
{
    public static string ExtractTextWithImageMarkers(
        string pdfPath,
        bool includeImageMarkers = true,
        bool deduplicatePdfImages = true,
        int maxOccurrencesPerImageSignature = 1,
        int maxPdfImagesPerPage = 6,
        float minPdfImageDisplayWidth = 15,
        float minPdfImageDisplayHeight = 15,
        float overlayMergeDistancePx = 5,
        Func<IReadOnlyList<byte[]>, string?>? ocrTextExtractor = null,
        ILogger? log = null)
    {
        StringBuilder sb = new StringBuilder();
        Dictionary<string, int> signatureToMarker = new(StringComparer.Ordinal);

        using PdfReader reader = new PdfReader(pdfPath);
        using PdfDocument pdf = new PdfDocument(reader);

        int nextMarkerIndex = 1;

        for (int pageNum = 1; pageNum <= pdf.GetNumberOfPages(); pageNum++)
        {
            PdfPage page = pdf.GetPage(pageNum);
            Rectangle pageRect = page.GetPageSize();
            byte[] pageOcrPayload = TryBuildSinglePagePdfPayload(page, log);

            var listener = new FlowContentListener(
                pageRect,
                nextMarkerIndex,
                pageNumber: pageNum,
                includeImages: false,
                includeImageMarkers: includeImageMarkers,
                deduplicatePdfImages: deduplicatePdfImages,
                maxOccurrencesPerImageSignature: maxOccurrencesPerImageSignature,
                maxPdfImagesPerPage: maxPdfImagesPerPage,
                minPdfImageDisplayWidth: minPdfImageDisplayWidth,
                minPdfImageDisplayHeight: minPdfImageDisplayHeight,
                overlayMergeDistancePx: overlayMergeDistancePx,
                pageOcrPayload: pageOcrPayload,
                ocrTextExtractor: ocrTextExtractor,
                signatureToMarker: signatureToMarker,
                targetPdf: null,
                log: log);

            new PdfCanvasProcessor(listener).ProcessPageContent(page);
            listener.FinalizePage();
            nextMarkerIndex = listener.NextImageIndex;

            string pageText = listener.BuildPageText();
            if (string.IsNullOrWhiteSpace(pageText))
                continue;

            if (sb.Length > 0)
                sb.AppendLine().AppendLine();

            sb.Append(pageText.Trim());
        }

        return sb.ToString();
    }

    public static IReadOnlyList<PdfImageXObjectAsset> ExtractImagesInOrder(
        string pdfPath,
        PdfDocument targetPdf,
        bool deduplicatePdfImages = true,
        int maxOccurrencesPerImageSignature = 1,
        int maxPdfImagesPerPage = 6,
        float minPdfImageDisplayWidth = 15,
        float minPdfImageDisplayHeight = 15,
        float overlayMergeDistancePx = 5,
        Func<IReadOnlyList<byte[]>, string?>? ocrTextExtractor = null,
        ILogger? log = null)
    {
        List<PdfImageXObjectAsset> images = new List<PdfImageXObjectAsset>();
        Dictionary<string, int> signatureToMarker = new(StringComparer.Ordinal);

        using PdfReader reader = new PdfReader(pdfPath);
        using PdfDocument pdf = new PdfDocument(reader);

        int nextMarkerIndex = 1;

        for (int pageNum = 1; pageNum <= pdf.GetNumberOfPages(); pageNum++)
        {
            PdfPage page = pdf.GetPage(pageNum);
            Rectangle pageRect = page.GetPageSize();
            byte[] pageOcrPayload = TryBuildSinglePagePdfPayload(page, log);

            var listener = new FlowContentListener(
                pageRect,
                nextMarkerIndex,
                pageNumber: pageNum,
                includeImages: true,
                includeImageMarkers: true,
                deduplicatePdfImages: deduplicatePdfImages,
                maxOccurrencesPerImageSignature: maxOccurrencesPerImageSignature,
                maxPdfImagesPerPage: maxPdfImagesPerPage,
                minPdfImageDisplayWidth: minPdfImageDisplayWidth,
                minPdfImageDisplayHeight: minPdfImageDisplayHeight,
                overlayMergeDistancePx: overlayMergeDistancePx,
                pageOcrPayload: pageOcrPayload,
                ocrTextExtractor: ocrTextExtractor,
                signatureToMarker: signatureToMarker,
                targetPdf: targetPdf,
                log: log);

            new PdfCanvasProcessor(listener).ProcessPageContent(page);
            listener.FinalizePage();
            nextMarkerIndex = listener.NextImageIndex;

            if (listener.Images.Count > 0)
                images.AddRange(listener.Images);
        }

        return images;
    }

    private sealed class FlowContentListener : IEventListener
    {
        private const float TextImageOverlapIgnoreThreshold = 0.15f;
        private const int DecorativeMinStreamBytes = 96;

        private const int OcrMaxCandidatesPerPage = 3;
        private const int OcrMinCharsToAccept = 20;
        private const int OcrMinStreamBytes = 1200;
        private const float OcrMinCoverage = 0.01f;
        private const float OcrMaxCoverage = 0.70f;
        private const float OcrMaxAspectRatio = 8.0f;

        private static readonly Regex OcrWeakTextPattern =
            new(@"^[\W_\d\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly Rectangle _pageRect;
        private readonly float _pageArea;
        private readonly int _pageNumber;
        private readonly bool _includeImages;
        private readonly bool _includeImageMarkers;
        private readonly bool _deduplicatePdfImages;
        private readonly int _maxUniqueMarkersPerPage;
        private readonly float _minPdfImageDisplayWidth;
        private readonly float _minPdfImageDisplayHeight;
        private readonly float _overlayMergeDistancePx;
        private readonly byte[] _pageOcrPayload;
        private readonly Func<IReadOnlyList<byte[]>, string?>? _ocrTextExtractor;
        private readonly Dictionary<string, int> _signatureToMarker;
        private readonly PdfDocument? _targetPdf;
        private readonly ILogger? _log;

        private readonly List<FlowToken> _tokens = new List<FlowToken>();
        private readonly Dictionary<int, PdfImageXObjectAsset> _imagesByMarker = new Dictionary<int, PdfImageXObjectAsset>();
        private readonly List<ImageRegion> _pageImageRegions = new List<ImageRegion>();
        private readonly List<Rectangle> _imageIgnoreRegions = new List<Rectangle>();
        private readonly Dictionary<int, ImageMeta> _imageMetaByMarker = new Dictionary<int, ImageMeta>();
        private readonly List<PdfImageXObjectAsset> _images = new List<PdfImageXObjectAsset>();

        private bool _isFinalized;

        public FlowContentListener(
            Rectangle pageRect,
            int startImageIndex,
            int pageNumber,
            bool includeImages,
            bool includeImageMarkers,
            bool deduplicatePdfImages,
            int maxOccurrencesPerImageSignature,
            int maxPdfImagesPerPage,
            float minPdfImageDisplayWidth,
            float minPdfImageDisplayHeight,
            float overlayMergeDistancePx,
            byte[] pageOcrPayload,
            Func<IReadOnlyList<byte[]>, string?>? ocrTextExtractor,
            Dictionary<string, int> signatureToMarker,
            PdfDocument? targetPdf,
            ILogger? log)
        {
            _pageRect = pageRect;
            _pageArea = MathF.Max(1f, pageRect.GetWidth() * pageRect.GetHeight());
            _pageNumber = pageNumber;

            NextImageIndex = startImageIndex;
            _includeImages = includeImages;
            _includeImageMarkers = includeImageMarkers;
            _deduplicatePdfImages = deduplicatePdfImages;
            _ = maxOccurrencesPerImageSignature;
            _maxUniqueMarkersPerPage = Math.Max(1, maxPdfImagesPerPage);
            _minPdfImageDisplayWidth = Math.Max(0, minPdfImageDisplayWidth);
            _minPdfImageDisplayHeight = Math.Max(0, minPdfImageDisplayHeight);
            _overlayMergeDistancePx = Math.Max(0, overlayMergeDistancePx);
            _pageOcrPayload = pageOcrPayload ?? Array.Empty<byte>();
            _ocrTextExtractor = ocrTextExtractor;
            _signatureToMarker = signatureToMarker;
            _targetPdf = targetPdf;
            _log = log;
        }

        public int NextImageIndex { get; private set; }

        public IReadOnlyList<PdfImageXObjectAsset> Images
        {
            get
            {
                FinalizePage();
                return _images;
            }
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            switch (type)
            {
                case EventType.RENDER_TEXT:
                {
                    if (data is not TextRenderInfo textInfo)
                        return;

                    string text = ExtractReadableText(textInfo);
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    _tokens.Add(FlowToken.FromText(text));

                    if (TryBuildTextBounds(textInfo, out Rectangle textBounds))
                        _imageIgnoreRegions.Add(textBounds);

                    break;
                }

                case EventType.RENDER_IMAGE:
                {
                    if (data is not ImageRenderInfo imageInfo)
                        return;

                    PdfImageXObject? imageXObj = imageInfo.GetImage();
                    if (imageXObj is null)
                        return;

                    if (!_includeImages && !_includeImageMarkers)
                        return;

                    if (!TryBuildImageBounds(imageInfo, out Rectangle imageBounds, out float displayWidth, out float displayHeight))
                        return;

                    if (displayWidth < _minPdfImageDisplayWidth || displayHeight < _minPdfImageDisplayHeight)
                        return;

                    if (IsInsideIgnoreRegion(imageBounds))
                        return;

                    int streamLength = TryGetImageStreamLength(imageXObj);
                    byte[] streamBytes = TryGetImageStreamBytes(imageXObj);
                    string? signature = TryGetImageSignature(imageXObj);

                    if (IsDecorativeOrPlaceholder(imageBounds, streamLength))
                        return;

                    int markerIndex;
                    bool createdNewMarker = false;

                    if (_deduplicatePdfImages && signature is not null && _signatureToMarker.TryGetValue(signature, out int existingBySignature))
                    {
                        markerIndex = existingBySignature;
                    }
                    else if (TryFindNearbyMarker(imageBounds, out int existingNearby))
                    {
                        markerIndex = existingNearby;
                    }
                    else
                    {
                        if (_pageImageRegions.Select(r => r.MarkerIndex).Distinct().Count() >= _maxUniqueMarkersPerPage)
                            return;

                        markerIndex = NextImageIndex++;
                        createdNewMarker = true;

                        if (_deduplicatePdfImages && signature is not null)
                            _signatureToMarker[signature] = markerIndex;

                        _pageImageRegions.Add(new ImageRegion(markerIndex, imageBounds));
                    }

                    if (_includeImageMarkers)
                        _tokens.Add(FlowToken.FromImage(markerIndex));

                    TrackImageMeta(markerIndex, imageBounds, streamLength, streamBytes, signature);

                    if (!_includeImages || _targetPdf is null)
                        return;

                    if (_imagesByMarker.ContainsKey(markerIndex))
                        return;

                    try
                    {
                        PdfStream copiedStream = (PdfStream)imageXObj.GetPdfObject().CopyTo(_targetPdf);
                        var asset = new PdfImageXObjectAsset(
                            markerIndex,
                            new PdfImageXObject(copiedStream),
                            displayWidth,
                            displayHeight);

                        _imagesByMarker[markerIndex] = asset;

                        if (!createdNewMarker)
                            _pageImageRegions.Add(new ImageRegion(markerIndex, imageBounds));
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning(ex, "Failed to copy image XObject {Index} from source PDF.", markerIndex);
                    }

                    break;
                }
            }
        }

        public ICollection<EventType>? GetSupportedEvents() => null;

        public void FinalizePage()
        {
            if (_isFinalized)
                return;

            _isFinalized = true;

            HashSet<int> suppressedMarkers = new HashSet<int>();

            foreach (ImageMeta meta in _imageMetaByMarker.Values)
            {
                if (meta.IsDecorative)
                    suppressedMarkers.Add(meta.MarkerIndex);
            }

            RunRegionLevelOcrAndSuppress(suppressedMarkers);

            if (suppressedMarkers.Count > 0)
            {
                SuppressMarkers(suppressedMarkers);
                _log?.LogInformation("Page {Page}: suppressed {Count} marker(s).", _pageNumber, suppressedMarkers.Count);
            }

            _images.Clear();
            foreach (int marker in _imagesByMarker.Keys.OrderBy(x => x))
                _images.Add(_imagesByMarker[marker]);
        }

        public string BuildPageText()
        {
            FinalizePage();

            StringBuilder sb = new StringBuilder();

            foreach (FlowToken token in _tokens)
            {
                if (token.Kind == FlowTokenKind.Text)
                {
                    if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
                        sb.Append(' ');

                    sb.Append(token.Text);
                    continue;
                }

                if (sb.Length > 0 && !EndsWithDoubleNewline(sb))
                    sb.AppendLine().AppendLine();

                sb.Append(PdfImageMarker.FromIndex(token.ImageIndex));
                sb.AppendLine().AppendLine();
            }

            return sb.ToString().Trim();
        }

        private void RunRegionLevelOcrAndSuppress(HashSet<int> suppressedMarkers)
        {
            if (_ocrTextExtractor is null)
                return;

            List<ImageMeta> candidates = _imageMetaByMarker.Values
                .Where(m => !m.IsDecorative && m.IsOcrCandidate && !suppressedMarkers.Contains(m.MarkerIndex))
                .OrderByDescending(m => m.Area)
                .Take(OcrMaxCandidatesPerPage)
                .ToList();

            if (candidates.Count == 0)
                return;

            _log?.LogInformation("Page {Page}: OCR candidate regions={Count}", _pageNumber, candidates.Count);

            foreach (ImageMeta c in candidates)
            {
                if (c.StreamBytes.Length < OcrMinStreamBytes)
                    continue;

                string? ocrText = null;
                try
                {
                    List<byte[]> payloads = new List<byte[]>(2);
                    if (IsSupportedOcrPayload(c.StreamBytes))
                        payloads.Add(c.StreamBytes);
                    if (_pageOcrPayload.Length > 0)
                        payloads.Add(_pageOcrPayload);

                    if (payloads.Count == 0)
                        continue;

                    ocrText = _ocrTextExtractor(payloads);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Page {Page}: OCR region failed. Marker={Marker}", _pageNumber, c.MarkerIndex);
                    continue;
                }

                if (!LooksUsefulOcrText(ocrText))
                {
                    _log?.LogInformation("Page {Page}: OCR text rejected for marker {Marker} (weak/noisy).", _pageNumber, c.MarkerIndex);
                    continue;
                }

                _tokens.Add(FlowToken.FromText(ocrText!.Trim()));
                suppressedMarkers.Add(c.MarkerIndex);

                _log?.LogInformation(
                    "Page {Page}: OCR accepted for marker {Marker}. Added {Chars} chars and suppressed marker.",
                    _pageNumber,
                    c.MarkerIndex,
                    ocrText.Length);
            }
        }

        private static bool IsSupportedOcrPayload(byte[] payload)
        {
            if (payload.Length < 4)
                return false;

            if (payload[0] == 0x25 && payload[1] == 0x50 && payload[2] == 0x44 && payload[3] == 0x46)
                return true;

            if (payload.Length >= 8 && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
                return true;

            if (payload[0] == 0xFF && payload[1] == 0xD8)
                return true;

            if (payload[0] == 0x42 && payload[1] == 0x4D)
                return true;

            if ((payload[0] == 0x49 && payload[1] == 0x49) || (payload[0] == 0x4D && payload[1] == 0x4D))
                return true;

            return false;
        }

        private void SuppressMarkers(HashSet<int> suppressed)
        {
            _tokens.RemoveAll(t => t.Kind == FlowTokenKind.Image && suppressed.Contains(t.ImageIndex));

            foreach (int marker in suppressed)
            {
                _imagesByMarker.Remove(marker);
                _imageMetaByMarker.Remove(marker);
            }

            List<string> keysToRemove = _signatureToMarker
                .Where(kv => suppressed.Contains(kv.Value))
                .Select(kv => kv.Key)
                .ToList();

            foreach (string key in keysToRemove)
                _signatureToMarker.Remove(key);
        }

        private bool IsInsideIgnoreRegion(Rectangle imageBounds)
        {
            float imgArea = MathF.Max(1f, imageBounds.GetWidth() * imageBounds.GetHeight());

            foreach (Rectangle textBounds in _imageIgnoreRegions)
            {
                float overlap = IntersectionArea(imageBounds, textBounds);
                if (overlap <= 0)
                    continue;

                float ratio = overlap / imgArea;
                if (ratio >= TextImageOverlapIgnoreThreshold)
                    return true;
            }

            return false;
        }

        private static bool IsDecorativeOrPlaceholder(Rectangle bounds, int streamLength)
        {
            if (streamLength is > 0 and < DecorativeMinStreamBytes)
                return true;

            float w = MathF.Max(1f, bounds.GetWidth());
            float h = MathF.Max(1f, bounds.GetHeight());
            float aspect = w > h ? w / h : h / w;

            if (aspect > 40f)
                return true;

            return false;
        }

        private bool IsTextLikeOcrCandidate(Rectangle bounds, int streamLength)
        {
            if (streamLength < OcrMinStreamBytes)
                return false;

            float area = MathF.Max(1f, bounds.GetWidth() * bounds.GetHeight());
            float coverage = area / _pageArea;
            if (coverage < OcrMinCoverage || coverage > OcrMaxCoverage)
                return false;

            float w = MathF.Max(1f, bounds.GetWidth());
            float h = MathF.Max(1f, bounds.GetHeight());
            float aspect = w > h ? w / h : h / w;
            if (aspect > OcrMaxAspectRatio)
                return false;

            // Skip top/bottom margins where decorative/cover artifacts are frequent.
            float y = bounds.GetY();
            float yCenter = y + (h / 2f);
            float normalizedY = yCenter / MathF.Max(1f, _pageRect.GetHeight());
            if (normalizedY < 0.05f || normalizedY > 0.95f)
                return false;

            return true;
        }

        private static bool LooksUsefulOcrText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.Trim();
            if (normalized.Length < OcrMinCharsToAccept)
                return false;

            if (OcrWeakTextPattern.IsMatch(normalized))
                return false;

            int letters = normalized.Count(char.IsLetter);
            double letterRatio = (double)letters / normalized.Length;
            return letterRatio >= 0.25;
        }

        private void TrackImageMeta(int markerIndex, Rectangle bounds, int streamLength, byte[] streamBytes, string? signature)
        {
            float area = MathF.Max(1f, bounds.GetWidth() * bounds.GetHeight());
            bool isDecorative = IsDecorativeOrPlaceholder(bounds, streamLength);
            bool isOcrCandidate = !isDecorative && IsTextLikeOcrCandidate(bounds, streamLength);

            if (_imageMetaByMarker.TryGetValue(markerIndex, out ImageMeta existing))
            {
                float mergedArea = MathF.Max(existing.Area, area);
                Rectangle mergedBounds = MergeBounds(existing.Bounds, bounds);
                int mergedLength = Math.Max(existing.StreamLength, streamLength);

                _imageMetaByMarker[markerIndex] = existing with
                {
                    Area = mergedArea,
                    Bounds = mergedBounds,
                    StreamLength = mergedLength,
                    StreamBytes = existing.StreamBytes.Length >= streamBytes.Length ? existing.StreamBytes : streamBytes,
                    IsDecorative = existing.IsDecorative && isDecorative,
                    IsOcrCandidate = existing.IsOcrCandidate || isOcrCandidate,
                    Signature = existing.Signature ?? signature
                };

                return;
            }

            _imageMetaByMarker[markerIndex] = new ImageMeta(
                markerIndex,
                bounds,
                area,
                streamLength,
                streamBytes,
                signature,
                isDecorative,
                isOcrCandidate);
        }

        private bool TryFindNearbyMarker(Rectangle candidate, out int markerIndex)
        {
            foreach (ImageRegion r in _pageImageRegions)
            {
                if (OverlapsOrNear(r.Bounds, candidate, _overlayMergeDistancePx))
                {
                    markerIndex = r.MarkerIndex;
                    return true;
                }
            }

            markerIndex = 0;
            return false;
        }

        private static bool OverlapsOrNear(Rectangle a, Rectangle b, float threshold)
        {
            float ax1 = a.GetX();
            float ay1 = a.GetY();
            float ax2 = ax1 + a.GetWidth();
            float ay2 = ay1 + a.GetHeight();

            float bx1 = b.GetX();
            float by1 = b.GetY();
            float bx2 = bx1 + b.GetWidth();
            float by2 = by1 + b.GetHeight();

            float sepX = MathF.Max(0f, MathF.Max(ax1 - bx2, bx1 - ax2));
            float sepY = MathF.Max(0f, MathF.Max(ay1 - by2, by1 - ay2));

            return sepX <= threshold && sepY <= threshold;
        }

        private static float IntersectionArea(Rectangle a, Rectangle b)
        {
            float x1 = MathF.Max(a.GetX(), b.GetX());
            float y1 = MathF.Max(a.GetY(), b.GetY());
            float x2 = MathF.Min(a.GetX() + a.GetWidth(), b.GetX() + b.GetWidth());
            float y2 = MathF.Min(a.GetY() + a.GetHeight(), b.GetY() + b.GetHeight());

            float w = x2 - x1;
            float h = y2 - y1;
            if (w <= 0 || h <= 0)
                return 0;

            return w * h;
        }

        private static Rectangle MergeBounds(Rectangle a, Rectangle b)
        {
            float x1 = MathF.Min(a.GetX(), b.GetX());
            float y1 = MathF.Min(a.GetY(), b.GetY());
            float x2 = MathF.Max(a.GetX() + a.GetWidth(), b.GetX() + b.GetWidth());
            float y2 = MathF.Max(a.GetY() + a.GetHeight(), b.GetY() + b.GetHeight());
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        private static bool TryBuildImageBounds(ImageRenderInfo imageInfo, out Rectangle bounds, out float displayWidth, out float displayHeight)
        {
            Matrix ctm = imageInfo.GetImageCtm();

            float a = ctm.Get(Matrix.I11);
            float b = ctm.Get(Matrix.I12);
            float c = ctm.Get(Matrix.I21);
            float d = ctm.Get(Matrix.I22);

            displayWidth = MathF.Sqrt(a * a + b * b);
            displayHeight = MathF.Sqrt(c * c + d * d);

            float x = ctm.Get(Matrix.I31);
            float y = ctm.Get(Matrix.I32);

            bounds = new Rectangle(x, y, MathF.Abs(displayWidth), MathF.Abs(displayHeight));
            return true;
        }

        private static bool TryBuildTextBounds(TextRenderInfo textInfo, out Rectangle bounds)
        {
            bounds = default;

            try
            {
                LineSegment asc = textInfo.GetAscentLine();
                LineSegment desc = textInfo.GetDescentLine();

                Vector a1 = asc.GetStartPoint();
                Vector a2 = asc.GetEndPoint();
                Vector d1 = desc.GetStartPoint();
                Vector d2 = desc.GetEndPoint();

                float minX = MathF.Min(MathF.Min(a1.Get(Vector.I1), a2.Get(Vector.I1)), MathF.Min(d1.Get(Vector.I1), d2.Get(Vector.I1)));
                float maxX = MathF.Max(MathF.Max(a1.Get(Vector.I1), a2.Get(Vector.I1)), MathF.Max(d1.Get(Vector.I1), d2.Get(Vector.I1)));
                float minY = MathF.Min(MathF.Min(a1.Get(Vector.I2), a2.Get(Vector.I2)), MathF.Min(d1.Get(Vector.I2), d2.Get(Vector.I2)));
                float maxY = MathF.Max(MathF.Max(a1.Get(Vector.I2), a2.Get(Vector.I2)), MathF.Max(d1.Get(Vector.I2), d2.Get(Vector.I2)));

                float w = MathF.Max(0f, maxX - minX);
                float h = MathF.Max(0f, maxY - minY);
                if (w <= 0 || h <= 0)
                    return false;

                bounds = new Rectangle(minX, minY, w, h);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int TryGetImageStreamLength(PdfImageXObject imageXObj)
        {
            try
            {
                return imageXObj.GetPdfObject().GetBytes().Length;
            }
            catch
            {
                return 0;
            }
        }

        private static byte[] TryGetImageStreamBytes(PdfImageXObject imageXObj)
        {
            try
            {
                return imageXObj.GetPdfObject().GetBytes();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static string? TryGetImageSignature(PdfImageXObject imageXObj)
        {
            try
            {
                byte[] bytes = imageXObj.GetPdfObject().GetBytes();
                if (bytes.Length == 0)
                    return null;

                byte[] hash = SHA256.HashData(bytes);
                return Convert.ToHexString(hash);
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractReadableText(TextRenderInfo textInfo)
        {
            string text = textInfo.GetText();
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            // Type3/ToUnicode fallback: if glyphs exist but unicode mapping is missing,
            // keep a synthetic placeholder so the page is treated as text-bearing.
            IList<TextRenderInfo> glyphs = textInfo.GetCharacterRenderInfos();
            if (glyphs.Count > 0)
                return new string('x', glyphs.Count);

            try
            {
                byte[] raw = textInfo.GetPdfString().GetValueBytes();
                if (raw.Length > 0)
                    return new string('x', raw.Length);
            }
            catch
            {
                // ignored
            }

            return string.Empty;
        }

        private static bool EndsWithDoubleNewline(StringBuilder sb)
        {
            if (sb.Length < 2)
                return false;

            return sb[^1] == '\n' && sb[^2] == '\n';
        }
    }

    private readonly record struct FlowToken(FlowTokenKind Kind, string Text, int ImageIndex)
    {
        public static FlowToken FromText(string text) => new(FlowTokenKind.Text, text, 0);
        public static FlowToken FromImage(int imageIndex) => new(FlowTokenKind.Image, string.Empty, imageIndex);
    }

    private readonly record struct ImageRegion(int MarkerIndex, Rectangle Bounds);

    private readonly record struct ImageMeta(
        int MarkerIndex,
        Rectangle Bounds,
        float Area,
        int StreamLength,
        byte[] StreamBytes,
        string? Signature,
        bool IsDecorative,
        bool IsOcrCandidate);

    private static byte[] TryBuildSinglePagePdfPayload(PdfPage sourcePage, ILogger? log)
    {
        try
        {
            using MemoryStream ms = new MemoryStream();
            using (PdfWriter writer = new PdfWriter(ms))
            using (PdfDocument onePageDoc = new PdfDocument(writer))
            {
                PdfPage copied = sourcePage.CopyTo(onePageDoc);
                onePageDoc.AddPage(copied);
            }

            return ms.ToArray();
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Failed to build single-page PDF OCR payload.");
            return Array.Empty<byte>();
        }
    }

    private enum FlowTokenKind
    {
        Text,
        Image
    }
}
