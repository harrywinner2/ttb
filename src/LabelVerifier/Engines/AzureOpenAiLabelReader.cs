using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LabelVerifier.Models;
using Microsoft.Extensions.Options;

namespace LabelVerifier.Engines;

/// <summary>
/// Primary engine: sends the artwork to an Azure OpenAI vision deployment (gpt-4o)
/// and asks for the label fields back as strict JSON. Handles imperfect photos
/// (angles, glare, stylised fonts) far better than OCR — Jenny's wish-list item.
/// </summary>
public sealed class AzureOpenAiLabelReader : ILabelReader
{
    private readonly HttpClient _http;
    private readonly AzureOpenAiOptions _opts;
    private readonly ILogger<AzureOpenAiLabelReader> _log;

    public AzureOpenAiLabelReader(HttpClient http, IOptions<AzureOpenAiOptions> opts, ILogger<AzureOpenAiLabelReader> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public string Name => $"Azure OpenAI ({_opts.Deployment ?? "gpt-4o"})";

    public bool IsAvailable => _opts.IsConfigured;

    // Phrasing is deliberately neutral/descriptive: imperative "ONLY / never / do not"
    // styling can trip Azure's prompt-shield (jailbreak) content filter and 400 the request.
    private const string SystemPrompt =
        "You are a compliance assistant for the U.S. Alcohol and Tobacco Tax and Trade Bureau (TTB). " +
        "You read photographs of alcohol beverage labels and report the printed text accurately. " +
        "When a detail is not visible, you record it as null. You pay close attention to the health " +
        "warning statement, copying its wording exactly and noting whether the heading is in capital " +
        "letters and bold. You reply with a JSON object.";

    private const string UserPrompt =
        "Please read this alcohol label and provide a JSON object with these keys: " +
        "brandName, classType, alcoholContent, netContents, bottlerNameAddress, countryOfOrigin, " +
        "governmentWarningText (the full health warning statement, including the words GOVERNMENT WARNING, " +
        "copied exactly as printed, or null), " +
        "warningHeadingAllCaps (true, false, or null — whether the words GOVERNMENT WARNING appear in capital letters), " +
        "warningHeadingBold (true, false, or null — whether the words GOVERNMENT WARNING appear in bold type), " +
        "rawText (all the text you can read on the label), and " +
        "notes (image quality observations such as angle, glare, or blur). " +
        "Use null for any field you cannot read.";

    public async Task<LabelReading> ReadAsync(byte[] imageBytes, string contentType, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Azure OpenAI is not configured.");

        var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(imageBytes)}";

        var payload = new
        {
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = UserPrompt },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            temperature = 0,
            max_tokens = 1200,
            response_format = new { type = "json_object" }
        };

        var url = $"{_opts.Endpoint!.TrimEnd('/')}/openai/deployments/{_opts.Deployment}/chat/completions?api-version={_opts.ApiVersion}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("api-key", _opts.ApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, timeoutCts.Token);
        var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Azure OpenAI returned {Status}: {Body}", (int)resp.StatusCode, Truncate(body, 500));
            throw new HttpRequestException($"Azure OpenAI returned {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        var reading = ParseReading(content);
        reading.EngineUsed = Name;
        _log.LogInformation("Azure OpenAI read label in {Ms}ms", sw.ElapsedMilliseconds);
        return reading;
    }

    private static LabelReading ParseReading(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new LabelReading
        {
            BrandName = Str(r, "brandName"),
            ClassType = Str(r, "classType"),
            AlcoholContent = Str(r, "alcoholContent"),
            NetContents = Str(r, "netContents"),
            BottlerNameAddress = Str(r, "bottlerNameAddress"),
            CountryOfOrigin = Str(r, "countryOfOrigin"),
            GovernmentWarningText = Str(r, "governmentWarningText"),
            WarningHeadingAllCaps = Bool(r, "warningHeadingAllCaps"),
            WarningHeadingBold = Bool(r, "warningHeadingBold"),
            RawText = Str(r, "rawText"),
            Notes = Str(r, "notes"),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
