using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace WebhookApi.Services;

public static class TelegramWebhookEndpoints
{
    public static void MapTelegramWebhookEndpoints(this WebApplication app)
    {
        var cfg = app.Configuration.GetSection("Telegram");
        var botClient = app.Services.GetRequiredService<TelegramBotClient>();

        long.TryParse(cfg["AllowedUserId"], out var allowedUserId);

        var auditLog = new List<string>();
        var tools = new Dictionary<string, Func<Dictionary<string,string>, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            { "shutdown_server", async (parameters) => {
                string env = parameters.GetValueOrDefault("environment", "unknown");
                await Task.Delay(500);
                return $"Server {env} shutdown executed.";
            }},
            { "lights_off", async (parameters) => {
                await Task.Delay(200);
                return "Lights turned off.";
            }}
        };

        app.MapPost("/telegram/webhook", async (HttpRequest req, Update update) =>
        {
            // Telegram delivers both messages and callback queries as Update objects to the webhook URL.
            // Handle callback queries first.
            if (update.CallbackQuery is not null)
            {
                var cb = update.CallbackQuery;
                if (cb.Message == null) return Results.BadRequest();

                string chatId = cb.Message.Chat.Id.ToString();
                string data = cb.Data ?? string.Empty;

                if (data == "confirm_shutdown")
                {
                    await Task.Delay(500); // simulate
                    await botClient.SendTextMessageAsync(chatId, "✅ Server shutdown executed.");
                    auditLog.Add($"[{DateTime.Now}] Shutdown confirmed");
                }
                else if (data == "cancel")
                {
                    await botClient.SendTextMessageAsync(chatId, "❌ Action cancelled.");
                    auditLog.Add($"[{DateTime.Now}] Action cancelled");
                }

                return Results.Ok();
            }

            // Handle normal messages
            if (update.Message is null)
                return Results.Ok();

            if (update.Message.From is null)
                return Results.BadRequest();

            // If AllowedUserId not configured (or invalid) treat as unauthorized
            if (allowedUserId == 0 || update.Message.From.Id != allowedUserId)
                return Results.Unauthorized();

            string chat = update.Message.Chat.Id.ToString();
            string text = update.Message.Text ?? string.Empty;

            if (text.StartsWith("/"))
            {
                await HandleCommand(botClient, chat, text, tools, auditLog);
            }
            else
            {
                await botClient.SendTextMessageAsync(chat, $"You said: {text}\nLater we can parse this with AI/MCP.");
            }

            return Results.Ok();
        });
    }

    private static async Task HandleCommand(TelegramBotClient bot, string chatId, string command, Dictionary<string, Func<Dictionary<string,string>, Task<string>>> tools, List<string> audit)
    {
        if (command.StartsWith("/shutdown"))
        {
            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new []
                {
                    InlineKeyboardButton.WithCallbackData("✅ Yes","confirm_shutdown"),
                    InlineKeyboardButton.WithCallbackData("❌ Cancel","cancel")
                }
            });
            await bot.SendTextMessageAsync(chatId, "⚠️ This will shutdown the server. Confirm?", replyMarkup:inlineKeyboard);
            audit.Add($"[{DateTime.Now}] Shutdown requested");
        }
        else if (command.StartsWith("/lights_off"))
        {
            string result = await tools["lights_off"](new Dictionary<string,string>());
            await bot.SendTextMessageAsync(chatId, result);
            audit.Add($"[{DateTime.Now}] Lights off executed");
        }
    }
}
