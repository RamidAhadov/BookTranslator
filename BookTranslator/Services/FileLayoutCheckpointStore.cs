using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BookTranslator.Models.Layout;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class FileLayoutCheckpointStore : ILayoutCheckpointStore
{
    private static readonly LayoutCheckpointReadResult MissResult = new(false, Array.Empty<TranslatedTextItem>());

    private readonly TranslationOptions _translation;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SemaphoreSlim _sync = new(1, 1);

    private bool _initialized;
    private string _runRoot = "";
    private string _pagesRoot = "";
    private string _manifestPath = "";
    private LayoutRunManifest _manifest = new();

    public FileLayoutCheckpointStore(IOptions<TranslationOptions> translation)
    {
        _translation = translation.Value;
    }

    public async Task InitializeAsync(string sourcePdfPath, string targetLanguage, string providerName, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            string sourceFullPath = Path.GetFullPath(sourcePdfPath);
            string baseRoot = ResolveCheckpointRoot(_translation.CheckpointDir);

            string runIdentity =
                $"layout|source={sourceFullPath}|lang={targetLanguage}|provider={providerName}";
            string runHash = CacheKeyBuilder.Build(runIdentity);

            _runRoot = Path.Combine(baseRoot, "layout-runs", runHash);
            _pagesRoot = Path.Combine(_runRoot, "pages");
            _manifestPath = Path.Combine(_runRoot, "manifest.json");

            Directory.CreateDirectory(baseRoot);
            Directory.CreateDirectory(_runRoot);
            Directory.CreateDirectory(_pagesRoot);

            if (File.Exists(_manifestPath))
            {
                string json = await File.ReadAllTextAsync(_manifestPath, Encoding.UTF8, ct);
                _manifest = JsonSerializer.Deserialize<LayoutRunManifest>(json, _json) ?? new LayoutRunManifest();

                if (json.Contains("\\u", StringComparison.Ordinal))
                    await WriteManifestInternalAsync(ct);
            }
            else
            {
                _manifest = new LayoutRunManifest
                {
                    RunHash = runHash,
                    SourcePdfPath = sourceFullPath,
                    TargetLanguage = targetLanguage,
                    Provider = providerName,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await WriteManifestInternalAsync(ct);
            }

            _initialized = true;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<LayoutCheckpointReadResult> TryReadPageAsync(int pageNumber, string pageFingerprint, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            EnsureInitialized();

            if (!_manifest.Pages.TryGetValue(pageNumber, out LayoutPageManifestItem? pageItem))
                return MissResult;

            if (pageItem.Status != LayoutPageStatus.Success)
                return MissResult;

            string checkpointPath = GetPageCheckpointPath(pageNumber);
            if (!File.Exists(checkpointPath))
                return MissResult;

            string json = await File.ReadAllTextAsync(checkpointPath, Encoding.UTF8, ct);
            LayoutPageCheckpoint? checkpoint = JsonSerializer.Deserialize<LayoutPageCheckpoint>(json, _json);

            if (checkpoint is null)
                return MissResult;

            if (json.Contains("\\u", StringComparison.Ordinal))
            {
                string refreshed = JsonSerializer.Serialize(checkpoint, _json);
                await AtomicFile.WriteAllTextAtomicAsync(checkpointPath, refreshed, Encoding.UTF8, ct);
            }

            // Fingerprint mismatches are tolerated here so geometry-only changes can
            // still resume. Pipeline-level coverage checks verify block identity/text.
            return new LayoutCheckpointReadResult(true, checkpoint.Items);
        }
        catch
        {
            return MissResult;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SavePageSuccessAsync(
        int pageNumber,
        string pageFingerprint,
        IReadOnlyList<TranslatedTextItem> items,
        CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            EnsureInitialized();

            LayoutPageCheckpoint checkpoint = new()
            {
                PageNumber = pageNumber,
                PageFingerprint = pageFingerprint,
                Items = items.ToList(),
                UpdatedAt = DateTimeOffset.UtcNow
            };

            string pageJson = JsonSerializer.Serialize(checkpoint, _json);
            await AtomicFile.WriteAllTextAtomicAsync(
                GetPageCheckpointPath(pageNumber),
                pageJson,
                Encoding.UTF8,
                ct);

            _manifest.Pages[pageNumber] = new LayoutPageManifestItem
            {
                Status = LayoutPageStatus.Success,
                PageFingerprint = pageFingerprint,
                Error = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _manifest.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteManifestInternalAsync(ct);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SavePageFailureAsync(int pageNumber, string pageFingerprint, string error, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            EnsureInitialized();

            _manifest.Pages[pageNumber] = new LayoutPageManifestItem
            {
                Status = LayoutPageStatus.Failed,
                PageFingerprint = pageFingerprint,
                Error = error,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _manifest.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteManifestInternalAsync(ct);
        }
        finally
        {
            _sync.Release();
        }
    }

    private string GetPageCheckpointPath(int pageNumber)
    {
        return Path.Combine(_pagesRoot, $"page-{pageNumber:D4}.json");
    }

    private async Task WriteManifestInternalAsync(CancellationToken ct)
    {
        string manifestJson = JsonSerializer.Serialize(_manifest, _json);
        await AtomicFile.WriteAllTextAtomicAsync(_manifestPath, manifestJson, Encoding.UTF8, ct);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Checkpoint store is not initialized.");
    }

    private static string ResolveCheckpointRoot(string checkpointDir)
    {
        if (Path.IsPathRooted(checkpointDir))
            return Path.GetFullPath(checkpointDir);

        string baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, checkpointDir));
    }
}
