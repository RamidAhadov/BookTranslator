using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class OpenAiTranslator : ITranslator
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly OpenAiOptions _opt;
    private readonly ILogger<OpenAiTranslator> _log;

    public OpenAiTranslator(IHttpClientFactory httpFactory, IOptions<OpenAiOptions> options, ILogger<OpenAiTranslator> log)
    {
        _httpFactory = httpFactory;
        _opt = options.Value;
        _log = log;
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, string cacheKey, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");

        var client = _httpFactory.CreateClient("OpenAI");
        client.BaseAddress = new Uri(_opt.BaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        
        var payload = new
        {
            model = _opt.Model,
            temperature = _opt.Temperature,
            max_output_tokens = _opt.MaxOutputTokens,

            truncation = "auto",

            input = new object[]
            {
                new {
                    role = "system",
                    content = _opt.SystemPrompt.Replace("{{ target_language }}", targetLanguage)
                },
                new {
                    role = "user",
                    content =
                        $"Translate the following text to {targetLanguage}.\n" +
                        "Return only the translated text.\n\nTEXT:\n" + text
                }
            }
        };

        return await Retry.WithBackoffAsync(
            action: async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };

                using var resp = await client.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("OpenAI error {Status}: {Body}", (int)resp.StatusCode, body);
                    
                    throw new InvalidOperationException($"OpenAI API error {(int)resp.StatusCode}: {body}");
                }

                return extractOutputText(body);
            },
            maxAttempts: 5,
            shouldRetry: ex => true
        );
    }

    private static string extractOutputText(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unexpected response: missing 'output' array.");

        var sb = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeEl)) continue;
            if (typeEl.GetString() != "message") continue;

            if (!item.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var c in contentEl.EnumerateArray())
            {
                if (c.TryGetProperty("type", out var ctype) && ctype.GetString() == "output_text" &&
                    c.TryGetProperty("text", out var textEl))
                {
                    sb.Append(textEl.GetString());
                }
            }
        }

        var result = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("No translated text found in response.");

        return result;
    }
}
