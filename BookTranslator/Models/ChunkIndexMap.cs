namespace BookTranslator.Models;

public sealed class ChunkIndexMap
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, ChunkIndexMapEntry> Entries { get; set; } = new();
}

public sealed class ChunkIndexMapEntry
{
    public string ChunkHash { get; set; } = "";
    public string RunHash { get; set; } = "";
    public int ChunkIndex { get; set; }

    public string SourcePdfPath { get; set; } = "";
    public string BookName { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxOutputTokens { get; set; }
    public string TargetLanguage { get; set; } = "";
    public int MaxCharsPerChunk { get; set; }

    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string ErrorPath { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
