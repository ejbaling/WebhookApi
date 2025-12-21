using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace WebhookApi.Services;

public static class TelegramWebhookEndpoints
{
    private record PendingAction(string ActionName, Dictionary<string, string> Parameters, long RequestedBy, DateTime CreatedAt);

    public static void MapTelegramWebhookEndpoints(this WebApplication app)
    {
        var cfg = app.Configuration.GetSection("Telegram");
        var botClient = app.Services.GetRequiredService<TelegramBotClient>();

        long.TryParse(cfg["AllowedUserId"], out var allowedUserId);

        var auditLog = new List<string>();
        var pendingActions = new ConcurrentDictionary<string, PendingAction>(StringComparer.OrdinalIgnoreCase);

        var tools = new Dictionary<string, Func<Dictionary<string, string>, Task<string>>>(StringComparer.OrdinalIgnoreCase)
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
        var executionSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();

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

                // CallbackQuery handling: expect data like "confirm:{id}" or "cancel:{id}"
                if (root.TryGetProperty("callback_query", out var cbElem) && cbElem.ValueKind == JsonValueKind.Object)
                {
                    if (!cbElem.TryGetProperty("message", out var msgElem) || msgElem.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest();

                    var chatId = msgElem.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                    var data = cbElem.GetProperty("data").GetString() ?? string.Empty;
                    var fromId = cbElem.TryGetProperty("from", out var cbFrom) && cbFrom.TryGetProperty("id", out var cbFromId) && cbFromId.ValueKind == JsonValueKind.Number
                        ? cbFromId.GetInt64()
                        : 0L;

                    if (data.StartsWith("confirm:", StringComparison.OrdinalIgnoreCase) || data.StartsWith("cancel:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = data.Split(':', 2);
                        if (parts.Length != 2)
                        {
                            await botClient.SendTextMessageAsync(chatId, "Invalid callback data.");
                            return Results.Ok();
                        }

                        var kind = parts[0].ToLowerInvariant();
                        var id = parts[1];

                        var sem = executionSemaphores.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                        await sem.WaitAsync();
                        try
                        {
                            if (!pendingActions.TryGetValue(id, out var pending))
                            {
                                await botClient.SendTextMessageAsync(chatId, "This action has expired or is unknown.");
                                return Results.Ok();
                            }

                            if (kind == "cancel")
                            {
                                // remove when cancelled
                                pendingActions.TryRemove(id, out _);
                                await botClient.SendTextMessageAsync(chatId, "❌ Action cancelled.");
                                auditLog.Add($"[{DateTime.UtcNow}] Action {pending.ActionName} cancelled by {fromId}");
                                return Results.Ok();
                            }

                            if (!tools.TryGetValue(pending.ActionName, out var executor))
                            {
                                await botClient.SendTextMessageAsync(chatId, $"Unknown action: {pending.ActionName}");
                                return Results.Ok();
                            }

                            if (pending.RequestedBy != 0 && fromId != pending.RequestedBy)
                            {
                                await botClient.SendTextMessageAsync(chatId, "You are not authorized to confirm this action.");
                                return Results.Ok();
                            }

                            try
                            {
                                var result = await executor(pending.Parameters);
                                // remove only after success
                                pendingActions.TryRemove(id, out _);
                                await botClient.SendTextMessageAsync(chatId, $"✅ {result}");
                                auditLog.Add($"[{DateTime.UtcNow}] Action {pending.ActionName} confirmed by {fromId}");
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Failed executing action {Action} id={Id}", pending.ActionName, id);
                                await botClient.SendTextMessageAsync(chatId, $"Error executing action (kept pending): {ex.Message}");
                            }
                        }
                        finally
                        {
                            sem.Release();
                            executionSemaphores.TryRemove(id, out _);
                        }

                        return Results.Ok();
                    }

                    return Results.Ok();
                }

                // Message handling
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
                        // create a pending action for commands that need confirmation
                        if (text.StartsWith("/shutdown", StringComparison.OrdinalIgnoreCase))
                        {
                            var id = Guid.NewGuid().ToString("N");
                            var paramsDict = new Dictionary<string, string> { { "environment", "prod" } };
                            var pending = new PendingAction("shutdown_server", paramsDict, fromId, DateTime.UtcNow);
                            pendingActions[id] = pending;

                            var inlineKeyboard = new InlineKeyboardMarkup(new[]
                            {
                                new []
                                {
                                    InlineKeyboardButton.WithCallbackData("✅ Yes", $"confirm:{id}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Cancel", $"cancel:{id}")
                                }
                            });

                            await botClient.SendTextMessageAsync(chatId, $"⚠️ This will shutdown the server. Confirm? (id={id.Substring(0,8)})", replyMarkup: inlineKeyboard);
                            auditLog.Add($"[{DateTime.UtcNow}] Shutdown requested by {fromId}, id={id}");
                        }
                        else if (text.StartsWith("/lights_off", StringComparison.OrdinalIgnoreCase))
                        {
                            // immediate action for lights_off
                            string result = await tools["lights_off"](new Dictionary<string, string>());
                            await botClient.SendTextMessageAsync(chatId, result);
                            auditLog.Add($"[{DateTime.UtcNow}] Lights off executed by {fromId}");
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(chatId, "Unknown command.");
                        }
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(chatId, $"You said: {text}\nLater we can parse this with AI/MCP.");
                    }

                    return Results.Ok();
                }

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
}
