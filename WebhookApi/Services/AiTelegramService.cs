using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using System.Text.RegularExpressions;
using RedwoodIloilo.Common.Entities;
using WebhookApi.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using System.Text.Json;
using RedwoodIloilo.Common.Models;

namespace WebhookApi.Services
{
    public class AiTelegramService : BackgroundService
    {
        private readonly ILogger<TelegramReceiverService> _logger;
        private readonly IConfiguration _configuration;
        private TelegramBotClient? _aiBotClient;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly HttpClient _httpClient;

        public AiTelegramService(ILogger<TelegramReceiverService> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _scopeFactory = scopeFactory;
            _httpClient = httpClientFactory.CreateClient();
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
            Config? aiConfig = null;
            using (var scope = _scopeFactory.CreateScope()) 
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                aiConfig = await dbContext.Configs.FirstOrDefaultAsync(c => c.Key == "AiEnabled");
            }

            QaResponse? qaResponse = null;
            if (aiConfig?.Value == true)
            {
                List<Rule> relevantRules;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var ruleRepository = scope.ServiceProvider.GetRequiredService<IRuleRepository>();
                    relevantRules = await ruleRepository.GetRelevantRulesAsync(message?.Text ?? string.Empty);
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
                if (rulesData.Any())
                {
                    var rulesObject = new { rules = rulesData };
                    rulesJson = JsonSerializer.Serialize(rulesObject);
                    _logger.LogInformation("############################rulesJson: {rulesJson}", rulesJson);

                }
                else
                {
                    rulesJson = HouseRules.RulesJson; // Fallback
                }

                var request = new
                {
                    question = message?.Text ?? string.Empty,
                    rules = rulesJson
                };

                var result = await _httpClient.PostAsJsonAsync("http://100.80.77.91:8000/qa", request);
                string response = string.Empty;
                if (result != null && result.IsSuccessStatusCode == true && result.Content != null)
                    response = await result.Content.ReadAsStringAsync();

                if (!string.IsNullOrWhiteSpace(response))
                {
                    qaResponse = JsonSerializer.Deserialize<QaResponse>(response, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
            }
            
            await client.SendTextMessageAsync(
                chatId: message?.Chat?.Id ?? 0,
                text: qaResponse?.Answer ?? "Sorry, no response from AI.",
                cancellationToken: token);
        }
    }
}
