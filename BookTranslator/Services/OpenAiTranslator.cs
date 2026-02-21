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
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");

        HttpClient client = _httpFactory.CreateClient("OpenAI");
        client.BaseAddress = new Uri(_opt.BaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        
        object payload = new
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
                        "Preserve tokens matching __IMG_00001__ pattern exactly as-is (do not translate or alter them).\n" +
                        "If such token appears, keep it as a standalone block line.\n" +
                        "Return only the translated text.\n\nTEXT:\n" + text
                }
            }
        };

        return await Retry.WithBackoffAsync(
            action: async () =>
            {
                string reqContent = JsonSerializer.Serialize(payload);
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
                {
                    Content = new StringContent(reqContent, Encoding.UTF8, "application/json")
                };

                using HttpResponseMessage resp = await client.SendAsync(req, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);

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
        using JsonDocument doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unexpected response: missing 'output' array.");

        StringBuilder sb = new StringBuilder();

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement typeEl)) continue;
            if (typeEl.GetString() != "message") continue;

            if (!item.TryGetProperty("content", out JsonElement contentEl) || contentEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement c in contentEl.EnumerateArray())
            {
                if (c.TryGetProperty("type", out JsonElement ctype) && ctype.GetString() == "output_text" &&
                    c.TryGetProperty("text", out JsonElement textEl))
                {
                    sb.Append(textEl.GetString());
                }
            }
        }

        string result = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("No translated text found in response.");

        return result;
    }
}
