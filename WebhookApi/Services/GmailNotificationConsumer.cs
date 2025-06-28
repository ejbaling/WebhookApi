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

namespace WebhookApi.Services;

public class GmailPushData
{
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("publishTime")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("historyId")]
    public ulong HistoryId { get; set; }
}

public class GmailNotification
{
    [JsonPropertyName("message")]
    public GmailPushData? Message { get; set; }
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
                consumer.ReceivedAsync  += async (model, ea) =>
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);

                    try
                    {
                        // _logger.LogInformation("Processing message: {Message}", message);
                        
                        var notification = JsonSerializer.Deserialize<GmailNotification>(message);
                        if (notification != null)
                        {
                            // Process the notification
                            await ProcessNotification(notification);
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
    
                        _logger.LogError(ex, "Error processing message: {Message}", message);
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

    private async Task ProcessNotification(GmailNotification notification)
    {
        // _logger.LogInformation("Processing Gmail notification from {Timestamp}: {MessageId}",
        //     notification.Message?.Timestamp, notification.Message?.MessageId);

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

        if (!string.IsNullOrEmpty(notification.Message?.Data))
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(notification.Message.Data));
            var pushData = JsonSerializer.Deserialize<GmailPushData>(json);

            // ulong startHistoryId = pushData?.HistoryId ?? 0;
            // if (_lastProcessedHistoryId > 0 && _lastProcessedHistoryId < startHistoryId)
            // {
            //     startHistoryId = _lastProcessedHistoryId;
            // }

            if (pushData?.HistoryId > 0)
            {
                // Simplified: single attempt to fetch new messages
                var historyRequest = gmailService.Users.History.List("me");
                historyRequest.StartHistoryId = _lastProcessedHistoryId > 0 ? _lastProcessedHistoryId : pushData.HistoryId;
                historyRequest.HistoryTypes = UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded;
                var historyResponse = await historyRequest.ExecuteAsync();

                if (historyResponse.History != null)
                {
                    foreach (var history in historyResponse.History)
                    {
                        if (history.MessagesAdded != null)
                        {
                            foreach (var msgAdded in history.MessagesAdded)
                            {
                                var messageId = msgAdded.Message.Id;
                                var message = await gmailService.Users.Messages.Get("me", messageId).ExecuteAsync();
                                _logger.LogInformation("Fetched Gmail message snippet: {Snippet}", message.Snippet);
                                // You can process the message here
                                //var messageJson = JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = true });
                                //_logger.LogInformation("Full Gmail message: {MessageJson}", messageJson);
                            }
                        }
                    }
                    // Update in-memory last processed historyId
                    _lastProcessedHistoryId = Math.Max(_lastProcessedHistoryId, historyResponse.HistoryId ?? _lastProcessedHistoryId);
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
