using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebhookApi.Data;

namespace WebhookApi.Services
{
    public class OpenAiGuestClassifier : IGuestClassifier
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<OpenAiGuestClassifier> _logger;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _endpoint;

        public OpenAiGuestClassifier(IConfiguration config, IHttpClientFactory httpFactory, ILogger<OpenAiGuestClassifier> logger)
        {
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiKey = config["AI:ApiKey"] ?? string.Empty;
            _model = config["AI:Model"] ?? "gpt-4o-mini";
            _endpoint = config["AI:Endpoint"]?.TrimEnd('/') ?? "https://api.openai.com";
        }

        public async Task<GuestClassificationResult> ClassifyAsync(string combinedMessages, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogWarning("OpenAI API key not configured (AI:ApiKey). Returning uncertain result.");
                return new GuestClassificationResult(true, 0.5, "uncertain", "API key not configured");
            }

            var prompt = $@"You are a classification assistant. Decide if a guest (based on messages) is 'good' or 'bad'.\nReturn ONLY valid JSON with keys: label (good|bad|uncertain), isGood (boolean), score (0.0-1.0), reason (short).\nNow classify the combined messages below. Keep JSON compact.\n### MESSAGES:\n{combinedMessages}";

            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = "You MUST reply only with JSON per the schema." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.0,
                max_tokens = 200
            };

            try
            {
                var client = _httpFactory.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_endpoint + "/"), "v1/chat/completions"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                req.Content = JsonContent.Create(payload);

                using var resp = await client.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("OpenAI API returned {Status}: {Body}", resp.StatusCode, body);
                    return new GuestClassificationResult(true, 0.5, "uncertain", "API error");
                }

                using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
                    _logger.LogWarning("OpenAI returned empty completion content");
                    return new GuestClassificationResult(true, 0.5, "uncertain", "Empty model response");
                }

                try
                {
                    // Clean up common wrappers from model output (markdown code fences, ```json ... ``` etc.)
                    var cleaned = assistantText.Trim();

                    // Remove leading/trailing triple-backtick fences
                    if (cleaned.StartsWith("```"))
                    {
                        // If it's ```json or ``` we want the inner content
                        var fenceEnd = cleaned.IndexOf("```", 3, StringComparison.Ordinal);
                        if (fenceEnd > 3)
                        {
                            cleaned = cleaned.Substring(3, fenceEnd - 3).Trim();
                        }
                        else
                        {
                            // try to remove trailing fence if present at end
                            if (cleaned.EndsWith("```"))
                                cleaned = cleaned.Substring(3, cleaned.Length - 6).Trim();
                        }
                    }

                    // If assistant wrapped in a single-line markdown block like `...`, strip backticks
                    if (cleaned.StartsWith("`") && cleaned.EndsWith("`"))
                        cleaned = cleaned.Trim('`').Trim();

                    // Attempt to extract the first JSON object from the response (from first '{' to matching last '}')
                    var firstBrace = cleaned.IndexOf('{');
                    var lastBrace = cleaned.LastIndexOf('}');
                    if (firstBrace >= 0 && lastBrace > firstBrace)
                    {
                        cleaned = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
                    }

                    var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsed = JsonSerializer.Deserialize<GuestClassificationResult>(cleaned, opt);
                    if (parsed != null) return parsed;
                    _logger.LogWarning("Classifier parsed null after cleaning. Raw: {Raw} Cleaned: {Cleaned}", assistantText, cleaned);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse classifier output. Raw: {Raw}", assistantText);
                }

                _logger.LogWarning("Unexpected classifier response structure.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Classifier request failed");
            }

            return new GuestClassificationResult(true, 0.5, "uncertain", "Unable to parse or classify");
        }
    }
}
