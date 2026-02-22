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

public sealed class GeminiMultimodalTranslator : ITranslatorService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GeminiOptions _gemini;
    private readonly ILogger<GeminiMultimodalTranslator> _log;
    private readonly SemaphoreSlim _rateLimiterGate = new(1, 1);
    private DateTimeOffset _nextAllowedRequestUtc = DateTimeOffset.MinValue;

    public GeminiMultimodalTranslator(
        IHttpClientFactory httpClientFactory,
        IOptions<GeminiOptions> gemini,
        ILogger<GeminiMultimodalTranslator> log)
    {
        _httpClientFactory = httpClientFactory;
        _gemini = gemini.Value;
        _log = log;
    }

    public string ProviderName => "gemini";

    public async Task<IReadOnlyList<TranslatedTextItem>> TranslatePageAsync(
        PageObject page,
        string targetLanguage,
        CancellationToken ct)
    {
        if (page.TextBlocks.Count == 0)
            return Array.Empty<TranslatedTextItem>();

        string apiKey = ReadApiKey();
        HttpClient client = _httpClientFactory.CreateClient("Gemini");
        client.BaseAddress = BuildBaseUri(_gemini.BaseUrl);

        string fileUri = await UploadPdfPageAsync(client, page, apiKey, ct);
        List<IReadOnlyList<TextBlock>> batches = BuildInitialBatches(page.TextBlocks);
        List<TranslatedTextItem> merged = new();

        _log.LogInformation(
            "Page {Page}: translating {Blocks} block(s) in {Batches} batch(es).",
            page.PageNumber,
            page.TextBlocks.Count,
            batches.Count);

        foreach (IReadOnlyList<TextBlock> batch in batches)
        {
            IReadOnlyList<TranslatedTextItem> translated = await TranslateBlocksAdaptiveAsync(
                client,
                page.PageNumber,
                batch,
                fileUri,
                targetLanguage,
                apiKey,
                ct);

            merged.AddRange(translated);
        }

        return NormalizeByKnownBlockIds(page, merged);
    }

    private string ReadApiKey()
    {
        string? value = Environment.GetEnvironmentVariable(_gemini.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{_gemini.ApiKeyEnvironmentVariable} is not set.");

        return value;
    }

    private async Task<string> UploadPdfPageAsync(HttpClient client, PageObject page, string apiKey, CancellationToken ct)
    {
        string startPath = $"upload/v1beta/files?key={Uri.EscapeDataString(apiKey)}";

        using HttpRequestMessage startReq = new(HttpMethod.Post, startPath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { file = new { display_name = $"page-{page.PageNumber:D4}.pdf" } }),
                Encoding.UTF8,
                "application/json")
        };

        startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startReq.Headers.Add("X-Goog-Upload-Command", "start");
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", page.SourcePagePdfBytes.Length.ToString());
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", "application/pdf");

        using HttpResponseMessage startResp = await SendWithRateLimitAsync(client, startReq, ct);
        string startBody = await startResp.Content.ReadAsStringAsync(ct);

        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini upload start failed ({(int)startResp.StatusCode}): {startBody}");

        if (!startResp.Headers.TryGetValues("X-Goog-Upload-URL", out IEnumerable<string>? uploadUrls))
            throw new InvalidOperationException("Gemini upload URL missing in X-Goog-Upload-URL header.");

        string? uploadUrl = uploadUrls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(uploadUrl))
            throw new InvalidOperationException("Gemini upload URL is empty.");

        using HttpRequestMessage uploadReq = new(HttpMethod.Post, uploadUrl)
        {
            Content = new ByteArrayContent(page.SourcePagePdfBytes)
        };

        uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        using HttpResponseMessage uploadResp = await SendWithRateLimitAsync(client, uploadReq, ct);
        string uploadBody = await uploadResp.Content.ReadAsStringAsync(ct);

        if (!uploadResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini upload finalize failed ({(int)uploadResp.StatusCode}): {uploadBody}");

        using JsonDocument uploadDoc = JsonDocument.Parse(uploadBody);

        if (!uploadDoc.RootElement.TryGetProperty("file", out JsonElement fileEl))
            throw new InvalidOperationException("Gemini upload response does not contain file object.");

        if (!fileEl.TryGetProperty("uri", out JsonElement uriEl))
            throw new InvalidOperationException("Gemini upload response does not contain file.uri.");

        string? uri = uriEl.GetString();
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidOperationException("Gemini file.uri is empty.");

        _log.LogInformation("Gemini page upload completed. Page={Page}, Uri={Uri}", page.PageNumber, uri);
        return uri;
    }

    private async Task<IReadOnlyList<TranslatedTextItem>> TranslateBlocksAdaptiveAsync(
        HttpClient client,
        int pageNumber,
        IReadOnlyList<TextBlock> blocks,
        string fileUri,
        string targetLanguage,
        string apiKey,
        CancellationToken ct)
    {
        if (blocks.Count == 0)
            return Array.Empty<TranslatedTextItem>();

        GeminiResponse response;
        List<TranslatedTextItem> parsed;
        bool responseIncomplete = false;

        try
        {
            response = await RequestTranslationAsync(client, pageNumber, blocks, fileUri, targetLanguage, apiKey, ct);
            responseIncomplete = string.Equals(response.FinishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase);

            parsed = ParseTranslationItems(response.Text);
        }
        catch (Exception ex) when (blocks.Count > 1)
        {
            _log.LogWarning(
                ex,
                "Page {Page}: translation parse/request failed for {Count} block(s), splitting and retrying.",
                pageNumber,
                blocks.Count);
            return await SplitAndTranslateAsync(client, pageNumber, blocks, fileUri, targetLanguage, apiKey, ct);
        }

        if (responseIncomplete)
        {
            if (blocks.Count > 1)
            {
                _log.LogWarning(
                    "Page {Page}: model hit MAX_TOKENS for {Count} block(s), splitting and retrying.",
                    pageNumber,
                    blocks.Count);
                return await SplitAndTranslateAsync(client, pageNumber, blocks, fileUri, targetLanguage, apiKey, ct);
            }

            throw new InvalidOperationException(
                $"Gemini response was truncated with finishReason=MAX_TOKENS for block {blocks[0].BlockId}.");
        }

        HashSet<string> expectedIds = blocks.Select(x => x.BlockId).ToHashSet(StringComparer.Ordinal);
        int matched = parsed.Count(x => expectedIds.Contains(x.BlockId));
        if (matched < expectedIds.Count && blocks.Count > 1)
        {
            _log.LogWarning(
                "Page {Page}: model returned partial block coverage ({Matched}/{Expected}), splitting and retrying.",
                pageNumber,
                matched,
                expectedIds.Count);
            return await SplitAndTranslateAsync(client, pageNumber, blocks, fileUri, targetLanguage, apiKey, ct);
        }

        return NormalizeByKnownBlockIds(blocks, parsed);
    }

    private async Task<IReadOnlyList<TranslatedTextItem>> SplitAndTranslateAsync(
        HttpClient client,
        int pageNumber,
        IReadOnlyList<TextBlock> blocks,
        string fileUri,
        string targetLanguage,
        string apiKey,
        CancellationToken ct)
    {
        if (blocks.Count <= 1)
            throw new InvalidOperationException("Cannot split a single block further.");

        int mid = blocks.Count / 2;
        List<TextBlock> left = blocks.Take(mid).ToList();
        List<TextBlock> right = blocks.Skip(mid).ToList();

        IReadOnlyList<TranslatedTextItem> leftResult = await TranslateBlocksAdaptiveAsync(
            client,
            pageNumber,
            left,
            fileUri,
            targetLanguage,
            apiKey,
            ct);

        IReadOnlyList<TranslatedTextItem> rightResult = await TranslateBlocksAdaptiveAsync(
            client,
            pageNumber,
            right,
            fileUri,
            targetLanguage,
            apiKey,
            ct);

        return leftResult.Concat(rightResult).ToList();
    }

    private async Task<GeminiResponse> RequestTranslationAsync(
        HttpClient client,
        int pageNumber,
        IReadOnlyList<TextBlock> blocks,
        string fileUri,
        string targetLanguage,
        string apiKey,
        CancellationToken ct)
    {
        var payloadBlocks = blocks.Select(x => new
        {
            block_id = x.BlockId,
            original_text = TextSanitizer.CleanPdfArtifacts(x.OriginalText)
        });

        string blocksJson = JsonSerializer.Serialize(payloadBlocks, JsonOptions);

        string instruction =
            "You are a professional document translator. " +
            $"Translate all block texts into {targetLanguage}. " +
            "Use the PDF page to resolve broken words and encoding artifacts visually. " +
            "Return ONLY valid JSON array with objects in this exact shape: " +
            "[{\"original_text\":\"...\",\"translated_text\":\"...\",\"block_id\":\"...\"}]. " +
            "Do not add markdown fences, comments, or extra keys.";

        var payload = new
        {
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = instruction },
                        new { text = "Blocks to translate (JSON):\n" + blocksJson },
                        new { file_data = new { mime_type = "application/pdf", file_uri = fileUri } }
                    }
                }
            },
            generationConfig = new
            {
                temperature = _gemini.Temperature,
                maxOutputTokens = _gemini.MaxOutputTokens,
                responseMimeType = "application/json"
            }
        };

        string model = string.IsNullOrWhiteSpace(_gemini.Model) ? "gemini-1.5-pro" : _gemini.Model.Trim();
        string endpoint = $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        string payloadStr = JsonSerializer.Serialize(payload, JsonOptions);

        using HttpRequestMessage req = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payloadStr, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage resp = await SendWithRateLimitAsync(client, req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini translation request failed ({(int)resp.StatusCode}): {body}");

        GeminiResponse parsed = ExtractFirstCandidate(body);
        string text = parsed.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Gemini response does not contain candidate text.");

        _log.LogInformation(
            "Page {Page}: Gemini batch translated blocks={Count}, finishReason={FinishReason}",
            pageNumber,
            blocks.Count,
            parsed.FinishReason ?? "n/a");

        return parsed;
    }

    private static GeminiResponse ExtractFirstCandidate(string responseJson)
    {
        using JsonDocument doc = JsonDocument.Parse(responseJson);

        if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
            return new GeminiResponse(string.Empty, null, responseJson);

        foreach (JsonElement candidate in candidates.EnumerateArray())
        {
            string? finishReason = candidate.TryGetProperty("finishReason", out JsonElement fr)
                ? fr.GetString()
                : null;

            if (!candidate.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Object)
                continue;

            if (!content.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array)
                continue;

            StringBuilder sb = new();

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out JsonElement textEl))
                {
                    string? text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.Append(text);
                }
            }

            string merged = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(merged))
                return new GeminiResponse(merged, finishReason, responseJson);
        }

        return new GeminiResponse(string.Empty, null, responseJson);
    }

    private static List<TranslatedTextItem> ParseTranslationItems(string modelText)
    {
        string json = ExtractJsonArray(modelText);
        List<GeminiTranslationRow>? rows = JsonSerializer.Deserialize<List<GeminiTranslationRow>>(json, JsonOptions);

        if (rows is null || rows.Count == 0)
            throw new InvalidOperationException("Gemini returned empty translation JSON array.");

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
            throw new InvalidOperationException("Gemini output does not contain a JSON array.");

        return trimmed[start..(end + 1)];
    }

    private IReadOnlyList<TranslatedTextItem> NormalizeByKnownBlockIds(PageObject page, List<TranslatedTextItem> parsed)
    {
        Dictionary<string, TranslatedTextItem> byId = parsed
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(x => x.BlockId, x => x, StringComparer.Ordinal);

        List<TranslatedTextItem> result = new(page.TextBlocks.Count);

        foreach (TextBlock block in page.TextBlocks)
        {
            if (byId.TryGetValue(block.BlockId, out TranslatedTextItem? hit) &&
                !string.IsNullOrWhiteSpace(hit.TranslatedText))
            {
                result.Add(hit);
                continue;
            }

            result.Add(new TranslatedTextItem(block.BlockId, block.OriginalText, block.OriginalText));
        }

        return result;
    }

    private static IReadOnlyList<TranslatedTextItem> NormalizeByKnownBlockIds(
        IReadOnlyList<TextBlock> blocks,
        List<TranslatedTextItem> parsed)
    {
        Dictionary<string, TranslatedTextItem> byId = parsed
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToDictionary(x => x.BlockId, x => x, StringComparer.Ordinal);

        List<TranslatedTextItem> result = new(blocks.Count);

        foreach (TextBlock block in blocks)
        {
            if (byId.TryGetValue(block.BlockId, out TranslatedTextItem? hit) &&
                !string.IsNullOrWhiteSpace(hit.TranslatedText))
            {
                result.Add(hit);
                continue;
            }

            result.Add(new TranslatedTextItem(block.BlockId, block.OriginalText, block.OriginalText));
        }

        return result;
    }

    private static Uri BuildBaseUri(string baseUrl)
    {
        string value = baseUrl.Trim();
        if (!value.EndsWith("/", StringComparison.Ordinal))
            value += "/";

        return new Uri(value, UriKind.Absolute);
    }

    private List<IReadOnlyList<TextBlock>> BuildInitialBatches(IReadOnlyList<TextBlock> blocks)
    {
        int maxBlocks = _gemini.MaxBlocksPerRequest <= 0 ? int.MaxValue : _gemini.MaxBlocksPerRequest;
        int maxChars = _gemini.MaxInputCharsPerRequest <= 0 ? int.MaxValue : _gemini.MaxInputCharsPerRequest;

        List<IReadOnlyList<TextBlock>> result = new();
        List<TextBlock> current = new();
        int currentChars = 0;

        foreach (TextBlock block in blocks)
        {
            int blockChars = Math.Max(1, TextSanitizer.CleanPdfArtifacts(block.OriginalText).Length);
            bool overflowByCount = current.Count >= maxBlocks;
            bool overflowByChars = current.Count > 0 && currentChars + blockChars > maxChars;

            if (overflowByCount || overflowByChars)
            {
                result.Add(current.ToList());
                current.Clear();
                currentChars = 0;
            }

            current.Add(block);
            currentChars += blockChars;
        }

        if (current.Count > 0)
            result.Add(current.ToList());

        if (result.Count == 0)
            result.Add(Array.Empty<TextBlock>());

        return result;
    }

    private async Task<HttpResponseMessage> SendWithRateLimitAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        await WaitForRateLimitSlotAsync(ct);
        return await client.SendAsync(request, ct);
    }

    private async Task WaitForRateLimitSlotAsync(CancellationToken ct)
    {
        if (!_gemini.EnableRateLimit)
            return;

        double rps = _gemini.MaxRequestsPerSecond;
        if (rps <= 0)
            return;

        TimeSpan minInterval = TimeSpan.FromSeconds(1d / rps);

        await _rateLimiterGate.WaitAsync(ct);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan wait = _nextAllowedRequestUtc > now
                ? _nextAllowedRequestUtc - now
                : TimeSpan.Zero;

            DateTimeOffset scheduledAt = now + wait;
            _nextAllowedRequestUtc = scheduledAt + minInterval;

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
        }
        finally
        {
            _rateLimiterGate.Release();
        }
    }

    private sealed record GeminiTranslationRow(string? block_id, string? original_text, string? translated_text);
    private sealed record GeminiResponse(string Text, string? FinishReason, string RawResponse);
}
