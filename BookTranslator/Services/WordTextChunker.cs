using System.Text.RegularExpressions;
using BookTranslator.Models;
using BookTranslator.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class WordTextChunker : Chunker, ITextChunker
{
    public WordTextChunker(IOptions<TestingOptions> testingOptions, ILogger<WordTextChunker> logger) : base(testingOptions, logger)
    {
    }

    public IReadOnlyList<TranslationChunk> Chunk(string text, int wordsPerChunk)
    {
        if (wordsPerChunk <= 0) throw new ArgumentOutOfRangeException(nameof(wordsPerChunk));

        string[] words = Regex.Split(text, @"\s+")
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToArray();

        List<TranslationChunk> chunks = new List<TranslationChunk>();
        int idx = 0;

        for (int i = 0; i < words.Length; i += wordsPerChunk)
        {
            var slice = words.Skip(i).Take(wordsPerChunk);
            chunks.Add(new TranslationChunk(idx++, string.Join(" ", slice)));
        }

        return base.filterChunks(chunks);
    }
}