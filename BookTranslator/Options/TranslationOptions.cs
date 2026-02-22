namespace BookTranslator.Options;

public sealed class TranslationOptions
{
    public string InputPath { get; set; }
    public string OutputFormat { get; set; }
    public string TranslatorProvider { get; set; } = "gemini";
    public bool IncludeImagesInPdf { get; set; } = true;
    public bool DeduplicatePdfImages { get; set; } = true;
    public int MaxOccurrencesPerImageSignature { get; set; } = 1;
    public int MaxPdfImagesPerPage { get; set; } = 6;
    public float MinPdfImageDisplayWidth { get; set; } = 15;
    public float MinPdfImageDisplayHeight { get; set; } = 15;
    public float OverlayMergeDistancePx { get; set; } = 5;
    public bool EnableDynamicFontScaling { get; set; } = true;
    public float MinAutoFontSize { get; set; } = 7;
    public float FontScaleStep { get; set; } = 0.5f;
    public bool ClearOriginalTextArea { get; set; } = true;
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

    public override string ToString()
    {
        return $"Input={Path.GetFileName(InputPath)}, Target={TargetLanguage}, Format={OutputFormat}, Provider={TranslatorProvider}, " +
               $"Chunk={MaxCharsPerChunk}, Parallel={MaxDegreeOfParallelism}, Resume={Resume}, " +
               $"IncludeImagesInPdf={IncludeImagesInPdf}, DeduplicatePdfImages={DeduplicatePdfImages}, " +
               $"MaxOccurrencesPerImageSignature={MaxOccurrencesPerImageSignature}, MaxPdfImagesPerPage={MaxPdfImagesPerPage}, " +
               $"MinPdfImageDisplayWidth={MinPdfImageDisplayWidth}, MinPdfImageDisplayHeight={MinPdfImageDisplayHeight}, " +
               $"OverlayMergeDistancePx={OverlayMergeDistancePx}, DynamicFontScaling={EnableDynamicFontScaling}, MinAutoFontSize={MinAutoFontSize}";
    }
}
