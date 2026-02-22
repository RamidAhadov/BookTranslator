using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookTranslator.Models.Layout;
using BookTranslator.Options;
using BookTranslator.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class GptTranslatorService : ITranslatorService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenAiOptions _openAi;
    private readonly ILogger<GptTranslatorService> _log;

    public GptTranslatorService(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenAiOptions> openAi,
        ILogger<GptTranslatorService> log)
    {
        _httpClientFactory = httpClientFactory;
        _openAi = openAi.Value;
        _log = log;
    }

    public string ProviderName => "gpt";

    public async Task<IReadOnlyList<TranslatedTextItem>> TranslatePageAsync(PageObject page, string targetLanguage, CancellationToken ct)
    {
        if (page.TextBlocks.Count == 0)
            return Array.Empty<TranslatedTextItem>();

        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");

        HttpClient client = _httpClientFactory.CreateClient("OpenAI");
        client.BaseAddress = new Uri(_openAi.BaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var blocks = page.TextBlocks.Select(x => new
        {
            block_id = x.BlockId,
            original_text = TextSanitizer.CleanPdfArtifacts(x.OriginalText)
        });

        string blocksJson = JsonSerializer.Serialize(blocks, JsonOptions);

        var payload = new
        {
            model = _openAi.Model,
            temperature = _openAi.Temperature,
            max_output_tokens = _openAi.MaxOutputTokens,
            input = new object[]
            {
                new
                {
                    role = "system",
                    content = "Translate structured text blocks and return only JSON array."
                },
                new
                {
                    role = "user",
                    content =
                        $"Translate all `original_text` fields into {targetLanguage}. " +
                        "Preserve block_id values exactly. " +
                        "Return JSON only in shape: [{\"original_text\":\"...\",\"translated_text\":\"...\",\"block_id\":\"...\"}].\n\n" +
                        "Input blocks:\n" + blocksJson
                }
            }
        };

        using HttpRequestMessage req = new(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage resp = await client.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI translation failed ({(int)resp.StatusCode}): {body}");

        string text = ExtractOutputText(body);
        List<TranslatedTextItem> parsed = ParseTranslationItems(text);

        _log.LogInformation("GPT page translation completed. Page={Page}, Items={Items}", page.PageNumber, parsed.Count);

        Dictionary<string, TranslatedTextItem> byId = parsed
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(x => x.BlockId, x => x, StringComparer.Ordinal);

        List<TranslatedTextItem> result = new(page.TextBlocks.Count);

        foreach (TextBlock block in page.TextBlocks)
        {
            if (byId.TryGetValue(block.BlockId, out TranslatedTextItem? found))
            {
                result.Add(found);
                continue;
            }

            result.Add(new TranslatedTextItem(block.BlockId, block.OriginalText, block.OriginalText));
        }

        return result;
    }

    private static string ExtractOutputText(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenAI response missing output array.");

        StringBuilder sb = new();

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement typeEl) || typeEl.GetString() != "message")
                continue;

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
            throw new InvalidOperationException("OpenAI returned empty output text.");

        return result;
    }

    private static List<TranslatedTextItem> ParseTranslationItems(string modelText)
    {
        string json = ExtractJsonArray(modelText);
        List<TranslationRow>? rows = JsonSerializer.Deserialize<List<TranslationRow>>(json, JsonOptions);

        if (rows is null || rows.Count == 0)
            throw new InvalidOperationException("Model returned empty translation array.");

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.block_id))
            .Select(r => new TranslatedTextItem(
                r.block_id!.Trim(),
                (r.original_text ?? string.Empty).Trim(),
                TextSanitizer.CleanPdfArtifacts((r.translated_text ?? string.Empty).Trim())))
            .Where(x => !string.IsNullOrWhiteSpace(x.TranslatedText))
            .ToList();
    }

    private static string ExtractJsonArray(string modelText)
    {
        string trimmed = modelText.Trim();
        trimmed = Regex.Replace(trimmed, "^```(?:json)?", string.Empty, RegexOptions.IgnoreCase).Trim();
        trimmed = Regex.Replace(trimmed, "```$", string.Empty, RegexOptions.IgnoreCase).Trim();

        int start = trimmed.IndexOf('[');
        int end = trimmed.LastIndexOf(']');

        if (start < 0 || end < start)
            throw new InvalidOperationException("Model output does not include JSON array.");

        return trimmed[start..(end + 1)];
    }

    private sealed record TranslationRow(string? block_id, string? original_text, string? translated_text);
}