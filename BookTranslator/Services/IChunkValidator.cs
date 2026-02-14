namespace BookTranslator.Services;

public interface IChunkValidator
{
    (bool ok, string? reason) Validate(string input, string output);
}