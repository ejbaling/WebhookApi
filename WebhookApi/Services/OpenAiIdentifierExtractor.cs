using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WebhookApi.Services;

public class OpenAiIdentifierExtractor : IIdentifierExtractor
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OpenAiIdentifierExtractor> _logger;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;

    public OpenAiIdentifierExtractor(IConfiguration config, IHttpClientFactory httpFactory, ILogger<OpenAiIdentifierExtractor> logger)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKey = config["AI:ApiKey"] ?? string.Empty;
        _model = config["AI:Model"] ?? "gpt-4o-mini";
        _endpoint = config["AI:Endpoint"]?.TrimEnd('/') ?? "https://api.openai.com";
    }

    public async Task<IdentifierResult> ExtractAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("AI:ApiKey not configured; returning empty identifiers.");
            return new IdentifierResult(null, null, null, null, null, null, false);
        }

        var prompt = "Extract the following fields from the message: name, email, phone, bookingId, airbnbId, amount, urgent.\n"
             + "Return ONLY valid JSON with keys: name, email, phone, bookingId, airbnbId, amount, urgent. Use null when missing for strings and false for `urgent`.\n"
             + "If present, return `amount` as the full currency string (for example: '₱2,483.65 PHP').\n"
             + "IMPORTANT: Do NOT extract `amount` if the message is an Airbnb hosting notification (e.g., 'YOU COULD EARN [amount] FOR HOSTING [name]'). In such cases, set `amount` to null.\n"
             + "`urgent` should be a boolean: true for any question the guest asks or any request that requires a reply or action (even if not immediate).\n"
             + "Treat interrogative sentences and explicit questions as urgent. Do NOT rely on code-side heuristics; determine `urgent` based on the content.\n"
             + "Do NOT mark as urgent when the sender explicitly says 'no rush', 'no hurry', 'no need to reply', or similar.\n"
             + "Return only the JSON object and nothing else.\n\n"
             + "MESSAGE:\n" + text;

        var payload = new
        {
            model = _model,
            messages = new[] {
                new { role = "system", content = "You MUST reply only with JSON per the schema." },
                new { role = "user", content = prompt }
            },
            temperature = 0.0,
            max_tokens = 300
        };

        try
        {
            var client = _httpFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_endpoint + "/"), "v1/chat/completions"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = JsonContent.Create(payload);

            using var resp = await client.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Identifier extractor API returned {Status}: {Body}", resp.StatusCode, body);
                    return new IdentifierResult(null, null, null, null, null, null, false);
                }

            var bodyStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            string assistantText = string.Empty;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                {
                    assistantText = content.GetString() ?? string.Empty;
                }
                else if (first.TryGetProperty("text", out var textElem))
                {
                    assistantText = textElem.GetString() ?? string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                _logger.LogWarning("Identifier extractor returned empty completion content");
                return new IdentifierResult(null, null, null, null, null, null, false);
            }

            // Clean code fences/backticks
            var cleaned = assistantText.Trim();
            if (cleaned.StartsWith("```"))
            {
                // strip opening and trailing fences if present
                if (cleaned.EndsWith("```"))
                    cleaned = cleaned.Substring(3, cleaned.Length - 6).Trim();
                else
                {
                    var end = cleaned.IndexOf("```", 3, StringComparison.Ordinal);
                    if (end > 3) cleaned = cleaned.Substring(3, end - 3).Trim();
                }
            }
            if (cleaned.StartsWith("`") && cleaned.EndsWith("`")) cleaned = cleaned.Trim('`').Trim();

            var firstBrace = cleaned.IndexOf('{');
            var lastBrace = cleaned.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                cleaned = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            try
            {
                var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<IdentifierResult>(cleaned, opt);
                if (parsed != null) return parsed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse identifier JSON. Raw: {Raw}", assistantText);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Identifier extraction request failed");
        }

        return new IdentifierResult(null, null, null, null, null, null, false);
    }
}
