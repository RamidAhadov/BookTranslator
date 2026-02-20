using System.Text;
using System.Text.Json;
using BookTranslator.Models;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class FileCheckpointStore : ICheckpointStore
{
    private readonly TranslationOptions _translation;
    private readonly OpenAiOptions _openAi;
    private readonly JsonSerializerOptions _jsonOpt = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _mapSync = new();
    private ChunkIndexMap _map = new();
    private bool _mapLoaded;

    private string _baseRoot = "";
    private string _runRoot = "";
    private string _manifestPath = "";
    private string _globalMapPath = "";

    private string _runHash = "";
    private string _sourcePdfPath = "";
    private string _bookName = "";
    private string _targetLanguage = "";

    public FileCheckpointStore(IOptions<TranslationOptions> translation, IOptions<OpenAiOptions> openAi)
    {
        _translation = translation.Value;
        _openAi = openAi.Value;
    }

    public Task InitializeAsync(string sourcePdfPath, string targetLanguage, CancellationToken ct)
    {
        _sourcePdfPath = sourcePdfPath;
        _bookName = Path.GetFileNameWithoutExtension(sourcePdfPath);
        _targetLanguage = targetLanguage;

        _baseRoot = Path.GetFullPath(_translation.CheckpointDir);
        _globalMapPath = Path.Combine(_baseRoot, "chunk-map.json");

        string runIdentity =
            $"book={_bookName}|model={_openAi.Model}|tokens={_openAi.MaxOutputTokens}|lang={targetLanguage}|maxchars={_translation.MaxCharsPerChunk}";
        _runHash = CacheKeyBuilder.Build(runIdentity);

        _runRoot = Path.Combine(_baseRoot, "runs", _runHash);
        Directory.CreateDirectory(_baseRoot);
        Directory.CreateDirectory(Path.Combine(_baseRoot, "runs"));
        Directory.CreateDirectory(_runRoot);
        Directory.CreateDirectory(Path.Combine(_runRoot, "chunks"));
        Directory.CreateDirectory(Path.Combine(_runRoot, "errors"));

        _manifestPath = Path.Combine(_runRoot, "manifest.json");

        EnsureMapLoaded();
        return EnsureManifestAsync(ct);
    }

    public string GetOutputPath(int chunkIndex)
    {
        string chunkHash = GetChunkHash(chunkIndex);
        EnsureChunkMapped(chunkIndex, chunkHash);
        return Path.Combine(_runRoot, "chunks", $"{chunkHash}.output.txt");
    }

    public string GetInputPath(int chunkIndex)
    {
        string chunkHash = GetChunkHash(chunkIndex);
        EnsureChunkMapped(chunkIndex, chunkHash);
        return Path.Combine(_runRoot, "chunks", $"{chunkHash}.input.txt");
    }

    public string GetErrorPath(int chunkIndex)
    {
        string chunkHash = GetChunkHash(chunkIndex);
        EnsureChunkMapped(chunkIndex, chunkHash);
        return Path.Combine(_runRoot, "errors", $"{chunkHash}.error.json");
    }

    public Task<bool> HasSuccessAsync(int chunkIndex, CancellationToken ct)
        => Task.FromResult(File.Exists(GetOutputPath(chunkIndex)));

    public async Task<string?> ReadOutputAsync(int chunkIndex, CancellationToken ct)
    {
        string p = GetOutputPath(chunkIndex);
        if (!File.Exists(p)) return null;
        return await File.ReadAllTextAsync(p, Encoding.UTF8, ct);
    }

    public async Task MarkSuccessAsync(int chunkIndex, string output, CancellationToken ct)
    {
        await AtomicFile.WriteAllTextAtomicAsync(GetOutputPath(chunkIndex), output, Encoding.UTF8, ct);
    }

    public async Task MarkFailedAsync(int chunkIndex, int attempts, string error, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { chunkIndex, attempts, status = "failed", error }, _jsonOpt);
        await AtomicFile.WriteAllTextAtomicAsync(GetErrorPath(chunkIndex), payload, Encoding.UTF8, ct);
    }

    public async Task MarkQuarantinedAsync(int chunkIndex, int attempts, string error, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { chunkIndex, attempts, status = "quarantined", error }, _jsonOpt);
        await AtomicFile.WriteAllTextAtomicAsync(GetErrorPath(chunkIndex), payload, Encoding.UTF8, ct);
    }

    public async Task<ChunkManifest> ReadManifestAsync(CancellationToken ct)
    {
        if (!File.Exists(_manifestPath))
            return new ChunkManifest();

        string json = await File.ReadAllTextAsync(_manifestPath, Encoding.UTF8, ct);
        return JsonSerializer.Deserialize<ChunkManifest>(json, _jsonOpt) ?? new ChunkManifest();
    }

    public async Task WriteManifestAsync(ChunkManifest manifest, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(manifest, _jsonOpt);
        await AtomicFile.WriteAllTextAtomicAsync(_manifestPath, json, Encoding.UTF8, ct);
    }

    private async Task EnsureManifestAsync(CancellationToken ct)
    {
        if (File.Exists(_manifestPath)) return;

        ChunkManifest m = new ChunkManifest
        {
            RunHash = _runHash,
            SourcePdfPath = _sourcePdfPath,
            BookName = _bookName,
            Model = _openAi.Model,
            MaxOutputTokens = _openAi.MaxOutputTokens,
            TargetLanguage = _targetLanguage,
            MaxCharsPerChunk = _translation.MaxCharsPerChunk,
            StartedAt = DateTimeOffset.UtcNow
        };
        await WriteManifestAsync(m, ct);
    }

    private string GetChunkHash(int chunkIndex)
    {
        string identity =
            $"run={_runHash}|book={_bookName}|model={_openAi.Model}|tokens={_openAi.MaxOutputTokens}|lang={_targetLanguage}|maxchars={_translation.MaxCharsPerChunk}|idx={chunkIndex:D5}";
        return CacheKeyBuilder.Build(identity);
    }

    private void EnsureMapLoaded()
    {
        lock (_mapSync)
        {
            if (_mapLoaded)
                return;

            if (File.Exists(_globalMapPath))
            {
                string json = File.ReadAllText(_globalMapPath, Encoding.UTF8);
                _map = JsonSerializer.Deserialize<ChunkIndexMap>(json, _jsonOpt) ?? new ChunkIndexMap();
            }
            else
            {
                _map = new ChunkIndexMap();
            }

            _mapLoaded = true;
        }
    }

    private void EnsureChunkMapped(int chunkIndex, string chunkHash)
    {
        lock (_mapSync)
        {
            if (!_mapLoaded)
                EnsureMapLoaded();

            string inputPath = Path.Combine(_runRoot, "chunks", $"{chunkHash}.input.txt");
            string outputPath = Path.Combine(_runRoot, "chunks", $"{chunkHash}.output.txt");
            string errorPath = Path.Combine(_runRoot, "errors", $"{chunkHash}.error.json");

            bool changed = false;
            if (!_map.Entries.TryGetValue(chunkHash, out var entry))
            {
                entry = new ChunkIndexMapEntry
                {
                    ChunkHash = chunkHash,
                    RunHash = _runHash,
                    ChunkIndex = chunkIndex,
                    SourcePdfPath = _sourcePdfPath,
                    BookName = _bookName,
                    Model = _openAi.Model,
                    MaxOutputTokens = _openAi.MaxOutputTokens,
                    TargetLanguage = _targetLanguage,
                    MaxCharsPerChunk = _translation.MaxCharsPerChunk,
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    ErrorPath = errorPath,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                _map.Entries[chunkHash] = entry;
                changed = true;
            }
            else
            {
                // Keep the map fresh if metadata changes for any reason.
                if (entry.ChunkIndex != chunkIndex ||
                    !string.Equals(entry.RunHash, _runHash, StringComparison.Ordinal) ||
                    !string.Equals(entry.SourcePdfPath, _sourcePdfPath, StringComparison.Ordinal) ||
                    !string.Equals(entry.Model, _openAi.Model, StringComparison.Ordinal) ||
                    entry.MaxOutputTokens != _openAi.MaxOutputTokens ||
                    !string.Equals(entry.TargetLanguage, _targetLanguage, StringComparison.Ordinal) ||
                    entry.MaxCharsPerChunk != _translation.MaxCharsPerChunk ||
                    !string.Equals(entry.InputPath, inputPath, StringComparison.Ordinal) ||
                    !string.Equals(entry.OutputPath, outputPath, StringComparison.Ordinal) ||
                    !string.Equals(entry.ErrorPath, errorPath, StringComparison.Ordinal))
                {
                    entry.RunHash = _runHash;
                    entry.ChunkIndex = chunkIndex;
                    entry.SourcePdfPath = _sourcePdfPath;
                    entry.BookName = _bookName;
                    entry.Model = _openAi.Model;
                    entry.MaxOutputTokens = _openAi.MaxOutputTokens;
                    entry.TargetLanguage = _targetLanguage;
                    entry.MaxCharsPerChunk = _translation.MaxCharsPerChunk;
                    entry.InputPath = inputPath;
                    entry.OutputPath = outputPath;
                    entry.ErrorPath = errorPath;
                    entry.UpdatedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                _map.UpdatedAt = DateTimeOffset.UtcNow;
                string json = JsonSerializer.Serialize(_map, _jsonOpt);
                File.WriteAllText(_globalMapPath, json, Encoding.UTF8);
            }
        }
    }
}
