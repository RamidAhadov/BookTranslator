namespace BookTranslator.Models;

public sealed class ChunkManifest
{
    public string SourcePdfPath { get; set; } = "";
    public string TargetLanguage { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<int, ChunkManifestItem> Items { get; set; } = new();
}

public sealed class ChunkManifestItem
{
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}