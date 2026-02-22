using BookTranslator.Models.Layout;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace BookTranslator.Services;

public sealed class LayoutTranslationPipeline
{
    private readonly IPdfLayoutAnalyzer _analyzer;
    private readonly ITranslatorService _translator;
    private readonly ILayoutCheckpointStore _checkpointStore;
    private readonly PdfReconstructor _reconstructor;
    private readonly TranslationOptions _translation;
    private readonly ILogger<LayoutTranslationPipeline> _log;

    public LayoutTranslationPipeline(
        IPdfLayoutAnalyzer analyzer,
        ITranslatorService translator,
        ILayoutCheckpointStore checkpointStore,
        PdfReconstructor reconstructor,
        IOptions<TranslationOptions> translation,
        ILogger<LayoutTranslationPipeline> log)
    {
        _analyzer = analyzer;
        _translator = translator;
        _checkpointStore = checkpointStore;
        _reconstructor = reconstructor;
        _translation = translation.Value;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string inputPath = _translation.InputPath;
        string outputName = Path.GetFileNameWithoutExtension(inputPath) + "_translated.pdf";
        string outputPath = Path.Combine(_translation.OutputFolder, outputName);

        _log.LogInformation("Starting coordinate-aware translation. Input={Input}, Provider={Provider}", inputPath, _translator.ProviderName);

        IReadOnlyList<PageObject> pages = await _analyzer.AnalyzeAsync(inputPath, ct);
        _log.LogInformation("Layout extracted. Pages={Pages}", pages.Count);

        await _checkpointStore.InitializeAsync(inputPath, _translation.TargetLanguage, _translator.ProviderName, ct);

        SemaphoreSlim semaphore = new SemaphoreSlim(Math.Max(1, _translation.MaxDegreeOfParallelism));

        Task[] tasks = pages.Select(async page =>
        {
            await semaphore.WaitAsync(ct);
            string pageFingerprint = BuildPageFingerprint(page);

            try
            {
                if (_translation.Resume)
                {
                    LayoutCheckpointReadResult cached = await _checkpointStore.TryReadPageAsync(
                        page.PageNumber,
                        pageFingerprint,
                        ct);

                    if (cached.Hit)
                    {
                        if (!HasCompleteCoverage(page, cached.Items))
                        {
                            _log.LogWarning(
                                "Page {Page}: checkpoint exists but block coverage is incomplete. Re-translating page.",
                                page.PageNumber);
                        }
                        else
                        {
                            ApplyTranslations(page, cached.Items);
                            _log.LogInformation(
                                "Page {Page} resumed from checkpoint. TextBlocks={TextBlocks}",
                                page.PageNumber,
                                page.TextBlocks.Count);
                            return;
                        }
                    }
                }

                IReadOnlyList<TranslatedTextItem> translated = await _translator.TranslatePageAsync(page, _translation.TargetLanguage, ct);
                ApplyTranslations(page, translated);
                await _checkpointStore.SavePageSuccessAsync(page.PageNumber, pageFingerprint, translated, ct);

                _log.LogInformation(
                    "Page {Page} translated. TextBlocks={TextBlocks}, Provider={Provider}",
                    page.PageNumber,
                    page.TextBlocks.Count,
                    _translator.ProviderName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _checkpointStore.SavePageFailureAsync(page.PageNumber, pageFingerprint, ex.ToString(), ct);
                throw;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        await _reconstructor.ReconstructAsync(pages, outputPath, ct);
        _log.LogInformation("Coordinate-aware translation completed. Output={Output}", outputPath);
    }

    private static string BuildPageFingerprint(PageObject page)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("page=").Append(page.PageNumber).Append('|');

        foreach (TextBlock block in page.TextBlocks.OrderBy(b => b.BlockId, StringComparer.Ordinal))
        {
            sb.Append("txt|").Append(block.BlockId).Append('|');
            sb.Append("text=").Append(TextSanitizer.CleanPdfArtifacts(block.OriginalText)).Append('|');
        }

        return CacheKeyBuilder.Build(sb.ToString());
    }

    private static void ApplyTranslations(PageObject page, IReadOnlyList<TranslatedTextItem> translatedItems)
    {
        Dictionary<string, string> map = translatedItems
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockId) && !string.IsNullOrWhiteSpace(x.TranslatedText))
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(x => x.BlockId, x => x.TranslatedText, StringComparer.Ordinal);

        foreach (TextBlock block in page.TextBlocks)
        {
            if (map.TryGetValue(block.BlockId, out string? translated) && !string.IsNullOrWhiteSpace(translated))
                block.TranslatedText = translated;
        }
    }

    private static bool HasCompleteCoverage(PageObject page, IReadOnlyList<TranslatedTextItem> translatedItems)
    {
        Dictionary<string, TranslatedTextItem> byId = translatedItems
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockId))
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(x => x.BlockId, x => x, StringComparer.Ordinal);

        foreach (TextBlock block in page.TextBlocks)
        {
            if (!byId.TryGetValue(block.BlockId, out TranslatedTextItem? item))
                return false;

            string currentOriginal = TextSanitizer.CleanPdfArtifacts(block.OriginalText);
            string cachedOriginal = TextSanitizer.CleanPdfArtifacts(item.OriginalText);
            if (!string.Equals(currentOriginal, cachedOriginal, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
