using BookTranslator.Models;

namespace BookTranslator.Services;

public interface ITextChunker
{
    IReadOnlyList<TranslationChunk> Chunk(string text, int wordsPerChunk);
}