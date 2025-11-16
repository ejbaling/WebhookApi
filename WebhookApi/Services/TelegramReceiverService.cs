using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text.RegularExpressions;

namespace WebhookApi.Services
{
    public partial class TelegramReceiverService(
        ILogger<TelegramReceiverService> logger,
        IConfiguration configuration
    ) : BackgroundService
    {
        private TelegramBotClient? _botClient;
        // Generate the Regex at compile-time
        [GeneratedRegex(@"^\+?\d{3,}$", RegexOptions.CultureInvariant)]
        private static partial Regex PhoneRegex();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
             if (!bool.TryParse(configuration["TelegramReceiverService:Enabled"], out var isEnabled) || !isEnabled)
            {
                logger.LogInformation("Telegram Receiver Service is not enabled via configuration.");
                return;
            }

            var botToken = configuration["Telegram:BotToken"];
            if (string.IsNullOrEmpty(botToken))
            {
                logger.LogWarning("Telegram bot token is not configured.");
                return;
            }

            _botClient = new TelegramBotClient(botToken);

            UpdateType[] allowedUpdates = [];
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = allowedUpdates
            };

            _botClient.StartReceiving(
                async (client, update, token) =>
                {
                    if (update.Message is { } message)
                    {
                        await HandleIncomingMessageAsync(client, message, token);
                    }
                },
                (client, exception, token) =>
                {
                    logger.LogError(exception, "Telegram polling error");
                    return Task.CompletedTask;
                },
                receiverOptions,
                stoppingToken
            );

            // Keep the background service alive
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async Task HandleIncomingMessageAsync(ITelegramBotClient client, Message message, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                var parts = message.Text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    var phoneNumber = parts[0];
                    var smsMessage = parts[1];

                    // Use compiled static regex: starts with optional +, then digits
                    if (PhoneRegex().IsMatch(phoneNumber))
                    {
                        // Prepare the payload
                        var payload = new
                        {
                            message = smsMessage,
                            phoneNumbers = new[] { phoneNumber }
                        };

                        // Read SMS gateway config from appsettings
                        var smsGatewayUrl = configuration["SmsGateway:Url"] ?? "";
                        var smsGatewayUser = configuration["SmsGateway:User"] ?? "";
                        var smsGatewayPass = configuration["SmsGateway:Password"] ?? "";

                        using var httpClient = new HttpClient();
                        var byteArray = System.Text.Encoding.ASCII.GetBytes($"{smsGatewayUser}:{smsGatewayPass}");
                        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                        var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                        try
                        {
                            var response = await httpClient.PostAsync(smsGatewayUrl, content, token);
                            if (response.IsSuccessStatusCode)
                            {
                                await client.SendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: "✅ SMS sent successfully!",
                                    cancellationToken: token);
                            }
                            else
                            {
                                await client.SendTextMessageAsync(
                                    chatId: message.Chat.Id,
                                    text: $"❌ Failed to send SMS. Status: {response.StatusCode}",
                                    cancellationToken: token);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error sending SMS via gateway");
                            await client.SendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "❌ Error sending SMS.",
                                cancellationToken: token);
                        }
                    }
                }
            }
        }
    }
}
