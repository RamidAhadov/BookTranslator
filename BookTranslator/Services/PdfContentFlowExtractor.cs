using System.Text;
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
    public static string ExtractTextWithImageMarkers(string pdfPath, bool includeImageMarkers = true, ILogger? log = null)
    {
        StringBuilder sb = new StringBuilder();

        using PdfReader reader = new PdfReader(pdfPath);
        using PdfDocument pdf = new PdfDocument(reader);

        int imageIndex = 1;

        for (int pageNum = 1; pageNum <= pdf.GetNumberOfPages(); pageNum++)
        {
            var listener = new FlowContentListener(imageIndex, includeImages: false, includeImageMarkers: includeImageMarkers);
            new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(pageNum));

            imageIndex = listener.NextImageIndex;
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
        ILogger? log = null)
    {
        List<PdfImageXObjectAsset> images = new List<PdfImageXObjectAsset>();

        using PdfReader reader = new PdfReader(pdfPath);
        using PdfDocument pdf = new PdfDocument(reader);

        int imageIndex = 1;

        for (int pageNum = 1; pageNum <= pdf.GetNumberOfPages(); pageNum++)
        {
            var listener = new FlowContentListener(
                imageIndex,
                includeImages: true,
                includeImageMarkers: true,
                targetPdf,
                log);
            new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(pageNum));
            imageIndex = listener.NextImageIndex;

            if (listener.Images.Count == 0)
                continue;

            images.AddRange(listener.Images);
        }

        return images;
    }

    private sealed class FlowContentListener : IEventListener
    {
        private readonly bool _includeImages;
        private readonly bool _includeImageMarkers;
        private readonly PdfDocument? _targetPdf;
        private readonly ILogger? _log;
        private readonly List<FlowToken> _tokens = new List<FlowToken>();

        public FlowContentListener(
            int startImageIndex,
            bool includeImages,
            bool includeImageMarkers,
            PdfDocument? targetPdf = null,
            ILogger? log = null)
        {
            NextImageIndex = startImageIndex;
            _includeImages = includeImages;
            _includeImageMarkers = includeImageMarkers;
            _targetPdf = targetPdf;
            _log = log;
        }

        public int NextImageIndex { get; private set; }

        public List<PdfImageXObjectAsset> Images { get; } = new List<PdfImageXObjectAsset>();

        public void EventOccurred(IEventData data, EventType type)
        {
            switch (type)
            {
                case EventType.RENDER_TEXT:
                {
                    if (data is not TextRenderInfo textInfo)
                        return;

                    string text = textInfo.GetText();
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    _tokens.Add(FlowToken.FromText(text));
                    break;
                }

                case EventType.RENDER_IMAGE:
                {
                    if (data is not ImageRenderInfo imageInfo)
                        return;

                    var imageXObj = imageInfo.GetImage();
                    if (imageXObj is null)
                        return;

                    if (!_includeImages && !_includeImageMarkers)
                        return;

                    int index = NextImageIndex++;

                    if (_includeImageMarkers)
                        _tokens.Add(FlowToken.FromImage(index));

                    if (!_includeImages)
                        return;

                    if (_targetPdf is null)
                        return;

                    try
                    {
                        PdfStream copiedStream = (PdfStream)imageXObj.GetPdfObject().CopyTo(_targetPdf);
                        Matrix ctm = imageInfo.GetImageCtm();
                        float a = ctm.Get(Matrix.I11);
                        float b = ctm.Get(Matrix.I12);
                        float c = ctm.Get(Matrix.I21);
                        float d = ctm.Get(Matrix.I22);

                        float displayWidth = MathF.Sqrt(a * a + b * b);
                        float displayHeight = MathF.Sqrt(c * c + d * d);

                        Images.Add(new PdfImageXObjectAsset(
                            index,
                            new PdfImageXObject(copiedStream),
                            displayWidth,
                            displayHeight));
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning(ex, "Failed to copy image XObject {Index} from source PDF.", index);
                    }

                    break;
                }
            }
        }

        public ICollection<EventType>? GetSupportedEvents() => null;

        public string BuildPageText()
        {
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

    private enum FlowTokenKind
    {
        Text,
        Image
    }
}
