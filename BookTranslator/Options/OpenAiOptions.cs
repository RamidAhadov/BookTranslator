namespace BookTranslator.Options;

public sealed class OpenAiOptions
{
    public string BaseUrl { get; set; }
    public string Model { get; set; }
    public int MaxOutputTokens { get; set; }
    public double Temperature { get; set; }
    public string SystemPrompt { get; set; }
    
    public override string ToString()
    {
        return $"Model={Model}, Tokens={MaxOutputTokens}, Temp={Temperature}, " +
               $"BaseUrl={BaseUrl}, Prompt={SystemPrompt}";
    }
}