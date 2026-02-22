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

        _log.LogInformation("Starting coordinate-aware translation. Input={Input}, Provider={Provider}", inputPath, _translator.ProviderName);

        IReadOnlyList<PageObject> pages = await _analyzer.AnalyzeAsync(inputPath, ct);
        _log.LogInformation("Layout extracted. Pages={Pages}", pages.Count);

        IReadOnlyList<PageObject> pagesToProcess = SelectPagesToProcess(pages);
        string outputPath = BuildOutputPath(inputPath, pagesToProcess);

        await _checkpointStore.InitializeAsync(inputPath, _translation.TargetLanguage, _translator.ProviderName, ct);

        SemaphoreSlim semaphore = new SemaphoreSlim(Math.Max(1, _translation.MaxDegreeOfParallelism));

        Task[] tasks = pagesToProcess.Select(async page =>
        {
            await semaphore.WaitAsync(ct);
            string pageFingerprint = BuildPageFingerprint(page);
            bool bypassResumeForPageSelection =
                !string.IsNullOrWhiteSpace(_translation.PageSelection) &&
                _translation.ForceRetranslateSelectedPages;

            try
            {
                if (_translation.Resume && !bypassResumeForPageSelection)
                {
                    LayoutCheckpointReadResult cached = await _checkpointStore.TryReadPageAsync(
                        page.PageNumber,
                        pageFingerprint,
                        ct);

                    if (cached.Hit)
                    {
                        ApplyTranslations(page, cached.Items);
                        List<TextBlock> uncovered = GetUncoveredBlocks(page, cached.Items);

                        if (uncovered.Count > 0)
                        {
                            _log.LogWarning(
                                "Page {Page}: checkpoint coverage is partial. Cached={Cached}, Missing={Missing}. Translating only missing blocks.",
                                page.PageNumber,
                                page.TextBlocks.Count - uncovered.Count,
                                uncovered.Count);

                            PageObject subset = CreateSubsetPage(page, uncovered);
                            IReadOnlyList<TranslatedTextItem> translatedMissing = await _translator.TranslatePageAsync(
                                subset,
                                _translation.TargetLanguage,
                                ct);

                            ApplyTranslations(page, translatedMissing);
                            IReadOnlyList<TranslatedTextItem> merged = MergeTranslatedItems(cached.Items, translatedMissing);
                            await _checkpointStore.SavePageSuccessAsync(page.PageNumber, pageFingerprint, merged, ct);

                            _log.LogInformation(
                                "Page {Page} resumed partially. Reused={Reused}, NewlyTranslated={NewlyTranslated}",
                                page.PageNumber,
                                page.TextBlocks.Count - uncovered.Count,
                                uncovered.Count);
                            return;
                        }

                        _log.LogInformation(
                            "Page {Page} resumed from checkpoint. TextBlocks={TextBlocks}",
                            page.PageNumber,
                            page.TextBlocks.Count);
                        return;
                    }
                }
                else if (bypassResumeForPageSelection)
                {
                    _log.LogInformation(
                        "Page {Page}: checkpoint resume bypassed because PageSelection is active and ForceRetranslateSelectedPages=true.",
                        page.PageNumber);
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

        await _reconstructor.ReconstructAsync(pagesToProcess, outputPath, ct);
        _log.LogInformation("Coordinate-aware translation completed. Output={Output}", outputPath);
    }

    private IReadOnlyList<PageObject> SelectPagesToProcess(IReadOnlyList<PageObject> pages)
    {
        if (pages.Count == 0)
            return pages;

        if (string.IsNullOrWhiteSpace(_translation.PageSelection))
            return pages;

        int maxPage = pages.Max(x => x.PageNumber);
        IReadOnlyList<int> selectedPages = PageSelectionParser.Parse(_translation.PageSelection, maxPage);
        if (selectedPages.Count == 0)
            throw new InvalidOperationException("PageSelection is set but no valid pages were resolved.");

        HashSet<int> selectedSet = new(selectedPages);
        List<PageObject> filtered = pages
            .Where(x => selectedSet.Contains(x.PageNumber))
            .OrderBy(x => x.PageNumber)
            .ToList();

        if (filtered.Count == 0)
            throw new InvalidOperationException("PageSelection did not match any pages from the input PDF.");

        _log.LogInformation(
            "Page filter active. Selection={Selection}, Pages={Count}",
            PageSelectionParser.BuildDescriptor(selectedPages),
            filtered.Count);

        return filtered;
    }

    private string BuildOutputPath(string inputPath, IReadOnlyList<PageObject> pagesToProcess)
    {
        string inputBaseName = Path.GetFileNameWithoutExtension(inputPath);
        string outputName;

        if (string.IsNullOrWhiteSpace(_translation.PageSelection))
        {
            outputName = $"{inputBaseName}_translated.pdf";
        }
        else
        {
            string descriptor = PageSelectionParser.BuildDescriptor(pagesToProcess.Select(x => x.PageNumber).ToArray());
            outputName = $"{inputBaseName}_translated_pages_{descriptor}.pdf";
        }

        return Path.Combine(_translation.OutputFolder, outputName);
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

    private static PageObject CreateSubsetPage(PageObject page, IReadOnlyList<TextBlock> subset)
    {
        return new PageObject
        {
            PageNumber = page.PageNumber,
            Width = page.Width,
            Height = page.Height,
            SourcePageWidth = page.SourcePageWidth,
            SourcePageHeight = page.SourcePageHeight,
            Rotation = page.Rotation,
            SourcePagePdfBytes = page.SourcePagePdfBytes,
            TextBlocks = subset.ToList(),
            ImageBlocks = new List<ImageBlock>()
        };
    }

    private static IReadOnlyList<TranslatedTextItem> MergeTranslatedItems(
        IReadOnlyList<TranslatedTextItem> cached,
        IReadOnlyList<TranslatedTextItem> fresh)
    {
        return cached
            .Concat(fresh)
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockId))
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
    }

    private static void ApplyTranslations(PageObject page, IReadOnlyList<TranslatedTextItem> translatedItems)
    {
        Dictionary<string, string> byId = translatedItems
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockId) && !string.IsNullOrWhiteSpace(x.TranslatedText))
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(x => x.BlockId, x => x.TranslatedText, StringComparer.Ordinal);

        Dictionary<string, string> byUniqueOriginal = translatedItems
            .Where(x => !string.IsNullOrWhiteSpace(x.TranslatedText))
            .Select(x => new
            {
                Original = TextSanitizer.CleanPdfArtifacts(x.OriginalText),
                x.TranslatedText
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Original))
            .GroupBy(x => x.Original, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().TranslatedText, StringComparer.Ordinal);

        foreach (TextBlock block in page.TextBlocks)
        {
            if (byId.TryGetValue(block.BlockId, out string? translatedById) && !string.IsNullOrWhiteSpace(translatedById))
            {
                block.TranslatedText = translatedById;
                continue;
            }

            string original = TextSanitizer.CleanPdfArtifacts(block.OriginalText);
            if (byUniqueOriginal.TryGetValue(original, out string? translatedByText) && !string.IsNullOrWhiteSpace(translatedByText))
                block.TranslatedText = translatedByText;
        }
    }

    private static bool HasCompleteCoverage(PageObject page, IReadOnlyList<TranslatedTextItem> translatedItems)
    {
        return GetUncoveredBlocks(page, translatedItems).Count == 0;
    }

    private static List<TextBlock> GetUncoveredBlocks(PageObject page, IReadOnlyList<TranslatedTextItem> translatedItems)
    {
        Dictionary<string, TranslatedTextItem> byId = translatedItems
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockId))
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(x => x.BlockId, x => x, StringComparer.Ordinal);

        Dictionary<string, TranslatedTextItem> byUniqueOriginal = translatedItems
            .Select(x => new
            {
                Item = x,
                Original = TextSanitizer.CleanPdfArtifacts(x.OriginalText)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Original))
            .GroupBy(x => x.Original, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Item, StringComparer.Ordinal);

        List<TextBlock> uncovered = new();

        foreach (TextBlock block in page.TextBlocks)
        {
            string currentOriginal = TextSanitizer.CleanPdfArtifacts(block.OriginalText);

            if (byId.TryGetValue(block.BlockId, out TranslatedTextItem? itemById))
            {
                string cachedOriginal = TextSanitizer.CleanPdfArtifacts(itemById.OriginalText);
                if (string.Equals(currentOriginal, cachedOriginal, StringComparison.Ordinal))
                    continue;
            }

            if (byUniqueOriginal.ContainsKey(currentOriginal))
                continue;

            uncovered.Add(block);
        }

        return uncovered;
    }
}
