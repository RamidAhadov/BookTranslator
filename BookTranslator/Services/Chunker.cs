using BookTranslator.Models;
using BookTranslator.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public class Chunker
{
    private readonly TestingOptions _testingOptions;
    private readonly ILogger<ITextChunker> _logger;

    protected Chunker(IOptions<TestingOptions> testingOptions, ILogger<ITextChunker> logger)
    {
        _testingOptions = testingOptions.Value;
        _logger = logger;
    }
    
    protected TranslationChunk[] filterChunks(IReadOnlyList<TranslationChunk> chunks)
    {
        if (_testingOptions.TestChunks is { Count: > 0 })
        {
            _logger.LogInformation($"Testing chunks found: {_testingOptions.TestChunks.Count}");
            if (_testingOptions.TestChunks.Any(c => c >= chunks.Count))
            {
                _logger.LogWarning($"Testing chunks is equal or greater that chunks count. Chunks count: {chunks.Count}");
                return [];
            }

            TranslationChunk[] testingChunks = new TranslationChunk[_testingOptions.TestChunks.Count];
            int index = 0;
            foreach (int chunkIndex in _testingOptions.TestChunks)
            {
                TranslationChunk original = chunks[chunkIndex];

                TranslationChunk updated = original with { Index = index };
                testingChunks[index] = updated;
                index++;
            }
            
            _logger.LogInformation($"Testing chunks returned: {testingChunks.Length}");

            return testingChunks;
        }

        return chunks.ToArray();
    }
}