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
    private readonly string _provider;

    public OpenAiIdentifierExtractor(IConfiguration config, IHttpClientFactory httpFactory, ILogger<OpenAiIdentifierExtractor> logger)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKey = config["AI:ApiKey"] ?? string.Empty;
        _model = config["AI:Model"] ?? "gpt-4o-mini";
        _endpoint = config["AI:Endpoint"]?.TrimEnd('/') ?? "https://api.openai.com";
        // Provider can be "openai", "ollama" (or "local"), or "auto" (default).
        _provider = (config["AI:Provider"] ?? "auto").ToLowerInvariant();
    }

    public async Task<IdentifierResult> ExtractAsync(string text, CancellationToken cancellationToken = default)
    {
        // Require API key only when explicitly configured to use OpenAI, or when auto-detection
        // determines we are calling a remote OpenAI endpoint.
        // If provider is 'ollama' or 'local', allow missing API key for local usage.
        // We'll check more precisely after resolving provider/endpoint below.

        var prompt = "Extract the following fields from the message: name, email, phone, bookingId, airbnbId, amount, urgent.\n"
             + "Return ONLY valid JSON with keys: name, email, phone, bookingId, airbnbId, amount, urgent. Use null when missing for strings and false for `urgent`.\n"
             + "If present, return `amount` as the full currency string (for example: '₱2,483.65 PHP').\n"
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
            // Use named AI client so timeout can be configured in Program.cs (AI:TimeoutSeconds)
            var client = _httpFactory.CreateClient("ai");

            // Decide endpoint path: respect explicit provider config, otherwise auto-detect from endpoint
            bool isLocalOllama;
            if (_provider == "ollama" || _provider == "local")
            {
                isLocalOllama = true;
            }
            else if (_provider == "openai")
            {
                isLocalOllama = false;
            }
            else // auto
            {
                isLocalOllama = _endpoint.Contains("11434") || _endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) || _endpoint.Contains("/api/chat", StringComparison.OrdinalIgnoreCase) || _endpoint.Contains("100.80.77.91", StringComparison.OrdinalIgnoreCase);
            }
            var relativePath = isLocalOllama ? "api/chat" : "v1/chat/completions";

            // If we need an API key for OpenAI and none is configured, abort early
            if (!isLocalOllama && string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogWarning("AI:ApiKey not configured for remote OpenAI provider; returning empty identifiers.");
                return new IdentifierResult(null, null, null, null, null, null, false);
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_endpoint + "/"), relativePath));
            if (!isLocalOllama)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            req.Content = JsonContent.Create(payload);

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Identifier extractor API returned {Status}: {Body}", resp.StatusCode, body);
                return new IdentifierResult(null, null, null, null, null, null, false);
            }

            // Read streaming lines if available (Ollama / local LLMs often stream newline-delimited JSON)
            var sb = new System.Text.StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("data: ")) line = line.Substring(6);

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Ollama-style: { "message": { "role":"assistant","content":"..." }, "done": false }
                    if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var contentElem))
                    {
                        var piece = contentElem.GetString();
                        if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                    }
                    // OpenAI streaming deltas: {"choices":[{"delta":{"content":"..."}, ...}], ...}
                    else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var first = choices[0];
                        if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("content", out var deltaContent))
                        {
                            var piece = deltaContent.GetString();
                            if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                        }
                        else if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var msgContent))
                        {
                            var piece = msgContent.GetString();
                            if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                        }
                        else if (first.TryGetProperty("text", out var textElem))
                        {
                            var piece = textElem.GetString();
                            if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                        }
                    }

                    if (root.TryGetProperty("done", out var doneElem) && doneElem.ValueKind == JsonValueKind.True)
                    {
                        break;
                    }
                }
                catch (JsonException)
                {
                    // ignore non-json lines
                }
            }

            string assistantText = sb.ToString();

            // Fallback: if streaming produced nothing, try full-body parse (OpenAI non-stream)
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                var bodyStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: cancellationToken);
                var root = doc.RootElement;

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
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                _logger.LogWarning("Identifier extractor returned empty completion content");
                return new IdentifierResult(null, null, null, null, null, null, false);
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
