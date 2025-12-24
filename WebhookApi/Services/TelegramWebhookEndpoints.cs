using System.Collections.Concurrent;
using System.Threading;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
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
        var logger = app.Logger;

        var intentParser = app.Services.GetRequiredService<IIntentParser>();
        var actionRegistry = app.Services.GetRequiredService<IActionRegistry>();
        var pendingStore = app.Services.GetRequiredService<IPendingActionStore>();
        var semaphoreStore = app.Services.GetRequiredService<ISemaphoreStore>();

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

                        var sem = semaphoreStore.GetSemaphore(id);
                        await sem.WaitAsync();
                        try
                        {
                            if (!pendingStore.TryGet(id, out var pending))
                            {
                                await botClient.SendTextMessageAsync(chatId, "This action has expired or is unknown.");
                                return Results.Ok();
                            }

                            if (kind == "cancel")
                            {
                                // remove when cancelled
                                pendingStore.TryRemove(id, out _);
                                await botClient.SendTextMessageAsync(chatId, "❌ Action cancelled.");
                                auditLog.Add($"[{DateTime.UtcNow}] Action {pending!.ActionName} cancelled by {fromId}");
                                return Results.Ok();
                            }

                            if (!actionRegistry.TryGetExecutor(pending!.ActionName, out var executor) || executor is null)
                            {
                                await botClient.SendTextMessageAsync(chatId, $"Unknown action: {pending!.ActionName}");
                                return Results.Ok();
                            }

                            if (pending!.RequestedBy != 0 && fromId != pending.RequestedBy)
                            {
                                await botClient.SendTextMessageAsync(chatId, "You are not authorized to confirm this action.");
                                return Results.Ok();
                            }

                            try
                            {
                                // If the action is an AI assessment, include the chatId so the executor can deliver the assessment
                                if (string.Equals(pending!.ActionName, "assess_guest", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!pending.Parameters.ContainsKey("chatId") && !pending.Parameters.ContainsKey("chat_id"))
                                        pending.Parameters["chatId"] = chatId;
                                }

                                var result = await executor.ExecuteAsync(pending!.Parameters, CancellationToken.None);
                                // remove only after success
                                pendingStore.TryRemove(id, out _);

                                if (string.Equals(pending!.ActionName, "assess_guest", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Executor will send the full assessment to the chat; send a short confirmation here
                                    await botClient.SendTextMessageAsync(chatId, "✅ Assessment completed.");
                                    auditLog.Add($"[{DateTime.UtcNow}] Action {pending!.ActionName} confirmed by {fromId} (assessment sent)");
                                }
                                else
                                {
                                    await botClient.SendTextMessageAsync(chatId, $"✅ {result}");
                                    auditLog.Add($"[{DateTime.UtcNow}] Action {pending!.ActionName} confirmed by {fromId}");
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Failed executing action {Action} id={Id}", pending!.ActionName, id);
                                await botClient.SendTextMessageAsync(chatId, $"Error executing action (kept pending): {ex.Message}");
                            }
                        }
                        finally
                        {
                            sem.Release();
                            semaphoreStore.TryRemove(id);
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
                            pendingStore.TryAdd(id, pending);

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
                            if (actionRegistry.TryGetExecutor("lights_off", out var lightExec) && lightExec is not null)
                            {
                                var result = await lightExec.ExecuteAsync(new Dictionary<string,string>(), CancellationToken.None);
                                await botClient.SendTextMessageAsync(chatId, result);
                                auditLog.Add($"[{DateTime.UtcNow}] Lights off executed by {fromId}");
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(chatId, "Lights off action is not available.");
                            }
                        }
                        else
                            await botClient.SendTextMessageAsync(chatId, "Unknown command.");
                    }
                    else
                    {
                        // Use intent parser (rule-based or AI-backed) to map NL to actions
                        var intent = await intentParser.ParseAsync(text);
                        if (intent.Action is null)
                        {
                            await botClient.SendTextMessageAsync(chatId, $"You said: {text}\n(I didn't detect an actionable intent.)");
                        }
                        else
                        {
                            if (!actionRegistry.TryGetExecutor(intent.Action, out var intentExec) || intentExec is null)
                            {
                                await botClient.SendTextMessageAsync(chatId, $"Detected action '{intent.Action}' is not supported.");
                            }
                            else if (intent.RequireConfirm)
                            {
                                var id = Guid.NewGuid().ToString("N");
                                var pending = new PendingAction(intent.Action, intent.Parameters ?? new Dictionary<string,string>(), fromId, DateTime.UtcNow);
                                pendingStore.TryAdd(id, pending);

                                var inlineKeyboard = new InlineKeyboardMarkup(new[]
                                {
                                    new []
                                    {
                                        InlineKeyboardButton.WithCallbackData("✅ Yes", $"confirm:{id}"),
                                        InlineKeyboardButton.WithCallbackData("❌ Cancel", $"cancel:{id}")
                                    }
                                });

                                await botClient.SendTextMessageAsync(chatId, $"⚠️ Confirm: {intent.Action}? (id={id.Substring(0,8)})", replyMarkup: inlineKeyboard);
                                auditLog.Add($"[{DateTime.UtcNow}] Intent {intent.Action} requested by {fromId}, id={id}");
                            }
                            else
                            {
                                // execute immediately
                                var exec = intentExec!;
                                if (string.Equals(intent.Action, "assess_guest", StringComparison.OrdinalIgnoreCase))
                                {
                                    var parameters = intent.Parameters ?? new Dictionary<string,string>();
                                    if (!parameters.ContainsKey("chatId") && !parameters.ContainsKey("chat_id"))
                                        parameters["chatId"] = chatId;

                                    var result = await exec.ExecuteAsync(parameters, CancellationToken.None);
                                    auditLog.Add($"[{DateTime.UtcNow}] Intent {intent.Action} executed by {fromId} (assessment sent)");
                                }
                                else
                                {
                                    var result = await exec.ExecuteAsync(intent.Parameters ?? new Dictionary<string,string>(), CancellationToken.None);
                                    await botClient.SendTextMessageAsync(chatId, result);
                                    auditLog.Add($"[{DateTime.UtcNow}] Intent {intent.Action} executed by {fromId}");
                                }
                            }
                        }
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
