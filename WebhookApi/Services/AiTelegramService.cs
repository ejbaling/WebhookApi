using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using System.Text.RegularExpressions;

namespace WebhookApi.Services
{
    public class AiTelegramService : BackgroundService
    {
        private readonly ILogger<TelegramReceiverService> _logger;
        private readonly IConfiguration _configuration;
        private TelegramBotClient? _aiBotClient;

        public AiTelegramService(ILogger<TelegramReceiverService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var aiBotToken = _configuration["Telegram:AiBotToken"];
            if (string.IsNullOrEmpty(aiBotToken))
            {
                _logger.LogWarning("Telegram bot token is not configured.");
                return;
            }

            _aiBotClient = new TelegramBotClient(aiBotToken);

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [] // receive all update types
            };

            _aiBotClient.StartReceiving(
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
                await client.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: message.Text,
                    cancellationToken: token);
            }
        }
    }
}
