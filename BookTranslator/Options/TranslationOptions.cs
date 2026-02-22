namespace BookTranslator.Options;

public sealed class TranslationOptions
{
    public string InputPath { get; set; }
    public string OutputFormat { get; set; }
    public string TranslatorProvider { get; set; } = "gemini";
    public bool IncludeImagesInPdf { get; set; } = true;
    public bool IncludeInvisibleTextLayer { get; set; } = true;
    public bool UseInvisibleTextAsFallbackOnly { get; set; } = true;
    public int InvisibleFallbackMinVisibleTextChars { get; set; } = 80;
    public int InvisibleFallbackMinVisibleFragments { get; set; } = 20;
    public float MinVisibleCharsPerFragmentForStrongLayer { get; set; } = 1.8f;
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
    public bool MergeLinesIntoParagraphs { get; set; } = false;
    public bool SuppressBackgroundPageImages { get; set; } = true;
    public float BackgroundImageMinPageCoverage { get; set; } = 0.62f;
    public float MaxKeptImageCoverageOnTextPages { get; set; } = 0.45f;
    public int BackgroundImageMinTextBlocks { get; set; } = 3;
    public int BackgroundImageMinTextChars { get; set; } = 120;
    public float BackgroundImageEdgeTolerance { get; set; } = 16f;
    public string TargetLanguage { get; set; }
    public string OutputFolder { get; set; }
    public string? PageSelection { get; set; }
    public bool ForceRetranslateSelectedPages { get; set; } = true;

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
               $"IncludeInvisibleTextLayer={IncludeInvisibleTextLayer}, " +
               $"UseInvisibleTextAsFallbackOnly={UseInvisibleTextAsFallbackOnly}, " +
               $"InvisibleFallbackMinVisibleTextChars={InvisibleFallbackMinVisibleTextChars}, " +
               $"InvisibleFallbackMinVisibleFragments={InvisibleFallbackMinVisibleFragments}, " +
               $"MinVisibleCharsPerFragmentForStrongLayer={MinVisibleCharsPerFragmentForStrongLayer}, " +
               $"MaxOccurrencesPerImageSignature={MaxOccurrencesPerImageSignature}, MaxPdfImagesPerPage={MaxPdfImagesPerPage}, " +
               $"MinPdfImageDisplayWidth={MinPdfImageDisplayWidth}, MinPdfImageDisplayHeight={MinPdfImageDisplayHeight}, " +
               $"OverlayMergeDistancePx={OverlayMergeDistancePx}, DynamicFontScaling={EnableDynamicFontScaling}, MinAutoFontSize={MinAutoFontSize}, " +
               $"MergeLinesIntoParagraphs={MergeLinesIntoParagraphs}, " +
               $"SuppressBackgroundPageImages={SuppressBackgroundPageImages}, BackgroundImageMinPageCoverage={BackgroundImageMinPageCoverage}, " +
               $"MaxKeptImageCoverageOnTextPages={MaxKeptImageCoverageOnTextPages}, " +
               $"BackgroundImageMinTextBlocks={BackgroundImageMinTextBlocks}, BackgroundImageMinTextChars={BackgroundImageMinTextChars}, " +
               $"BackgroundImageEdgeTolerance={BackgroundImageEdgeTolerance}, PageSelection={PageSelection}, " +
               $"ForceRetranslateSelectedPages={ForceRetranslateSelectedPages}";
    }
}
