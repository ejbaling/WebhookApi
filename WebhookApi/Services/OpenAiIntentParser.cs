using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WebhookApi.Services
{
    public class OpenAiIntentParser : IIntentParser
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly ILogger<OpenAiIntentParser> _logger;
        private readonly string _systemPrompt;

        public OpenAiIntentParser(IConfiguration config, IHttpClientFactory httpFactory, ILogger<OpenAiIntentParser> logger)
        {
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _logger = logger;
            _apiKey = config["AI:ApiKey"] ?? string.Empty;
            _model = config["AI:Model"] ?? "gpt-4o-mini";

            _systemPrompt = "You are an intent parser that converts a user's natural language instruction into a JSON object with the following schema:\n{\"action\": string|null, \"parameters\": { string: string }, \"requireConfirm\": boolean}\nAllowed actions: shutdown_server, lights_off, assess_guest\nReturn ONLY valid JSON and nothing else. Examples:\n\"shutdown production server\" -> {\"action\":\"shutdown_server\",\"parameters\":{\"environment\":\"prod\"},\"requireConfirm\":true}\n\"turn off the lights\" -> {\"action\":\"lights_off\",\"parameters\":{},\"requireConfirm\":false}\n\"Is John Doe a good guest?\" -> {\"action\":\"assess_guest\",\"parameters\":{\"name\":\"John Doe\"},\"requireConfirm\":false}\n\"Please assess guest Jane Smith based on messages\" -> {\"action\":\"assess_guest\",\"parameters\":{\"name\":\"Jane Smith\"},\"requireConfirm\":false}";
        }

        public async Task<IntentResult> ParseAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new IntentResult(null, null, false);

            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogWarning("OpenAI API key not configured (AI:ApiKey). Falling back to no intent.");
                return new IntentResult(null, null, false);
            }

            try
            {
                var client = _httpFactory.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

                var payload = new
                {
                    model = _model,
                    messages = new[] {
                        new { role = "system", content = _systemPrompt },
                        new { role = "user", content = text }
                    },
                    max_tokens = 300,
                    temperature = 0.0
                };

                req.Content = JsonContent.Create(payload);

                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    _logger.LogWarning("OpenAI API returned {Status}: {Body}", resp.StatusCode, body);
                    return new IntentResult(null, null, false);
                }

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                var content = string.Empty;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var msg = choices[0].GetProperty("message");
                    if (msg.TryGetProperty("content", out var contentElem))
                        content = contentElem.GetString() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning("OpenAI returned empty completion content");
                    return new IntentResult(null, null, false);
                }

                // Try to parse JSON from the model output. Model should return raw JSON, but be tolerant.
                var json = ExtractJson(content);
                if (json is null)
                {
                    _logger.LogWarning("Failed to extract JSON from model output: {Output}", content);
                    return new IntentResult(null, null, false);
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<IntentResult>(json, options);
                return parsed ?? new IntentResult(null, null, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling OpenAI intent parser");
                return new IntentResult(null, null, false);
            }
        }

        private static string? ExtractJson(string s)
        {
            // Trim and try direct parse
            s = s.Trim();
            try
            {
                JsonDocument.Parse(s);
                return s;
            }
            catch { }

            // Fallback: find first { and last }
            var first = s.IndexOf('{');
            var last = s.LastIndexOf('}');
            if (first >= 0 && last > first)
                return s.Substring(first, last - first + 1);
            return null;
        }
    }
}
