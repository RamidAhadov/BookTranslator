namespace BookTranslator.Models;

public sealed record ChunkResult(
    int Index,
    ChunkStatus Status,
    string? Output,
    string? Error,
    int Attempts
);