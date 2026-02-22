using BookTranslator.Options;
using BookTranslator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Yaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookTranslator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.Sources.Clear();

                cfg.AddYamlFile("config.yml", optional: false, reloadOnChange: true);

                cfg.AddEnvironmentVariables();
                cfg.AddCommandLine(args);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<OpenAiOptions>(ctx.Configuration.GetSection("OpenAI"));
                services.Configure<GeminiOptions>(ctx.Configuration.GetSection("Gemini"));
                services.Configure<OcrOptions>(ctx.Configuration.GetSection("OCR"));
                services.Configure<TranslationOptions>(ctx.Configuration.GetSection("Translation"));
                services.Configure<TestingOptions>(ctx.Configuration.GetSection("Testing"));
                services.Configure<FontOptions>(ctx.Configuration.GetSection("Font"));

                services.AddHttpClient("OpenAI");
                services.AddHttpClient("Gemini");
                services.AddHttpClient("AzureOcr");

                services.AddSingleton<AzureReadOcrService>();
                services.AddSingleton<NullOcrService>();
                services.AddSingleton<IOcrService>(sp =>
                {
                    OcrOptions ocr = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OcrOptions>>().Value;
                    bool enabled = ocr.Enabled && string.Equals(ocr.Provider, "azure-read", StringComparison.OrdinalIgnoreCase);
                    return enabled ? sp.GetRequiredService<AzureReadOcrService>() : sp.GetRequiredService<NullOcrService>();
                });

                services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
                services.AddSingleton<ITextChunker, ParagraphTextChunker>();
                services.AddSingleton<ITranslator, OpenAiTranslator>();

                services.AddSingleton<ICheckpointStore, FileCheckpointStore>();
                services.AddSingleton<IChunkValidator, BasicChunkValidator>();
                services.AddSingleton<TranslationOrchestrator>();

                services.AddSingleton<TxtOutputWriter>();
                services.AddSingleton<PdfOutputWriter>();

                services.AddSingleton<IOutputWriter>(sp =>
                {
                    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TranslationOptions>>().Value;

                    return opt.OutputFormat.ToLowerInvariant() switch
                    {
                        "pdf" => sp.GetRequiredService<PdfOutputWriter>(),
                        _ => sp.GetRequiredService<TxtOutputWriter>()
                    };
                });

                services.AddSingleton<IPdfLayoutAnalyzer, PdfLayoutAnalyzer>();
                services.AddSingleton<GeminiMultimodalTranslator>();
                services.AddSingleton<GptTranslatorService>();
                services.AddSingleton<ILayoutCheckpointStore, FileLayoutCheckpointStore>();
                services.AddSingleton<ITranslatorService>(sp =>
                {
                    TranslationOptions opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TranslationOptions>>().Value;
                    return string.Equals(opt.TranslatorProvider, "gpt", StringComparison.OrdinalIgnoreCase)
                        ? sp.GetRequiredService<GptTranslatorService>()
                        : sp.GetRequiredService<GeminiMultimodalTranslator>();
                });
                services.AddSingleton<PdfReconstructor>();
                services.AddSingleton<LayoutTranslationPipeline>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                });
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        var pipeline = host.Services.GetRequiredService<LayoutTranslationPipeline>();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await pipeline.RunAsync(cts.Token);
        return 0;
    }
}
