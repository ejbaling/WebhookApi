using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace WebhookApi.Services;

public class GmailNotification
{
    public string? Message { get; set; }
    public DateTime Timestamp { get; set; }
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

    public GmailNotificationConsumer(
        IConnectionFactory connectionFactory,
        ILogger<GmailNotificationConsumer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
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
            _logger.LogInformation("Gmail notification consumer started");

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
                        _logger.LogInformation("Processing message: {Message}", message);
                        
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
        _logger.LogInformation("Processing Gmail notification from {Timestamp}: {Message}",
            notification.Timestamp, notification.Message);
        // Add your specific notification processing logic here
        await Task.CompletedTask;
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
