namespace BookTranslator.Models;

public sealed class ChunkManifest
{
    public string RunHash { get; set; } = "";
    public string SourcePdfPath { get; set; } = "";
    public string BookName { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxOutputTokens { get; set; }
    public string TargetLanguage { get; set; } = "";
    public int MaxCharsPerChunk { get; set; }
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
