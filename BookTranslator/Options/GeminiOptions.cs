namespace BookTranslator.Options;

public sealed class GeminiOptions
{
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";
    public string Model { get; set; } = "gemini-1.5-pro";
    public double Temperature { get; set; } = 0;
    public int MaxOutputTokens { get; set; } = 8192;
    public string ApiKeyEnvironmentVariable { get; set; } = "GEMINI_API_KEY";
    public bool EnableRateLimit { get; set; } = false;
    public double MaxRequestsPerSecond { get; set; } = 2;
    public int MaxBlocksPerRequest { get; set; } = 10;
    public int MaxInputCharsPerRequest { get; set; } = 6000;
}
