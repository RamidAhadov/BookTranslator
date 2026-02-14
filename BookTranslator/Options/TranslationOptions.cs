namespace BookTranslator.Options;

public sealed class TranslationOptions
{
    public string InputPath { get; set; }
    public string OutputFormat { get; set; }
    public string TargetLanguage { get; set; }
    public string OutputFolder { get; set; }

    public int MaxCharsPerChunk { get; set; }
    public int MaxDegreeOfParallelism { get; set; }

    public string CheckpointDir { get; set; }
    public bool Resume { get; set; }

    public int MaxAttemptsPerChunk { get; set; }
    public int QuarantineAfterAttempts { get; set; }

    public double MinOutputToInputRatio { get; set; }
    public int MinOutputChars { get; set; }

    public string FontStyle { get; set; }
    public string FontStyleExtension { get; set; }

    public override string ToString()
    {
        return $"Input={Path.GetFileName(InputPath)}, Target={TargetLanguage}, Format={OutputFormat}, " +
               $"Chunk={MaxCharsPerChunk}, Parallel={MaxDegreeOfParallelism}, Resume={Resume}";
    }
}