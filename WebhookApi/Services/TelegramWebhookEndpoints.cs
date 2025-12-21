using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

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

        var logger = app.Logger;

        app.MapPost("/telegram/webhook", async (HttpRequest req) =>
        {
            var remoteIp = req.HttpContext.Connection.RemoteIpAddress?.ToString();
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            logger.LogInformation("Telegram webhook hit from {RemoteIp}; body: {Body}", remoteIp, body);

            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest();

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // CallbackQuery
                if (root.TryGetProperty("callback_query", out var cbElem) && cbElem.ValueKind == JsonValueKind.Object)
                {
                    if (!cbElem.TryGetProperty("message", out var msgElem) || msgElem.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest();

                    var chatId = msgElem.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                    var data = cbElem.GetProperty("data").GetString() ?? string.Empty;

                    if (data == "confirm_shutdown")
                    {
                        await Task.Delay(500);
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

                // Message
                if (root.TryGetProperty("message", out var messageElem) && messageElem.ValueKind == JsonValueKind.Object)
                {
                    if (!messageElem.TryGetProperty("from", out var fromElem) || fromElem.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest();

                    var fromId = fromElem.GetProperty("id").GetInt64();
                    logger.LogInformation("Telegram message from user ID: {FromId}", fromId);
                    if (allowedUserId == 0 || fromId != allowedUserId)
                    {
                        logger.LogInformation("Dropping Telegram message from {FromId} (not allowed); acknowledging to stop retries", fromId);
                        return Results.Ok(); // acknowledge to Telegram so it won't retry
                    }

                    var chatId = messageElem.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                    var text = messageElem.TryGetProperty("text", out var textElem) && textElem.ValueKind == JsonValueKind.String
                        ? textElem.GetString() ?? string.Empty
                        : string.Empty;

                    if (text.StartsWith("/"))
                    {
                        await HandleCommand(botClient, chatId, text, tools, auditLog);
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(chatId, $"You said: {text}\nLater we can parse this with AI/MCP.");
                    }

                    return Results.Ok();
                }

                // nothing relevant
                return Results.Ok();
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse Telegram webhook JSON");
                return Results.BadRequest();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling Telegram webhook");
                return Results.Problem();
            }
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
