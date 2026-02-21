using BookTranslator.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    private readonly ILogger<PdfPigTextExtractor> _log;
    private readonly TranslationOptions _translationOptions;

    public PdfPigTextExtractor(ILogger<PdfPigTextExtractor> log, IOptions<TranslationOptions> translationOptions)
    {
        _log = log;
        _translationOptions = translationOptions.Value;
    }

    public string Extract(string pdfPath)
    {
        return PdfContentFlowExtractor.ExtractTextWithImageMarkers(
            pdfPath,
            includeImageMarkers: _translationOptions.IncludeImagesInPdf,
            log: _log);
    }
}
