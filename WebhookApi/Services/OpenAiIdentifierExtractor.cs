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
            return new IdentifierResult(null, null, null, null, null, null, null, null);
        }

           var prompt = "Extract the following fields from the message into JSON: name, guestName, hostName, email, phone, bookingId, airbnbId, amount.\n"
               + "Important rules:\n"
               + "1) When the message contains booking or reservation information (dates, listing names, 'Total paid', 'Home •', 'Reservation', 'Total paid:'), the 'guestName' MUST be the traveler who booked the reservation. Set 'name' to the same value as 'guestName'. If both guest and host appear, set 'hostName' to the recipient/host.\n"
               + "2) If only one personal name appears but the message clearly refers to a payout/recipient AND includes reservation details, treat the traveler name that appears in the reservation section as the guest. Prefer the traveler/guest when in doubt.\n"
               + "3) Return 'amount' as the full currency string (for example: '₱2,483.65 PHP').\n"
               + "4) ALWAYS return ONLY valid JSON and NOTHING else, with keys exactly: name, guestName, hostName, email, phone, bookingId, airbnbId, amount. Use null for missing fields.\n"
               + "Example:\n"
               + "MESSAGE:\n"
               + "₱4,058.88 PHP was sent today\n\nYour money was sent on January 23 and should arrive by January 30, 2026.\n\nView earnings\nBank account\n\nEmmanuel Jomer Baling, 4925 (PHP)\n\nPayouts received by your bank on weekends or holidays will be processed on the next business day.\n\nAirbnb account ID\n\n332328139\n\nDetails\n\nFaith Angeli Galera\n\n₱4,058.88 PHP\n\nHome • 01/22/2026 - 01/26/2026\n\nRedwood Iloilo Kauri holiday room (1170641659087583828)\n\nHMJXQXND5T\n\nTotal paid:\n\n₱4,058.88 PHP\n\nExpected JSON:\n"
               + "{\"name\":\"Faith Angeli Galera\",\"guestName\":\"Faith Angeli Galera\",\"hostName\":\"Emmanuel Jomer Baling\",\"email\":null,\"phone\":null,\"bookingId\":null,\"airbnbId\":null,\"amount\":\"₱4,058.88 PHP\"}\n"
               + "End of instruction.\n\n"
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
                    return new IdentifierResult(null, null, null, null, null, null, null, null);
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
                return new IdentifierResult(null, null, null, null, null, null, null, null);
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
                if (parsed != null)
                {
                    // Ensure `Name` refers to the guest when `guestName` is present.
                    if (!string.IsNullOrWhiteSpace(parsed.GuestName))
                    {
                        parsed = parsed with { Name = parsed.GuestName };
                    }

                    return parsed;
                }
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

                return new IdentifierResult(null, null, null, null, null, null, null, null);
    }
}
