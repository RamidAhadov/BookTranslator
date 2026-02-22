using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using BookTranslator.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookTranslator.Services;

public sealed class AzureReadOcrService : IOcrService
{
    private readonly object _rateLock = new();
    private readonly Queue<DateTime> _requestTimestampsUtc = new();
    private readonly object _cacheLock = new();

    private readonly IHttpClientFactory _httpFactory;
    private readonly OcrOptions _opt;
    private readonly TranslationOptions _translation;
    private readonly ILogger<AzureReadOcrService> _log;
    private readonly string _cacheDir;

    public AzureReadOcrService(
        IHttpClientFactory httpFactory,
        IOptions<OcrOptions> opt,
        IOptions<TranslationOptions> translation,
        ILogger<AzureReadOcrService> log)
    {
        _httpFactory = httpFactory;
        _opt = opt.Value;
        _translation = translation.Value;
        _log = log;

        _cacheDir = Path.Combine(Path.GetFullPath(_translation.CheckpointDir), "ocr");
        Directory.CreateDirectory(_cacheDir);
    }

    public bool IsEnabled =>
        _opt.Enabled &&
        !string.IsNullOrWhiteSpace(_opt.AzureEndpoint) &&
        !string.IsNullOrWhiteSpace(_opt.AzureApiKey) &&
        string.Equals(_opt.Provider, "azure-read", StringComparison.OrdinalIgnoreCase);

    public string? ExtractTextFromImages(IReadOnlyList<byte[]> images)
    {
        if (!IsEnabled || images.Count == 0)
            return null;

        int max = Math.Max(1, _opt.MaxImagesPerBatch);
        List<string> chunks = new List<string>(max);
        List<byte[]> orderedPayloads = images
            .OrderBy(x =>
            {
                string ct = GuessContentType(x);
                return string.Equals(ct, "application/pdf", StringComparison.Ordinal) ? 1 : 0;
            })
            .ToList();

        foreach (byte[] image in orderedPayloads)
        {
            if (chunks.Count >= max)
                break;

            if (image.Length < Math.Max(1, _opt.MinImageBytesForOcr))
            {
                _log.LogInformation("OCR payload skipped due to size: {Size} bytes.", image.Length);
                continue;
            }

            string? txt = ExtractSingleImage(image);
            if (!string.IsNullOrWhiteSpace(txt))
                chunks.Add(txt.Trim());
        }

        if (chunks.Count == 0)
            return null;

        return string.Join(Environment.NewLine, chunks);
    }

    private string? ExtractSingleImage(byte[] image)
    {
        string hash = ComputeHash(image);
        string mediaType = GuessContentType(image);

        if (TryReadCache(hash, out string? cachedText, out string? cachedStatus))
        {
            _log.LogInformation("OCR cache hit. Hash={Hash}, Status={Status}", hash, cachedStatus ?? "unknown");
            return cachedText;
        }

        if (string.Equals(mediaType, "application/octet-stream", StringComparison.Ordinal))
        {
            _log.LogWarning("OCR payload skipped: unsupported format. Hash={Hash}, Bytes={Bytes}", hash, image.Length);
            return null;
        }

        WritePayloadSnapshot(hash, mediaType, image);

        try
        {
            HttpClient client = _httpFactory.CreateClient("AzureOcr");
            client.BaseAddress = BuildBaseUri(_opt.AzureEndpoint);
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _opt.AzureApiKey);

            string submitPath = $"vision/{_opt.AzureApiVersion}/read/analyze";
            using HttpRequestMessage req = new(HttpMethod.Post, submitPath)
            {
                Content = new ByteArrayContent(image)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

            _log.LogInformation("Azure OCR submit started. Hash={Hash}, PayloadBytes={Bytes}, ContentType={ContentType}", hash, image.Length, mediaType);

            using HttpResponseMessage submitResp = SendWithRateLimit(client, req);
            if (!submitResp.IsSuccessStatusCode)
            {
                string body = submitResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if ((int)submitResp.StatusCode == 400 && body.Contains("InvalidImage", StringComparison.OrdinalIgnoreCase))
                    _log.LogWarning("Azure OCR invalid image payload. Hash={Hash}, Bytes={Bytes}, ContentType={ContentType}", hash, image.Length, mediaType);

                _log.LogWarning("Azure OCR submit failed {Status}: {Body}", (int)submitResp.StatusCode, body);
                WriteCache(hash, mediaType, image.Length, "submit_failed", null, body, $"HTTP {(int)submitResp.StatusCode}");
                return null;
            }

            if (!submitResp.Headers.TryGetValues("Operation-Location", out IEnumerable<string>? values))
            {
                WriteCache(hash, mediaType, image.Length, "submit_failed", null, null, "Operation-Location header missing");
                return null;
            }

            string? operationLocation = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(operationLocation))
            {
                WriteCache(hash, mediaType, image.Length, "submit_failed", null, null, "Operation-Location header empty");
                return null;
            }

            _log.LogInformation("Azure OCR submit accepted. Hash={Hash}", hash);
            PollResult poll = PollReadResult(client, operationLocation);

            WriteCache(hash, mediaType, image.Length, poll.Status, poll.Text, poll.RawJson, poll.Error);
            return poll.Text;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure OCR failed while extracting text from image. Hash={Hash}", hash);
            WriteCache(hash, mediaType, image.Length, "exception", null, null, ex.Message);
            return null;
        }
    }

    private PollResult PollReadResult(HttpClient client, string operationLocation)
    {
        int attempts = Math.Max(1, _opt.MaxPollAttempts);
        int delayMs = Math.Max(250, _opt.PollIntervalMs);

        for (int i = 0; i < attempts; i++)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, operationLocation);
            using HttpResponseMessage resp = SendWithRateLimit(client, req);
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if ((int)resp.StatusCode == 429)
            {
                int waitMs = ParseRetryAfterMs(resp) ?? Math.Max(1000, _opt.RetryAfter429Ms);
                _log.LogWarning("Azure OCR rate-limited (429). Waiting {Ms} ms.", waitMs);
                Thread.Sleep(waitMs);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                return new PollResult(null, body, "poll_failed", $"HTTP {(int)resp.StatusCode}");

            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("status", out JsonElement statusEl))
                return new PollResult(null, body, "poll_failed", "status field missing");

            string status = statusEl.GetString() ?? string.Empty;
            _log.LogInformation("Azure OCR poll attempt {Attempt}/{Max} status={Status}", i + 1, attempts, status);

            if (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            {
                string? text = ExtractLines(doc.RootElement, _opt);
                return new PollResult(text, body, "succeeded", null);
            }

            if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                return new PollResult(null, body, "failed", "Azure returned failed status");

            Thread.Sleep(delayMs);
        }

        return new PollResult(null, null, "timeout", "Max poll attempts reached");
    }

    private HttpResponseMessage SendWithRateLimit(HttpClient client, HttpRequestMessage req)
    {
        WaitForRequestSlot();
        return client.Send(req);
    }

    private void WaitForRequestSlot()
    {
        int maxRpm = Math.Max(1, _opt.MaxRequestsPerMinute);

        while (true)
        {
            int waitMs = 0;
            DateTime now = DateTime.UtcNow;

            lock (_rateLock)
            {
                while (_requestTimestampsUtc.Count > 0 && (now - _requestTimestampsUtc.Peek()).TotalSeconds >= 60)
                    _requestTimestampsUtc.Dequeue();

                if (_requestTimestampsUtc.Count < maxRpm)
                {
                    _requestTimestampsUtc.Enqueue(now);
                    return;
                }

                DateTime oldest = _requestTimestampsUtc.Peek();
                double msLeft = 60000 - (now - oldest).TotalMilliseconds;
                waitMs = (int)Math.Max(250, msLeft);
            }

            Thread.Sleep(waitMs);
        }
    }

    private static int? ParseRetryAfterMs(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is TimeSpan delta)
            return (int)Math.Max(250, delta.TotalMilliseconds);

        if (resp.Headers.RetryAfter?.Date is DateTimeOffset at)
        {
            double ms = (at - DateTimeOffset.UtcNow).TotalMilliseconds;
            return (int)Math.Max(250, ms);
        }

        return null;
    }

    private static string? ExtractLines(JsonElement root, OcrOptions opt)
    {
        if (!root.TryGetProperty("analyzeResult", out JsonElement analyze))
            return null;
        if (!analyze.TryGetProperty("readResults", out JsonElement readResults) || readResults.ValueKind != JsonValueKind.Array)
            return null;

        List<string> lines = new();

        foreach (JsonElement page in readResults.EnumerateArray())
        {
            if (!page.TryGetProperty("lines", out JsonElement ls) || ls.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement l in ls.EnumerateArray())
            {
                if (opt.DropVerticalText && IsVerticalLine(l))
                    continue;

                double confidence = GetAverageWordConfidence(l);
                if (confidence > 0 && confidence < opt.MinLineConfidence)
                    continue;

                if (l.TryGetProperty("text", out JsonElement textEl))
                {
                    string? text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        lines.Add(text);
                }
            }
        }

        if (lines.Count == 0)
            return null;

        return string.Join(Environment.NewLine, DeduplicateAdjacent(lines));
    }

    private static IEnumerable<string> DeduplicateAdjacent(IEnumerable<string> lines)
    {
        string? prev = null;
        foreach (string line in lines)
        {
            string current = line.Trim();
            if (current.Length == 0)
                continue;

            if (string.Equals(prev, current, StringComparison.Ordinal))
                continue;

            prev = current;
            yield return current;
        }
    }

    private static double GetAverageWordConfidence(JsonElement line)
    {
        if (!line.TryGetProperty("words", out JsonElement words) || words.ValueKind != JsonValueKind.Array)
            return 0;

        double sum = 0;
        int count = 0;

        foreach (JsonElement w in words.EnumerateArray())
        {
            if (!w.TryGetProperty("confidence", out JsonElement cEl) || cEl.ValueKind != JsonValueKind.Number)
                continue;

            if (!cEl.TryGetDouble(out double c))
                continue;

            sum += c;
            count++;
        }

        if (count == 0)
            return 0;

        return sum / count;
    }

    private static bool IsVerticalLine(JsonElement line)
    {
        if (!line.TryGetProperty("boundingBox", out JsonElement bb) || bb.ValueKind != JsonValueKind.Array)
            return false;

        double[] pts = bb.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (pts.Length < 8)
            return false;

        // Azure format: x1,y1,x2,y2,x3,y3,x4,y4
        double width = Distance(pts[0], pts[1], pts[2], pts[3]);
        double height = Distance(pts[2], pts[3], pts[4], pts[5]);

        if (width <= 0 || height <= 0)
            return false;

        return height / width >= 2.5;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private bool TryReadCache(string hash, out string? text, out string? status)
    {
        text = null;
        status = null;

        string path = GetCachePath(hash);
        if (!File.Exists(path))
            return false;

        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("status", out JsonElement statusEl))
                status = statusEl.GetString();

            if (doc.RootElement.TryGetProperty("extractedText", out JsonElement textEl))
                text = textEl.GetString();

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read OCR cache entry. Hash={Hash}", hash);
            return false;
        }
    }

    private void WriteCache(
        string hash,
        string contentType,
        int payloadBytes,
        string status,
        string? extractedText,
        string? rawResponse,
        string? error)
    {
        string path = GetCachePath(hash);

        var entry = new
        {
            hash,
            provider = _opt.Provider,
            status,
            createdUtc = DateTimeOffset.UtcNow,
            payloadBytes,
            contentType,
            extractedText,
            rawResponse,
            error
        };

        string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        lock (_cacheLock)
        {
            File.WriteAllText(path, json);
        }
    }

    private void WritePayloadSnapshot(string hash, string contentType, byte[] payload)
    {
        if (payload.Length == 0)
            return;

        string ext = GetPayloadExtension(contentType);
        string path = Path.Combine(_cacheDir, $"{hash}{ext}");

        lock (_cacheLock)
        {
            if (File.Exists(path))
                return;

            File.WriteAllBytes(path, payload);
        }
    }

    private string GetCachePath(string hash) => Path.Combine(_cacheDir, $"{hash}.json");

    private static string GetPayloadExtension(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        _ => ".bin"
    };

    private static string ComputeHash(byte[] payload)
    {
        byte[] hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GuessContentType(byte[] payload)
    {
        if (payload.Length >= 4 &&
            payload[0] == 0x25 && payload[1] == 0x50 && payload[2] == 0x44 && payload[3] == 0x46)
            return "application/pdf";

        if (payload.Length >= 8 &&
            payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
            return "image/png";

        if (payload.Length >= 3 &&
            payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF)
            return "image/jpeg";

        if (payload.Length >= 2 &&
            payload[0] == 0x42 && payload[1] == 0x4D)
            return "image/bmp";

        if (payload.Length >= 4 &&
            ((payload[0] == 0x49 && payload[1] == 0x49 && payload[2] == 0x2A && payload[3] == 0x00) ||
             (payload[0] == 0x4D && payload[1] == 0x4D && payload[2] == 0x00 && payload[3] == 0x2A)))
            return "image/tiff";

        return "application/octet-stream";
    }

    private static Uri BuildBaseUri(string endpoint)
    {
        string normalized = endpoint.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";

        return new Uri(normalized, UriKind.Absolute);
    }

    private sealed record PollResult(string? Text, string? RawJson, string Status, string? Error);
}


