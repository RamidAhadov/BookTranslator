namespace BookTranslator.Models.Layout;

public enum LayoutPageStatus
{
    Pending = 0,
    Success = 1,
    Failed = 2
}

public sealed class LayoutRunManifest
{
    public string RunHash { get; set; } = "";
    public string SourcePdfPath { get; set; } = "";
    public string TargetLanguage { get; set; } = "";
    public string Provider { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<int, LayoutPageManifestItem> Pages { get; set; } = new();
}

public sealed class LayoutPageManifestItem
{
    public LayoutPageStatus Status { get; set; } = LayoutPageStatus.Pending;
    public string PageFingerprint { get; set; } = "";
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LayoutPageCheckpoint
{
    public int PageNumber { get; set; }
    public string PageFingerprint { get; set; } = "";
    public List<TranslatedTextItem> Items { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
