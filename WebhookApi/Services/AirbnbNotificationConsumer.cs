using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WebhookApi.Data;
using RedwoodIloilo.Common.Entities;
using System.Threading.Tasks;
using System.Threading;

namespace WebhookApi.Services;

public class AirbnbNotificationConsumer : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<AirbnbNotificationConsumer> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private const string QueueName = "airbnb-notifications";
    private readonly int _maxRetryAttempts = 10;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);

    public AirbnbNotificationConsumer(
        IConnectionFactory connectionFactory,
        ILogger<AirbnbNotificationConsumer> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
    }

    private async Task<bool> TryConnect()
    {
        try
        {
            _connection = await _connectionFactory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            await _channel.QueueDeclareAsync(queue: QueueName,
                                  durable: true,
                                  exclusive: false,
                                  autoDelete: false,
                                  arguments: null);

            // Prefetch 1 for simple fair dispatch
            await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ for Airbnb consumer");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!bool.TryParse(_configuration["AirbnbNotification:Enabled"], out var isEnabled))
            isEnabled = true; // enabled by default

        if (!isEnabled)
        {
            _logger.LogInformation("Airbnb notification consumer is disabled via configuration.");
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
                _logger.LogError("RabbitMQ channel is null. Cannot start Airbnb consumer.");
                break;
            }

            retryCount = 0;

            try
            {
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += async (model, ea) =>
                {
                    var body = ea.Body.ToArray();
                    var strBody = Encoding.UTF8.GetString(body);

                        // Try to parse a JSON envelope and extract a `title` field for date parsing
                        string? incomingTitle = null;
                        string? incomingMessage = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(strBody);
                            var root = doc.RootElement;
                            if (root.ValueKind == JsonValueKind.Object)
                            {
                                if (root.TryGetProperty("title", out var t)) incomingTitle = t.GetString();
                                if (root.TryGetProperty("message", out var m)) incomingMessage = m.GetString();
                                // Some producers may nest message inside a payload object
                                if (incomingMessage == null && root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("message", out var pm))
                                    incomingMessage = pm.GetString();
                            }
                        }
                        catch
                        {
                            // not JSON or missing fields — fall back to raw body
                        }

                        try
                        {
                            _logger.LogInformation("Processing Airbnb message: {Message}", strBody);

                            using var scope = _scopeFactory.CreateScope();
                            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                            // Try to extract identifiers using registered extractor (if available)
                            IdentifierResult? ids = null;
                            try
                            {
                                var extractor = scope.ServiceProvider.GetService<IIdentifierExtractor>();
                                if (extractor != null)
                                    ids = await extractor.ExtractAsync(strBody, CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Identifier extractor failed for Airbnb message; continuing without AI extraction.");
                            }

                            var suggestion = ids?.Name ?? ids?.BookingId ?? ids?.AirbnbId ?? ids?.Email ?? ids?.Phone ?? ids?.Amount;

                            // Use title field (if present) to determine whether this notification is within a reservation date range
                            bool? isInRange = null;
                            if (!string.IsNullOrWhiteSpace(incomingTitle))
                            {
                                isInRange = IsCurrentDateInReservationRange(incomingTitle);
                                _logger.LogInformation("Title parsed for date range: {Title} => InRange={InRange}", incomingTitle, isInRange);
                            }

                            // If AI marked message as urgent and the reservation is in-range, trigger emergency AMI
                            if (isInRange.HasValue && isInRange.Value && ids?.Urgent == true)
                            {
                                try
                                {
                                    using var amiScope = _scopeFactory.CreateScope();
                                    var amiService = amiScope.ServiceProvider.GetService<IEmergencyAmiService>();
                                    if (amiService != null)
                                    {
                                        await amiService.TriggerEmergencyAsync(CancellationToken.None);
                                        _logger.LogWarning("Triggered emergency AMI originate for urgent Airbnb message.");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to trigger emergency AMI call for Airbnb message");
                                }
                            }

                            var guestMessage = new GuestMessage
                            {
                                Message = incomingMessage ?? strBody,
                                Language = "en",
                                Category = (isInRange.HasValue && isInRange.Value) ? "reservation" : "airbnb",
                                Sentiment = "neutral",
                                ReplySuggestion = suggestion,
                                Name = ids?.Name,
                                Email = ids?.Email,
                                Phone = ids?.Phone,
                                BookingId = ids?.BookingId,
                                AirbnbId = ids?.AirbnbId
                            };

                            dbContext.GuestMessages.Add(guestMessage);

                            var guestResponse = new GuestResponse
                            {
                                GuestMessage = guestMessage,
                                Response = string.Empty,
                                CreatedAt = DateTime.UtcNow
                            };
                            dbContext.GuestResponses.Add(guestResponse);

                            await dbContext.SaveChangesAsync(stoppingToken);

                            if (_channel != null)
                                await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                if (_channel != null)
                                    await _channel.BasicNackAsync(ea.DeliveryTag, false, true);
                            }
                            catch { }
                            _logger.LogError(ex, "Error processing Airbnb message: {Message}", strBody);
                        }
                };

                await _channel.BasicConsumeAsync(queue: QueueName, autoAck: false, consumer: consumer);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Airbnb consumer execution");
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
            _logger.LogError(ex, "Error during Airbnb RabbitMQ cleanup");
        }
    }

    public override void Dispose()
    {
        CleanupConnection();
        base.Dispose();
    }

    // Helper: Extract date range from title and check if current date is in range
    private bool? IsCurrentDateInReservationRange(string subject)
    {
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

                // Adjust start and end times
                startDate = startDate.Date.AddHours(6);
                endDate = endDate.Date.AddHours(23);

                _logger.LogInformation("Reservation start date: {StartDate}, end date: {EndDate}", startDate, endDate);
                return philippinesTime >= startDate && philippinesTime <= endDate;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error parsing date range from title: {Title}", subject);
                _logger.LogError(ex.Message);
            }
        }
        return null;
    }
}
