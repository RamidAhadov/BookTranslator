using BookTranslator.Models;

namespace BookTranslator.Services;

public interface ICheckpointStore
{
    Task InitializeAsync(string sourcePdfPath, string targetLanguage, CancellationToken ct);

    Task<bool> HasSuccessAsync(int chunkIndex, CancellationToken ct);
    Task<string?> ReadOutputAsync(int chunkIndex, CancellationToken ct);

    Task MarkSuccessAsync(int chunkIndex, string output, CancellationToken ct);
    Task MarkFailedAsync(int chunkIndex, int attempts, string error, CancellationToken ct);
    Task MarkQuarantinedAsync(int chunkIndex, int attempts, string error, CancellationToken ct);

    Task<ChunkManifest> ReadManifestAsync(CancellationToken ct);
    Task WriteManifestAsync(ChunkManifest manifest, CancellationToken ct);

    string GetOutputPath(int chunkIndex);
    string GetErrorPath(int chunkIndex);
    string GetInputPath(int chunkIndex);
}