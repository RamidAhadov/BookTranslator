using BookTranslator.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    private readonly ILogger<PdfPigTextExtractor> _log;
    private readonly TranslationOptions _translationOptions;
    private readonly IOcrService _ocr;

    public PdfPigTextExtractor(
        ILogger<PdfPigTextExtractor> log,
        IOptions<TranslationOptions> translationOptions,
        IOcrService ocr)
    {
        _log = log;
        _translationOptions = translationOptions.Value;
        _ocr = ocr;
    }

    public string Extract(string pdfPath)
    {
        return PdfContentFlowExtractor.ExtractTextWithImageMarkers(
            pdfPath,
            includeImageMarkers: _translationOptions.IncludeImagesInPdf,
            deduplicatePdfImages: _translationOptions.DeduplicatePdfImages,
            maxOccurrencesPerImageSignature: _translationOptions.MaxOccurrencesPerImageSignature,
            maxPdfImagesPerPage: _translationOptions.MaxPdfImagesPerPage,
            minPdfImageDisplayWidth: _translationOptions.MinPdfImageDisplayWidth,
            minPdfImageDisplayHeight: _translationOptions.MinPdfImageDisplayHeight,
            overlayMergeDistancePx: _translationOptions.OverlayMergeDistancePx,
            ocrTextExtractor: _ocr.IsEnabled ? _ocr.ExtractTextFromImages : null,
            log: _log);
    }
}
