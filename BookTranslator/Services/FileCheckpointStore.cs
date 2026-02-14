using System.Text;
using System.Text.Json;
using BookTranslator.Models;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class FileCheckpointStore : ICheckpointStore
{
    private readonly TranslationOptions _opt;
    private readonly JsonSerializerOptions _jsonOpt = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private string _root = "";
    private string _manifestPath = "";

    public FileCheckpointStore(IOptions<TranslationOptions> opt)
    {
        _opt = opt.Value;
    }

    public Task InitializeAsync(string sourcePdfPath, string targetLanguage, CancellationToken ct)
    {
        _root = Path.GetFullPath(_opt.CheckpointDir);

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "chunks"));
        Directory.CreateDirectory(Path.Combine(_root, "errors"));

        _manifestPath = Path.Combine(_root, "manifest.json");
        return EnsureManifestAsync(sourcePdfPath, targetLanguage, ct);
    }


    public string GetOutputPath(int chunkIndex) => Path.Combine(_root, "chunks", $"{chunkIndex:D5}.output.txt");
    public string GetInputPath(int chunkIndex) => Path.Combine(_root, "chunks", $"{chunkIndex:D5}.input.txt");
    public string GetErrorPath(int chunkIndex) => Path.Combine(_root, "errors", $"{chunkIndex:D5}.error.json");

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

    private async Task EnsureManifestAsync(string sourcePdfPath, string targetLanguage, CancellationToken ct)
    {
        if (File.Exists(_manifestPath)) return;

        ChunkManifest m = new ChunkManifest
        {
            SourcePdfPath = sourcePdfPath,
            TargetLanguage = targetLanguage,
            StartedAt = DateTimeOffset.UtcNow
        };
        await WriteManifestAsync(m, ct);
    }
}
