using BookTranslator.Models;
using System.Text;
using BookTranslator.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class ParagraphTextChunker : Chunker, ITextChunker
{
    public ParagraphTextChunker(IOptions<TestingOptions> testingOptions, ILogger<ParagraphTextChunker> logger) : base(testingOptions, logger)
    {
        
    }
    
    public IReadOnlyList<TranslationChunk> Chunk(string text, int maxCharsPerChunk)
    {
        if (maxCharsPerChunk <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharsPerChunk));

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim())
                             .Where(p => p.Length > 0)
                             .ToArray();

        var chunks = new List<TranslationChunk>();
        var sb = new StringBuilder();
        var idx = 0;

        foreach (var p in paragraphs)
        {
            if (p.Length > maxCharsPerChunk)
            {
                FlushIfNeeded();
                foreach (var part in splitLargeParagraph(p, maxCharsPerChunk))
                    chunks.Add(new TranslationChunk(idx++, part));
                continue;
            }

            if (sb.Length > 0 && sb.Length + 2 + p.Length > maxCharsPerChunk)
                FlushIfNeeded();

            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(p);
        }

        FlushIfNeeded();
        
        return base.filterChunks(chunks);

        void FlushIfNeeded()
        {
            if (sb.Length == 0) return;
            chunks.Add(new TranslationChunk(idx++, sb.ToString()));
            sb.Clear();
        }
    }

    private static IEnumerable<string> splitLargeParagraph(string p, int maxChars)
    {
        var sentences = System.Text.RegularExpressions.Regex
            .Split(p, @"(?<=[\.\!\?])\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        var sb = new StringBuilder();
        foreach (var s in sentences)
        {
            if (sb.Length > 0 && sb.Length + 1 + s.Length > maxChars)
            {
                yield return sb.ToString();
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }

        if (sb.Length > 0) yield return sb.ToString();
    }
}
