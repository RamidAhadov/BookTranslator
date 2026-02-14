using BookTranslator.Models;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class TranslationPipeline
{
    private readonly IPdfTextExtractor _extractor;
    private readonly ITextChunker _chunker;
    private readonly TranslationOrchestrator _orchestrator;
    private readonly TranslationOptions _translationOptions;
    private readonly OpenAiOptions _openAiOptions;
    private readonly TestingOptions _testingOptions;
    private readonly IOutputWriter _writer;
    private readonly ILogger<TranslationPipeline> _log;

    public TranslationPipeline(
        IPdfTextExtractor extractor,
        ITextChunker chunker,
        TranslationOrchestrator orchestrator,
        IOptions<TranslationOptions> translationOptions,
        IOptions<OpenAiOptions> openAiOptions,
        IOptions<TestingOptions> testingOptions,
        IOutputWriter writer,
        ILogger<TranslationPipeline> log)
    {
        _extractor = extractor;
        _chunker = chunker;
        _orchestrator = orchestrator;
        _translationOptions = translationOptions.Value;
        _openAiOptions = openAiOptions.Value;
        _testingOptions = testingOptions.Value;
        _writer = writer;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string pdfPath = _translationOptions.InputPath; 
        string outputPath = Path.Combine(_translationOptions.OutputFolder, Path.GetFileName(pdfPath));

        _log.LogInformation("Extracting PDF text: {Pdf}", pdfPath);
        var raw = _extractor.Extract(pdfPath);

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Extracted text is empty. If PDF is scanned, OCR is required.");

        string normalized = PdfTextPreprocessor.Preprocess(raw);

        IReadOnlyList<TranslationChunk> chunksRaw = _chunker.Chunk(normalized, _translationOptions.MaxCharsPerChunk);

        _log.LogInformation(
            "Chunking done. Chunks={Count}, MaxCharsPerChunk={Cpc}, Parallelism={Par}",
            chunksRaw.Count, _translationOptions.MaxCharsPerChunk, _translationOptions.MaxDegreeOfParallelism);

        List<TranslationChunk> chunks = chunksRaw
            .Select(c => new TranslationChunk(c.Index, c.Text))
            .ToList();

        string CacheKeyFactory(TranslationChunk ch)
        {
            var rawKey = $"{_openAiOptions}/{_translationOptions}/{_testingOptions}";
            return CacheKeyBuilder.Build(rawKey);
        }

        _log.LogInformation("Translating... (Resume={Resume}, Checkpoints={Dir})", _translationOptions.Resume, _translationOptions.CheckpointDir);

        var (mergedText, results) = await _orchestrator.RunAsync(
            sourcePdfPath: pdfPath,
            targetLanguage: _translationOptions.TargetLanguage,
            chunks: chunks,
            cacheKeyFactory: CacheKeyFactory,
            ct: ct);

        int success = results.Count(r => r.Status == ChunkStatus.Success);
        int quarantined = results.Count(r => r.Status == ChunkStatus.Quarantined);
        int failed = results.Count(r => r.Status == ChunkStatus.Failed);

        _log.LogInformation("Translation finished. Success={S}, Quarantined={Q}, Failed={F}", success, quarantined,
            failed);

        _log.LogInformation("Writing output: {Out}", outputPath);
        mergedText = TextSanitizer.SanitizeModelOutput(mergedText);
        await _writer.WriteAsync(mergedText, outputPath, ct);

        _log.LogInformation("Completed.");
    }
}