namespace BookTranslator.Options;

public sealed class OcrOptions
{
    public bool Enabled { get; set; } = false;
    public string Provider { get; set; } = "azure-read";
    public string AzureEndpoint { get; set; } = string.Empty;
    public string AzureApiKey { get; set; } = string.Empty;
    public string AzureApiVersion { get; set; } = "v3.2";
    public int PollIntervalMs { get; set; } = 1200;
    public int MaxPollAttempts { get; set; } = 12;
    public int MaxImagesPerBatch { get; set; } = 1;
    public int MinImageBytesForOcr { get; set; } = 1024;
    public int MaxRequestsPerMinute { get; set; } = 18;
    public int RetryAfter429Ms { get; set; } = 4000;
    public double MinLineConfidence { get; set; } = 0.55;
    public bool DropVerticalText { get; set; } = true;
}
