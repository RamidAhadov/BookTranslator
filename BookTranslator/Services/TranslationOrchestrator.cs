using System.Diagnostics;
using System.Text;
using BookTranslator.Models;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class TranslationOrchestrator
{
    private readonly ITranslator _translator;
    private readonly ICheckpointStore _store;
    private readonly IChunkValidator _validator;
    private readonly TranslationOptions _opt;
    private readonly OpenAiOptions _openAiOpt;
    private readonly ILogger<TranslationOrchestrator> _log;

    public TranslationOrchestrator(
        ITranslator translator,
        ICheckpointStore store,
        IChunkValidator validator,
        IOptions<TranslationOptions> opt,
        IOptions<OpenAiOptions> openAiOpt,
        ILogger<TranslationOrchestrator> log)
    {
        _translator = translator;
        _store = store;
        _validator = validator;
        _opt = opt.Value;
        _openAiOpt = openAiOpt.Value;
        _log = log;
    }

    public async Task<(string mergedText, List<ChunkResult> results)> RunAsync(
        string sourcePdfPath,
        string targetLanguage,
        IReadOnlyList<TranslationChunk> chunks,
        Func<TranslationChunk, string> cacheKeyFactory,
        CancellationToken ct)
    {
        await _store.InitializeAsync(sourcePdfPath, targetLanguage, ct);
        ChunkManifest manifest = await _store.ReadManifestAsync(ct);

        ChunkResult[] results = new ChunkResult[chunks.Count];
        SemaphoreSlim sem = new SemaphoreSlim(_opt.MaxDegreeOfParallelism);

        Task[] tasks = chunks.Select(async chunk =>
        {
            await sem.WaitAsync(ct);
            try
            {
                results[chunk.Index] = await processChunkAsync(chunk, targetLanguage, manifest, cacheKeyFactory, ct);
            }
            finally
            {
                sem.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        await _store.WriteManifestAsync(manifest, ct);

        StringBuilder merged = new StringBuilder();
        StringBuilder review = new StringBuilder();

        for (int i = 0; i < results.Length; i++)
        {
            ChunkResult r = results[i];
            if (r.Status == ChunkStatus.Success && !string.IsNullOrWhiteSpace(r.Output))
            {
                if (merged.Length > 0) merged.Append("\n\n");
                merged.Append(r.Output);
            }
            else
            {
                review.AppendLine($"----- CHUNK {i:D5} ({r.Status}) -----");
                if (!string.IsNullOrWhiteSpace(r.Error))
                    review.AppendLine(r.Error);
                review.AppendLine();
            }
        }

        if (review.Length > 0)
        {
            string reviewPath = Path.Combine(_opt.CheckpointDir, "TO_REVIEW.txt");
            await File.WriteAllTextAsync(reviewPath, review.ToString(), Encoding.UTF8, ct);
        }

        return (merged.ToString(), results.ToList());
    }

    private async Task<ChunkResult> processChunkAsync(
        TranslationChunk chunk,
        string targetLanguage,
        ChunkManifest manifest,
        Func<TranslationChunk, string> cacheKeyFactory,
        CancellationToken ct)
    {
        string inputPath = _store.GetInputPath(chunk.Index);
        bool currentInputMatchesPrevious = await HasSameInputAsync(inputPath, chunk.Text, ct);

        if (_opt.Resume && currentInputMatchesPrevious && await _store.HasSuccessAsync(chunk.Index, ct))
        {
            string? output = await _store.ReadOutputAsync(chunk.Index, ct);
            if (!string.IsNullOrWhiteSpace(output))
            {
                output = TextSanitizer.SanitizeModelOutput(output);
                var (ok, _) = _validator.Validate(chunk.Text, output);
                if (ok)
                {
                    _log.LogInformation("Chunk {Idx} skipped (resume).", chunk.Index);
                    upsert(manifest, chunk.Index, ChunkStatus.Success, attempts: 0, lastError: null);
                    return new ChunkResult(chunk.Index, ChunkStatus.Success, output, null, 0);
                }
            }
        }

        await File.WriteAllTextAsync(inputPath, chunk.Text, Encoding.UTF8, ct);

        int attempts = 0;
        Exception? lastEx = null;
        List<string> modelSequence = BuildModelSequence(_openAiOpt);

        while (attempts < _opt.MaxAttemptsPerChunk)
        {
            attempts++;
            try
            {
                string cacheKey = cacheKeyFactory(chunk);
                string selectedModel = modelSequence[(attempts - 1) % modelSequence.Count];
                string fingerprint = $"{cacheKey}|attempt={attempts}|model={selectedModel}|chunk={chunk.Index:D5}";

                Stopwatch sw = Stopwatch.StartNew();
                _log.LogInformation("Chunk {Idx} attempt {Attempt}: using model {Model}.", chunk.Index, attempts, selectedModel);

                string output = await _translator.TranslateAsync(
                    chunk.Text,
                    targetLanguage,
                    cacheKey,
                    modelOverride: selectedModel,
                    attemptFingerprint: fingerprint,
                    ct: ct);
                output = TextSanitizer.SanitizeModelOutput(output);
                sw.Stop();

                var (ok, reason) = _validator.Validate(chunk.Text, output);
                if (!ok)
                    throw new InvalidOperationException($"Validation failed: {reason}");

                await _store.MarkSuccessAsync(chunk.Index, output, ct);
                upsert(manifest, chunk.Index, ChunkStatus.Success, attempts, null);

                _log.LogInformation("Chunk {Idx} OK in {Ms}ms (attempt {Attempt}).",
                    chunk.Index, sw.ElapsedMilliseconds, attempts);

                return new ChunkResult(chunk.Index, ChunkStatus.Success, output, null, attempts);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                string msg = ex.Message;
                bool validationFailure = msg.Contains("Validation failed:", StringComparison.OrdinalIgnoreCase);

                if (msg.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase))
                {
                    upsert(manifest, chunk.Index, ChunkStatus.Failed, attempts, msg);
                    await _store.MarkFailedAsync(chunk.Index, attempts, msg, ct);
                    throw;
                }

                if (msg.Contains("OpenAI API error 400", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains(" 400", StringComparison.OrdinalIgnoreCase))
                {
                    upsert(manifest, chunk.Index, ChunkStatus.Failed, attempts, msg);
                    await _store.MarkFailedAsync(chunk.Index, attempts, msg, ct);
                    return new ChunkResult(chunk.Index, ChunkStatus.Failed, null, msg, attempts);
                }

                _log.LogWarning(ex, "Chunk {Idx} failed attempt {Attempt}/{Max}.",
                    chunk.Index, attempts, _opt.MaxAttemptsPerChunk);

                if (!validationFailure)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempts)), ct);
            }
        }

        string finalError = lastEx?.ToString() ?? "Unknown error";
        upsert(manifest, chunk.Index, ChunkStatus.Quarantined, attempts, finalError);
        await _store.MarkQuarantinedAsync(chunk.Index, attempts, finalError, ct);

        return new ChunkResult(chunk.Index, ChunkStatus.Quarantined, null, finalError, attempts);
    }

    private static async Task<bool> HasSameInputAsync(string inputPath, string currentInput, CancellationToken ct)
    {
        if (!File.Exists(inputPath))
            return false;

        string previousInput = await File.ReadAllTextAsync(inputPath, Encoding.UTF8, ct);
        return string.Equals(previousInput, currentInput, StringComparison.Ordinal);
    }

    private static void upsert(ChunkManifest manifest, int index, ChunkStatus status, int attempts, string? lastError)
    {
        if (!manifest.Items.TryGetValue(index, out var item))
        {
            item = new ChunkManifestItem();
            manifest.Items[index] = item;
        }

        item.Status = status;
        item.Attempts = attempts;
        item.LastError = lastError;
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static List<string> BuildModelSequence(OpenAiOptions openAi)
    {
        List<string> models = new List<string>();

        if (!string.IsNullOrWhiteSpace(openAi.Model))
            models.Add(openAi.Model.Trim());

        if (openAi.FallbackModels is { Count: > 0 })
        {
            foreach (string m in openAi.FallbackModels)
            {
                string trimmed = m?.Trim() ?? string.Empty;
                if (trimmed.Length == 0)
                    continue;
                if (models.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    continue;

                models.Add(trimmed);
            }
        }

        if (models.Count == 0)
            models.Add("gpt-4o-mini");

        return models;
    }
}
