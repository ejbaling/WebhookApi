using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using WebhookApi.Data;
using Telegram.Bot;

namespace WebhookApi.Services.Actions
{
    public class AssessGuestExecutor : IActionExecutor
    {
        public string Name => "assess_guest";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AssessGuestExecutor> _logger;
        private readonly TelegramBotClient? _botClient;

        public AssessGuestExecutor(IServiceScopeFactory scopeFactory, ILogger<AssessGuestExecutor> logger, TelegramBotClient? botClient = null)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _botClient = botClient;
        }

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            if (!parameters.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                return "Usage: provide parameter 'name'. Example: { \"name\": \"John\" }";

            name = name.Trim();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var classifier = scope.ServiceProvider.GetRequiredService<IGuestClassifier>();

                var matches = await db.GuestMessages
                    .AsNoTracking()
                    .Where(g => g.Message != null && EF.Functions.ILike(g.Message, $"%{name}%"))
                    .OrderByDescending(g => g.Id)
                    .Take(20)
                    .Select(g => g.Message)
                    .ToListAsync(cancellationToken);

                if (matches.Count == 0)
                    return $"No messages found containing '{name}'.";

                var combined = string.Join("\n---\n", matches);
                if (combined.Length > 20000) combined = combined.Substring(0, 20000);

                var result = await classifier.ClassifyAsync(combined, cancellationToken);

                // // Persist assessment pointing to most recent matching message id
                // var firstId = await db.GuestMessages
                //     .Where(g => g.Message != null && EF.Functions.ILike(g.Message, $"%{name}%"))
                //     .OrderByDescending(g => g.Id)
                //     .Select(g => g.Id)
                //     .FirstOrDefaultAsync(cancellationToken);

                // var assessment = new GuestAssessment
                // {
                //     GuestMessageId = firstId,
                //     IsGood = result.IsGood,
                //     Score = result.Score,
                //     Label = result.Label,
                //     Reason = result.Reason,
                //     EvaluatedAt = DateTime.UtcNow
                // };
                // db.GuestAssessments.Add(assessment);
                // await db.SaveChangesAsync(cancellationToken);

                var responseText = $"Assessment: {result.Label.ToUpper()} (score: {result.Score:F2})\nReason: {result.Reason}";

                // Optional: send to Telegram if caller provided a chat id parameter
                if (_botClient is not null)
                {
                    if (!parameters.TryGetValue("chatId", out var chatId) && !parameters.TryGetValue("chat_id", out chatId))
                    {
                        chatId = null;
                    }

                    if (!string.IsNullOrWhiteSpace(chatId))
                    {
                        try
                        {
                            await _botClient.SendTextMessageAsync(chatId, responseText, cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send assessment to Telegram chatId={ChatId}", chatId);
                        }
                    }
                }

                return responseText;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing AssessGuestExecutor for name={Name}", name);
                return $"Error assessing guest: {ex.Message}";
            }
        }
    }
}
