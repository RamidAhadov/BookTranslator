using BookTranslator.Options;
using BookTranslator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookTranslator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddEnvironmentVariables();
            })
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<OpenAiOptions>(ctx.Configuration.GetSection("OpenAI"));
                services.Configure<TranslationOptions>(ctx.Configuration.GetSection("Translation"));
                services.Configure<TestingOptions>(ctx.Configuration.GetSection("Testing"));

                services.AddHttpClient("OpenAI");

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

                services.AddSingleton<TranslationPipeline>();
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

        var pipeline = host.Services.GetRequiredService<TranslationPipeline>();

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
