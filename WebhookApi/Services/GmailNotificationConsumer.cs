using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;

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

public class GmailNotificationConsumer : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<GmailNotificationConsumer> _logger;
    private const string QueueName = "gmail-notifications";
    private readonly int _maxRetryAttempts = 10;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
    private readonly IConfiguration _configuration;

    // In-memory storage for last processed historyId (development only)
    private static ulong _lastProcessedHistoryId = 0;

    public GmailNotificationConsumer(
        IConnectionFactory connectionFactory,
        ILogger<GmailNotificationConsumer> logger,
        IConfiguration configuration)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _configuration = configuration;
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

                                    // Extract date range from subject and check if today is in range
                                    bool? isInRange = IsCurrentDateInReservationRange(subject);
                                    if (isInRange.HasValue)
                                    {
                                        _logger.LogInformation("Current date is {Status} the reservation range.", isInRange.Value ? "within" : "outside");
                                        if (isInRange.Value)
                                        {
                                            // Forward to Telegram if in range
                                            // Send message to Telegram
                                            var botToken = _configuration["Telegram:BotToken"];
                                            var chatId = _configuration["Telegram:ChatId"]; // Your personal Telegram user ID
                                            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
                                            {
                                                _logger.LogError("Telegram BotToken or chatIc is not configured.");
                                                return;
                                            }
                                            string telegramMessage = $"Reservation in range: {subject}";
                                            // Replace with your actual chatId and botClient instance
                                            var botClient = new TelegramBotClient(botToken);
                                            await botClient.SendTextMessageAsync(
                                                new Telegram.Bot.Types.ChatId(chatId),
                                                text: telegramMessage,
                                                cancellationToken: CancellationToken.None);
                                            _logger.LogInformation("Forwarded message to Telegram: {Message}", telegramMessage);
                                        }
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

    // Helper: Extract date range from subject and check if current date is in range
    private bool? IsCurrentDateInReservationRange(string subject)
    {
        // Example: Reservation at Redwood Iloilo Kowhai holiday room for Apr 18 - 29, 2025
        var match = System.Text.RegularExpressions.Regex.Match(subject, @"for (\w{3}) (\d{1,2}) - (\d{1,2}), (\d{4})");
        if (match.Success)
        {
            string month = match.Groups[1].Value;
            int startDay = int.Parse(match.Groups[2].Value);
            int endDay = int.Parse(match.Groups[3].Value);
            int year = int.Parse(match.Groups[4].Value);
            var startDate = DateTime.ParseExact($"{month} {startDay}, {year}", "MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
            var endDate = DateTime.ParseExact($"{month} {endDay}, {year}", "MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
            var philippinesTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
            var philippinesTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, philippinesTimeZone);
            startDate = startDate.Date; // Start time at 2 PM
            endDate = endDate.Date.AddHours(12); // End time at 12 PM
            _logger.LogInformation("Reservation start date: {StartDate}, end date: {EndDate}", startDate, endDate);
            _logger.LogInformation("Current Philippines date {PhilippinesTime}", philippinesTime);
            return philippinesTime >= startDate && philippinesTime <= endDate;
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
