using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text.RegularExpressions;

namespace WebhookApi.Services
{
    public class TelegramReceiverService : BackgroundService
    {
        private readonly ILogger<TelegramReceiverService> _logger;
        private readonly IConfiguration _configuration;
        private TelegramBotClient? _botClient;

        public TelegramReceiverService(ILogger<TelegramReceiverService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var botToken = _configuration["Telegram:BotToken"];
            if (string.IsNullOrEmpty(botToken))
            {
                _logger.LogWarning("Telegram bot token is not configured.");
                return;
            }

            _botClient = new TelegramBotClient(botToken);

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [] // receive all update types
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
                    _logger.LogError(exception, "Telegram polling error");
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

                    // Simple regex: starts with optional +, then digits, at least 10 digits
                    var phoneRegex = new Regex(@"^\+?\d{10,}$");
                    if (phoneRegex.IsMatch(phoneNumber))
                    {
                        // Prepare the payload
                        var payload = new
                        {
                            message = smsMessage,
                            phoneNumbers = new[] { phoneNumber }
                        };

                        // Read SMS gateway config from appsettings
                        var smsGatewayUrl = _configuration["SmsGateway:Url"] ?? "";
                        var smsGatewayUser = _configuration["SmsGateway:User"] ?? "";
                        var smsGatewayPass = _configuration["SmsGateway:Password"] ?? "";

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
                            _logger.LogError(ex, "Error sending SMS via gateway");
                            await client.SendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "❌ Error sending SMS.",
                                cancellationToken: token);
                        }
                    }
                }
                // else
                // {
                //     await client.SendTextMessageAsync(
                //         chatId: message.Chat.Id,
                //         text: "Please send in the format: <phone_number> <message>",
                //         cancellationToken: token);
                // }
            }
        }
    }
}
