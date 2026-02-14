namespace BookTranslator.Options;

public sealed class TestingOptions
{
    public List<int>? TestChunks { get; set; }
    
    public override string ToString()
    {
        return $"TestChunks=[{string.Join(",", TestChunks ?? [])}]";
    }
}