using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Telegram.Bot;
using WebhookApi.Data;
using RedwoodIloilo.Common.Entities;
using RedwoodIloilo.Common.Models;
using Microsoft.EntityFrameworkCore;

namespace WebhookApi.Services;

public class GmailMessageData
{
    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("historyId")]
    public ulong HistoryId { get; set; }
}

public class GmailMessage
{
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("publishTime")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

public class GmailPushData
{
    [JsonPropertyName("message")]
    public GmailMessage? Message { get; set; }

    [JsonPropertyName("subscription")]
    public string? subscription { get; set; }
}

public partial class GmailNotificationConsumer : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<GmailNotificationConsumer> _logger;
    private const string QueueName = "gmail-notifications";
    private readonly int _maxRetryAttempts = 10;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HttpClient _httpClient;

    // In-memory storage for last processed historyId (development only)
    private static ulong _lastProcessedHistoryId = 0;

    public GmailNotificationConsumer(
        IConnectionFactory connectionFactory,
        ILogger<GmailNotificationConsumer> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _httpClient = httpClientFactory.CreateClient();
    }

    private async Task<bool> TryConnect()
    {
        try
        {
            _connection = await _connectionFactory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            await _channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            // Enable message persistence
            await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!bool.TryParse(_configuration["GmailNotification:Enabled"], out var isEnabled) || !isEnabled)
        {
            _logger.LogInformation("Gmail notification consumer is disabled via configuration.");
            return;
        }

        var retryCount = 0;
        while (!stoppingToken.IsCancellationRequested && retryCount < _maxRetryAttempts)
        {
            if (!await TryConnect())
            {
                retryCount++;
                _logger.LogWarning("Retrying connection to RabbitMQ in {Delay} seconds... (Attempt {Count}/{Max})",
                    _reconnectDelay.TotalSeconds, retryCount, _maxRetryAttempts);
                await Task.Delay(_reconnectDelay, stoppingToken);
                continue;
            }

            if (_channel == null)
            {
                _logger.LogError("RabbitMQ channel is null. Cannot start consumer.");
                break;
            }

            retryCount = 0; // Reset retry count on successful connection
            // _logger.LogInformation("Gmail notification consumer started");

            try
            {
                Console.WriteLine(" [*] Waiting for messages.");

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += async (model, ea) =>
                {
                    var body = ea.Body.ToArray();
                    var strBody = Encoding.UTF8.GetString(body);

                    try
                    {
                        _logger.LogInformation("Processing message: {Message}", strBody);

                        var gmailPushData = JsonSerializer.Deserialize<GmailPushData>(strBody);
                        if (gmailPushData?.Message != null)
                        {
                            // Process the notification
                            await ProcessNotification(gmailPushData.Message);
                        }

                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        await _channel.BasicNackAsync(
                            deliveryTag: ea.DeliveryTag,
                            multiple: false,
                            requeue: true
                        );

                        _logger.LogError(ex, "Error processing message: {Message}", strBody);
                        // Consider implementing dead letter queue handling here
                    }
                };

                await _channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer);


                // Keep the service running until cancellation is requested
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in consumer execution");
                await Task.Delay(_reconnectDelay, stoppingToken);
            }
            finally
            {
                CleanupConnection();
            }
        }

        if (retryCount >= _maxRetryAttempts)
        {
            _logger.LogError("Failed to connect to RabbitMQ after {Count} attempts", _maxRetryAttempts);
        }
    }

    private async Task ProcessNotification(GmailMessage gmailMessage)
    {
        _logger.LogInformation("Processing Gmail notification from {Timestamp}: {MessageId}",
            gmailMessage.Timestamp, gmailMessage.MessageId);

        // Load Gmail API credentials from configuration
        var refreshToken = _configuration["Google:RefreshToken"];
        var clientId = _configuration["Google:ClientId"];
        var clientSecret = _configuration["Google:ClientSecret"];

        if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogError("Google API credentials are not configured properly.");
            return;
        }

        var token = new Google.Apis.Auth.OAuth2.Responses.TokenResponse { RefreshToken = refreshToken };
        var secrets = new Google.Apis.Auth.OAuth2.ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = new[] { GmailService.Scope.GmailReadonly }
        });
        var credential = new Google.Apis.Auth.OAuth2.UserCredential(flow, "user", token);

        var gmailService = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "WebhookApi"
        });

        if (!string.IsNullOrWhiteSpace(gmailMessage.Data))
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(gmailMessage.Data));
            _logger.LogInformation("Received Gmail push notification data: {Data}", json);
            var data = JsonSerializer.Deserialize<GmailMessageData>(json);

            if (data != null && data.HistoryId > 0)
            {
                if (_lastProcessedHistoryId == 0)
                    _lastProcessedHistoryId = data.HistoryId;

                // Simplified: single attempt to fetch new messages
                var historyRequest = gmailService.Users.History.List("me");
                historyRequest.StartHistoryId = _lastProcessedHistoryId;
                historyRequest.HistoryTypes = UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded;
                var historyResponse = await historyRequest.ExecuteAsync();

                if (historyResponse.History != null && historyResponse.HistoryId.HasValue)
                {
                    _logger.LogInformation("Processing Gmail history from ID: {HistoryId}", historyResponse.HistoryId);

                    // Process each history record
                    foreach (var record in historyResponse.History)
                    {
                        if (record.MessagesAdded != null)
                        {
                            foreach (var msgAdded in record.MessagesAdded)
                            {
                                var messageId = msgAdded.Message.Id;
                                try
                                {
                                    var message = await gmailService.Users.Messages.Get("me", messageId).ExecuteAsync();
                                    //_logger.LogInformation("Fetched Gmail message snippet: {Snippet}", message.Snippet);
                                    // Optionally log the full message as JSON
                                    // var messageJson = JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = true });
                                    // _logger.LogInformation("Full Gmail message: {MessageJson}", messageJson);

                                    string subject = message.Payload?.Headers?.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(No Subject)";
                                    _logger.LogInformation("Email subject: {Subject}", subject);

                                    var emailBody = message.Payload != null ? ExtractMessage(GetEmailBody(message.Payload), 1024) : string.Empty;

                                    // Extract date range from subject and check if today is in range
                                    bool? isInRange = IsCurrentDateInReservationRange(subject);
                                    QaResponse? qaResponse = null;

                                    if (isInRange.HasValue)
                                    {
                                        _logger.LogInformation("Current date is {Status} the reservation range.", isInRange.Value ? "within" : "outside");
                                        if (isInRange.Value)
                                        {

                                            Config? aiConfig = null;
                                            using (var scope = _scopeFactory.CreateScope()) 
                                            {
                                                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                                                aiConfig = await dbContext.Configs.FirstOrDefaultAsync(c => c.Key == "AiEnabled");
                                            }

                                            if (aiConfig?.Value == true)
                                            {
                                                // Fetch relevant rules from DB first
                                                List<Rule> relevantRules;
                                                using (var scope = _scopeFactory.CreateScope())
                                                {
                                                    var ruleRepository = scope.ServiceProvider.GetRequiredService<IRuleRepository>();
                                                    relevantRules = await ruleRepository.GetRelevantRulesAsync(emailBody);
                                                }

                                                // Group by category
                                                var rulesData = relevantRules
                                                    .GroupBy(r => r.RuleCategory.Name)
                                                    .Select(g => new
                                                    {
                                                        category = g.Key,
                                                        rules = g.Select(r => r.RuleText).ToList()
                                                    })
                                                    .ToList();

                                                // Check if rules are empty and use fallback if needed
                                                string rulesJson;
                                                if (rulesData.Count != 0)
                                                {
                                                    var rulesObject = new { rules = rulesData };
                                                    rulesJson = JsonSerializer.Serialize(rulesObject);

                                                }
                                                else
                                                    rulesJson = HouseRules.RulesJson; // Fallback

                                                var request = new
                                                {
                                                    question = emailBody,
                                                    rules = rulesJson
                                                };

                                                var result = await _httpClient.PostAsJsonAsync("http://100.80.77.91:8000/qa", request);
                                                string response = string.Empty;
                                                if (result != null && result.IsSuccessStatusCode == true && result.Content != null)
                                                    response = await result.Content.ReadAsStringAsync();

                                                if (!string.IsNullOrWhiteSpace(response))
                                                    qaResponse = JsonSerializer.Deserialize<QaResponse>(response, JsonOptions.Default);
                                            }

                                            // Forward to Telegram
                                            var botToken = _configuration["Telegram:BotToken"];
                                            var chatId = _configuration["Telegram:ChatId"]; // Your personal Telegram user ID
                                            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
                                            {
                                                _logger.LogError("Telegram BotToken or chatIc is not configured.");
                                                return;
                                            }
                                            
                                            var telegramMessage = $"{ExtractSubject(subject)}: {emailBody}";
                                            // Replace with your actual chatId and botClient instance
                                            var botClient = new TelegramBotClient(botToken);
                                            await botClient.SendTextMessageAsync(
                                                new Telegram.Bot.Types.ChatId(chatId),
                                                text: telegramMessage,
                                                cancellationToken: CancellationToken.None);

                                            if (aiConfig?.Value == true)
                                            {
                                                await botClient.SendTextMessageAsync(
                                                    new Telegram.Bot.Types.ChatId("@redwoodiloiloskycast"),
                                                    text: qaResponse?.Answer ?? "Sorry, no response from AI.",
                                                    cancellationToken: CancellationToken.None);
                                            }

                                            _logger.LogInformation("Forwarded message to Telegram: {Message}", telegramMessage);
                                        }
                                    }

                                    // Save to database
                                    // Determine sender and only save if from airbnb.com
                                    var fromHeader = message.Payload?.Headers?.FirstOrDefault(h => h.Name == "From")?.Value ?? string.Empty;

                                    bool isAirbnbSender = AirbnbRegex().IsMatch(fromHeader);

                                    if (isAirbnbSender)
                                    {
                                        emailBody = message.Payload != null ? ExtractMessage(GetEmailBody(message.Payload), 1024, true) : string.Empty;
                                        using var scope = _scopeFactory.CreateScope();
                                        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                                        var guestMessage = new GuestMessage
                                        {
                                            Message = emailBody,
                                            Language = "en",
                                            Category = "reservation",
                                            Sentiment = "neutral",
                                            ReplySuggestion = null
                                        };
                                        dbContext.GuestMessages.Add(guestMessage);

                                        var guestResponse = new GuestResponse
                                        {
                                            GuestMessage = guestMessage,
                                            Response = qaResponse?.Answer ?? "Sorry, no response from AI.",
                                            CreatedAt = DateTime.UtcNow
                                        };
                                        dbContext.GuestResponses.Add(guestResponse);

                                        await dbContext.SaveChangesAsync();

                                        _logger.LogInformation("Saved guest message to database with ID: {Id}", guestMessage.Id);
                                    }
                                }
                                catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                                {
                                    _logger.LogWarning("Gmail message with ID {MessageId} not found. Skipping. Error: {Error}", messageId, ex.Message);
                                }
                            }
                        }
                    }
                    // Update in-memory last processed historyId
                    _lastProcessedHistoryId = (ulong)historyResponse.HistoryId;
                }
                else
                {
                    _logger.LogWarning("No new messages found in Gmail history.");
                }
            }
            else
            {
                _logger.LogWarning("No valid historyId found in push notification.");
            }
        }
        else
        {
            _logger.LogWarning("Notification does not contain a data field.");
        }
    }

    private string ExtractSubject(string rawMessage, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return string.Empty;

        // Step 1: Remove email headers (From:, Date:, Subject:, To:)
        string[] headerPrefixes = { "From:", "Date:", "Subject:", "To:" };
        string[] skipPrefixes = { "Reply", "You can also respond", "<", "." };

        var lines = rawMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.None)
            .Select(RemoveInvisibleCharacters)
            .Select(line => line.Trim())
            .Where(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !headerPrefixes.Any(prefix => line.StartsWith(prefix)) &&
                !skipPrefixes.Any(skip => line.StartsWith(skip, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Step 2: Remove URLs (optional)
        for (int i = 0; i < lines.Count; i++)
            lines[i] = Regex.Replace(lines[i], @"https?://[^\s]+", ""); // strip links
        // Remove empty lines
        lines = [.. lines.Where(line => !string.IsNullOrWhiteSpace(line))];

        // Step 3: Remove tags like [image: Airbnb]
        lines = [.. lines.Where(line => !line.TrimStart().StartsWith("[image:", StringComparison.OrdinalIgnoreCase))];

        // Step 4: Join cleaned lines
        string cleanMessage = string.Join("\n", lines);

        // Step 5: Collapse multiple spaces
        cleanMessage = Regex.Replace(cleanMessage, @"\s{2,}", " ");

        // Step 6: Replace the whole string with room number based on room name.
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Rangiora", "Room 1" },
            { "Rimu", "Room 2" },
            { "Kauri", "Room 3" },
            { "Kowhai", "Room 4" }
        };

        string result = cleanMessage;
        foreach (var kvp in mappings)
        {
            if (cleanMessage.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                result = kvp.Value;
                break; // stop at first match
            }
        }

        // Step 7: Truncate to maxLength
        return result.Length <= maxLength
            ? result
            : result.Substring(0, maxLength - 3) + "...";
    }

    private string ExtractMessage(string rawMessage, int maxLength = 160, bool isGuestSection = false)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return string.Empty;

        // Step 1: Remove email headers (From:, Date:, Subject:, To:)
        string[] headerPrefixes = { "From:", "Date:", "Subject:", "To:" };
        string[] skipPrefixes = { "Reply", "You can also respond", "<", ".", "[image:"};

        var lines = rawMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.None)
            .Select(RemoveInvisibleCharacters)
            .Select(line => line.Trim())
            .Where(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !headerPrefixes.Any(prefix => line.StartsWith(prefix)) &&
                !skipPrefixes.Any(skip => line.StartsWith(skip, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var actualGuestLines = new List<string>();

        // Step 2: Remove URLs (optional)
        for (int i = 0; i < lines.Count; i++)
        {
            lines[i] = Regex.Replace(lines[i], @"https?://[^\s]+", ""); // strip links
            if (lines[i].Equals("Booker", StringComparison.OrdinalIgnoreCase))
            {
                isGuestSection = true;
                continue;
            }

            if (isGuestSection)
            {
                // Stop if we hit system markers
                if (lines[i].StartsWith("REDWOOD", StringComparison.OrdinalIgnoreCase) ||
                    lines[i].StartsWith("Check-in", StringComparison.OrdinalIgnoreCase) ||
                    lines[i].StartsWith("Guests", StringComparison.OrdinalIgnoreCase) ||
                    lines[i].StartsWith("Get the app", StringComparison.OrdinalIgnoreCase) ||
                    lines[i].StartsWith("Airbnb", StringComparison.OrdinalIgnoreCase) ||
                    lines[i].StartsWith("Update your email preferences", StringComparison.OrdinalIgnoreCase))
                    break;

                actualGuestLines.Add(lines[i]);
            }
        }

        // Step 3: Join cleaned lines
        string cleanMessage = string.Join("\n", actualGuestLines);

        // Step 4: Collapse multiple spaces
        cleanMessage = Regex.Replace(cleanMessage, @"\s{2,}", " ");

        // Step 5: Truncate to maxLength
        return cleanMessage.Length <= maxLength
            ? cleanMessage
            : cleanMessage.Substring(0, maxLength - 3) + "...";
    }

    // Remove invisible characters (including soft hyphen, non-breaking space, etc.)
    private string RemoveInvisibleCharacters(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Unicode categories that include invisible, formatting, control, etc.
        var clean = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            // CharUnicodeInfo.GetUnicodeCategory helps filter formatting/control chars
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.Format &&
                category != UnicodeCategory.Control &&
                category != UnicodeCategory.OtherNotAssigned)
            {
                clean.Append(c);
            }
        }

        // Remove soft hyphen, non-breaking space, zero-width joiners, etc.
        string result = clean.ToString();
        result = result.Replace("\u00A0", " ")  // non-breaking space
                       .Replace("\u200B", "")   // zero-width space
                       .Replace("\u200C", "")   // zero-width non-joiner
                       .Replace("\u200D", "")   // zero-width joiner
                       .Replace("\uFEFF", "")   // zero-width no-break space
                       .Replace("\u00AD", "")  // soft hyphen
                       .Replace("\u2060", "")  // narrow no-break space
                       .Replace("\u202F", "")  // narrow no-break space
                       .Replace("\u202A", "")  // Bidi controls, this line and below
                       .Replace("\u202B", "")
                       .Replace("\u202C", "")
                       .Replace("\u202D", "")
                       .Replace("\u202E", "");

        return result;
    }

    private string GetEmailBody(MessagePart payload)
    {
        if (payload.Parts == null || payload.Parts.Count == 0)
        {
            return payload.Body?.Data != null ? DecodeBase64(payload.Body.Data) : string.Empty;
        }

        // If there are parts, we assume the first part is the text/plain part
        var textPart = payload.Parts.FirstOrDefault(p => p.MimeType == "text/plain");
        if (textPart != null && textPart.Body?.Data != null)
        {
            return DecodeBase64(textPart.Body.Data);
        }

        // Fallback to the first part's body if no text/plain part found
        return payload.Parts[0].Body?.Data != null ? DecodeBase64(payload.Parts[0].Body.Data) : string.Empty;
    }

    [GeneratedRegex(@"@airbnb\.com\b", RegexOptions.IgnoreCase)]
    private static partial Regex AirbnbRegex();

    // Helper method to decode Base64 safely
    private string DecodeBase64(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return string.Empty;

        // Gmail uses URL-safe Base64
        base64 = base64.Replace('-', '+').Replace('_', '/');

        // Pad with '=' to make length multiple of 4
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }

        try
        {
            byte[] data = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            return "[Invalid base64 format]";
        }
    }

    // Helper: Extract date range from subject and check if current date is in range
    private bool? IsCurrentDateInReservationRange(string subject)
    {
        // Example: Reservation at Redwood Iloilo Kowhai holiday room Apr 18 - 29, 2025 or Apr 18 - 29 and any additional text
        var match = System.Text.RegularExpressions.Regex.Match(subject, @"(\w{3}) (\d{1,2})\s*[–\-]?\s*(?:(\w{3}) (\d{1,2})(?:, (\d{4}))?|(\d{1,2})(?:, (\d{4}))?)\s*(.*)");
        if (match.Success)
        {
            DateTime startDate;
            DateTime endDate;
            var timeZoneId = "Asia/Manila";
            var philippinesTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var philippinesTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, philippinesTimeZone);
            int year = philippinesTime.Year;

            string startMonth = match.Groups[1].Value;
            int startDay = int.Parse(match.Groups[2].Value);

            try
            {
                if (match.Groups[3].Success)
                {
                    // Two different months
                    string endMonth = match.Groups[3].Value;
                    int endDay = int.Parse(match.Groups[4].Value);
                    startDate = DateTime.ParseExact($"{startMonth} {startDay}, {year}", "MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    endDate = DateTime.ParseExact($"{endMonth} {endDay}, {year}", "MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    // Same month
                    string month = startMonth;
                    int endDay = int.Parse(match.Groups[6].Value);
                    startDate = DateTime.ParseExact($"{month} {startDay}, {year}", "MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    endDate = DateTime.ParseExact($"{month} {endDay}, {year}", "MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
                }

                // Adjust start and end times to specific hours
                startDate = startDate.Date.AddHours(6); // Start time at 6 AM
                endDate = endDate.Date.AddHours(19); // End time at 19 PM

                _logger.LogInformation("Reservation start date: {StartDate}, end date: {EndDate}", startDate, endDate);
                _logger.LogInformation("Current Philippines date {PhilippinesTime}", philippinesTime);
                return philippinesTime >= startDate && philippinesTime <= endDate;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error parsing date range from subject: {Subject}", subject);
                _logger.LogError(ex.Message);
            }
        }
        return null;
    }

    private void CleanupConnection()
    {
        try
        {
            if (_channel?.IsOpen == true)
            {
                _channel.Dispose();
            }
            _channel = null;

            if (_connection?.IsOpen == true)
            {
                _connection.Dispose();
            }
            _connection = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during RabbitMQ cleanup");
        }
    }

    public override void Dispose()
    {
        CleanupConnection();
        base.Dispose();
    }
}
