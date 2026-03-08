using System.Text;
using System.Text.Json;
using Serilog;
using RabbitMQ.Client;
using Telegram.Bot;
using WebhookApi.Services;
using WebhookApi.Data;
using Microsoft.EntityFrameworkCore;
using RedwoodIloilo.Common.Entities;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// Ensure Logs directory exists (useful for container bind-mounts)
try
{
    var logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
    Directory.CreateDirectory(logsDir);
}
catch
{
    // ignore directory creation failures here; Serilog will log if it cannot write
}

// Configure Serilog from configuration early so startup logs are captured
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var hostName = builder.Configuration.GetValue<string>("RabbitMQ:HostName") 
        ?? throw new InvalidOperationException("RabbitMQ:HostName configuration is required");
    
    return new ConnectionFactory { HostName = hostName };
});

// Add RabbitMQ consumer service
builder.Services.AddHostedService<GmailNotificationConsumer>();
builder.Services.AddHostedService<WebhookApi.Services.TelegramReceiverService>();
builder.Services.AddHttpClient();
// Token service for programmatic token refresh / client_credentials
builder.Services.AddSingleton<WebhookApi.Services.ITokenService, WebhookApi.Services.TokenService>();
// Renew Gmail watch subscription in background
builder.Services.AddHostedService<WebhookApi.Services.GmailWatchRenewalService>();
// Register intent parser (LLM-backed by default)
builder.Services.AddSingleton<WebhookApi.Services.IIntentParser, WebhookApi.Services.OpenAiIntentParser>();
// Register TelegramBotClient singleton from configuration
builder.Services.AddSingleton<TelegramBotClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>().GetSection("Telegram");
    var token = cfg["AdminBotToken"] ?? string.Empty;
    if (string.IsNullOrEmpty(token))
        throw new InvalidOperationException("Telegram:BotToken configuration is required");
    return new TelegramBotClient(token);
});
// Action executors and registry
builder.Services.AddSingleton<WebhookApi.Services.IActionExecutor, WebhookApi.Services.ShutdownExecutor>();
builder.Services.AddSingleton<WebhookApi.Services.IActionExecutor, WebhookApi.Services.LightsOffExecutor>();
builder.Services.AddSingleton<WebhookApi.Services.IActionExecutor, WebhookApi.Services.Actions.AssessGuestExecutor>();
builder.Services.AddSingleton<WebhookApi.Services.IActionRegistry, WebhookApi.Services.ActionRegistry>();

// Pending action store and semaphore store
builder.Services.AddSingleton<WebhookApi.Services.IPendingActionStore, WebhookApi.Services.PendingActionStore>();
builder.Services.AddSingleton<WebhookApi.Services.ISemaphoreStore, WebhookApi.Services.SemaphoreStore>();
// Add Tailscale monitor only when enabled in configuration
if (builder.Configuration.GetValue<bool?>("Tailscale:Enabled") ?? false)
{
    builder.Services.AddHostedService<WebhookApi.Services.TailscaleMonitorService>();
}
builder.Services.AddHostedService<WebhookApi.Services.AiTelegramService>();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"),
        o => o.UseVector()));

builder.Services.AddScoped<IRuleRepository, RuleRepository>();
// RAG ingestion service
builder.Services.AddScoped<WebhookApi.Services.IRagIngestService, WebhookApi.Services.RagIngestService>();

// Add HttpClient support
builder.Services.AddHttpClient();

// Configure a named HttpClient for AI calls with a configurable timeout (seconds)
var aiTimeoutSeconds = builder.Configuration.GetValue<int?>("AI:TimeoutSeconds") ?? 600; // default 10 minutes
builder.Services.AddHttpClient("ai").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(aiTimeoutSeconds));

// Emergency AMI service for originating calls to PBX
builder.Services.AddSingleton<WebhookApi.Services.IEmergencyAmiService, WebhookApi.Services.EmergencyAmiService>();

// Register OpenAI-backed guest classifier
builder.Services.AddHttpClient<WebhookApi.Services.OpenAiGuestClassifier>(client =>
{
    // BaseAddress and key are configured via appsettings (AI:Endpoint, AI:ApiKey)
    var endpoint = builder.Configuration["AI:Endpoint"] ?? "https://api.openai.com/";
    client.BaseAddress = new Uri(endpoint);
    var key = builder.Configuration["AI:ApiKey"];
    if (!string.IsNullOrWhiteSpace(key))
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
});
builder.Services.AddScoped<WebhookApi.Services.IGuestClassifier, WebhookApi.Services.OpenAiGuestClassifier>();

// Register identifier extractor (OpenAI-backed)
builder.Services.AddScoped<WebhookApi.Services.IIdentifierExtractor, WebhookApi.Services.OpenAiIdentifierExtractor>();

var app = builder.Build();

try
{
    Log.Information("Starting WebhookApi host");

    // Map Telegram webhook endpoints (refactored into service extension)
    app.MapTelegramWebhookEndpoints();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    var summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    app.MapGet("/weatherforecast", () =>
    {
        var forecast = Enumerable.Range(1, 5).Select(index =>
            new WeatherForecast
            (
                DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                Random.Shared.Next(-20, 55),
                summaries[Random.Shared.Next(summaries.Length)]
            ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast");

    app.MapPost("gmail/notifications", async (HttpContext context, IConnectionFactory connectionFactory) =>
    {
        using var reader = new StreamReader(context.Request.Body);
        var requestBody = await reader.ReadToEndAsync();

        // Log or process the webhook payload
        Console.WriteLine($"Received webhook: {requestBody}");

        // Push message to RabbitMQ
        using var connection = await connectionFactory.CreateConnectionAsync();
        using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(queue: "gmail-notifications",
                            durable: true,
                            exclusive: false,
                            autoDelete: false,
                            arguments: null);

        var body = Encoding.UTF8.GetBytes(requestBody);

        await channel.BasicPublishAsync(exchange: "",
                            routingKey: "gmail-notifications",
                            body: body);

        return Results.Ok("Webhook received");
    })
    .WithName("ProcessGmailWebhook");


    app.MapPost("sms/notifications", async (HttpContext context, IConnectionFactory connectionFactory, ILogger<Program> logger, IConfiguration config, IHttpClientFactory httpClientFactory, IEmergencyAmiService amiService) =>
    {
        using var reader = new StreamReader(context.Request.Body);
        var requestBody = await reader.ReadToEndAsync();
        logger.LogInformation("Received SMS webhook: {Payload}", requestBody);

        // Send message to Telegram
        var botToken = config["Telegram:BotToken"];
        var chatId = config["Telegram:ChatId"]; // Your personal Telegram user ID

        if (string.IsNullOrEmpty(botToken))
        {
            logger.LogError("Telegram BotToken is not configured.");
            return Results.Problem("Telegram BotToken is not configured.", statusCode: 500);
        }

        if (string.IsNullOrEmpty(chatId))
        {
            logger.LogError("Telegram ChatId is not configured.");
            return Results.Problem("Telegram ChatId is not configured.", statusCode: 500);
        }

        var botClient = new TelegramBotClient(botToken);

        string messageToSend;
        string phoneNumber = "";

        try
        {
            var json = JsonDocument.Parse(requestBody);
            if (json.RootElement.TryGetProperty("payload", out var payloadElement) &&
                payloadElement.TryGetProperty("message", out var messageElement))
            {
                messageToSend = messageElement.GetString() ?? "";
            }
            else
            {
                messageToSend = "No 'payload.message' property found in requestBody.";
            }

            // Get phoneNumber
            if (payloadElement.TryGetProperty("phoneNumber", out var phoneElement))
            {
                phoneNumber = phoneElement.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            messageToSend = "Invalid JSON in requestBody.";
        }

        // Check if phoneNumber is blacklisted
        var blackListedPhoneNumbers = config.GetSection("BlackListedPhoneNumbers").Get<string[]>();
        if (blackListedPhoneNumbers != null && blackListedPhoneNumbers.Contains(phoneNumber, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogInformation("Phone number {PhoneNumber} is blacklisted. Not sending message to Telegram or SMS, nor calling Asterisk PBX.", phoneNumber);
            return Results.Ok("Phone number is blacklisted.");
        }

        // Emergency detection (trigger AMI) - detect before forwarding
        bool containsHelp = !string.IsNullOrWhiteSpace(messageToSend) &&
            messageToSend.Contains("help", StringComparison.OrdinalIgnoreCase);

        if (containsHelp)
        {
            logger.LogWarning("Emergency help keyword detected in SMS from {PhoneNumber}", phoneNumber);
            var urgentText = $"🚨 EMERGENCY ALERT: Help request detected from {phoneNumber}. Message: {messageToSend}";

            // immediate Telegram notification
            await botClient.SendTextMessageAsync(new Telegram.Bot.Types.ChatId(chatId), text: urgentText, cancellationToken: CancellationToken.None);

            // trigger AMI originate via service - call Asterisk PBX extensions
            await amiService.TriggerEmergencyAsync(CancellationToken.None);
        }

        // Send Telegram message
        await botClient.SendTextMessageAsync(
            new Telegram.Bot.Types.ChatId(chatId),
            text: $"{phoneNumber} {messageToSend}".Trim(),
            cancellationToken: CancellationToken.None);

        // Send SMS via configured SmsGateway (if configured)
        var smsGatewayUrl = config["SmsGateway:Url"] ?? "";
        if (!string.IsNullOrWhiteSpace(smsGatewayUrl))
        {
            // Read an array from configuration
            var targetNumbers = config.GetSection("SmsGateway:ForwardToNumbers").Get<string[]>() ?? Array.Empty<string>();
            if (targetNumbers.Length > 0)
            {
                var smsUser = config["SmsGateway:User"] ?? "";
                var smsPass = config["SmsGateway:Password"] ?? "";

                var client = httpClientFactory.CreateClient(nameof(Program));
                if (!string.IsNullOrEmpty(smsUser) || !string.IsNullOrEmpty(smsPass))
                {
                    var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{smsUser}:{smsPass}"));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
                }

                var payload = new
                {
                    message = messageToSend,
                    phoneNumbers = targetNumbers
                };

                try
                {
                    var smsResponse = await client.PostAsJsonAsync(smsGatewayUrl, payload, CancellationToken.None);
                    if (!smsResponse.IsSuccessStatusCode)
                    {
                        logger.LogWarning("SMS gateway returned non-success status {StatusCode} for {Numbers}", smsResponse.StatusCode, string.Join(", ", targetNumbers));
                    }
                    else
                    {
                        logger.LogInformation("SMS sent to {Numbers} via gateway", string.Join(", ", targetNumbers));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send SMS to {Numbers} via gateway", string.Join(", ", targetNumbers));
                }
            }
            else
            {
                logger.LogWarning("SmsGateway configured but no target numbers available (SmsGateway:ForwardToNumbers empty).");
            }
        }

        return Results.Ok("Webhook received and forwarded to Telegram (and SMS if configured)");
    })
    .WithName("ProcessSmsWebhook");

    // RAG document upload endpoint
    app.MapPost("/api/rag/upload", async (HttpRequest request, WebhookApi.Services.IRagIngestService ingest, ILogger<Program> logger, CancellationToken ct) =>
    {
        string title = string.Empty;
        string source = string.Empty;
        string text = string.Empty;
        string[]? tags = null;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            title = form["title"].FirstOrDefault() ?? string.Empty;
            source = form["source"].FirstOrDefault() ?? string.Empty;
            var file = form.Files.FirstOrDefault();
            if (file != null)
            {
                using var sr = new StreamReader(file.OpenReadStream());
                text = await sr.ReadToEndAsync();
            }
            else
            {
                text = form["text"].FirstOrDefault() ?? string.Empty;
            }

            // Extract tags from form (supports multiple fields or comma-separated)
            var tagsRaw = form["tags"];
            if (tagsRaw.Count > 0)
            {
                tags = tagsRaw
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .SelectMany(s => s!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!)
                    .ToArray();
            }
        }
        else
        {
            // Try to read JSON body { title, source, text, tags }
            using var sr = new StreamReader(request.Body);
            var body = await sr.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("title", out var t)) title = t.GetString() ?? string.Empty;
                    if (root.TryGetProperty("source", out var s)) source = s.GetString() ?? string.Empty;
                    if (root.TryGetProperty("text", out var txt)) text = txt.GetString() ?? string.Empty;

                    if (root.TryGetProperty("tags", out var tagsEl))
                    {
                        if (tagsEl.ValueKind == JsonValueKind.Array)
                        {
                            tags = tagsEl.EnumerateArray()
                                .Select(e => e.GetString())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Select(s => s!)
                                .ToArray();
                        }
                        else if (tagsEl.ValueKind == JsonValueKind.String)
                        {
                            tags = tagsEl.GetString()?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim())
                                .Where(t => t.Length > 0)
                                .ToArray();
                        }
                    }
                }
                catch
                {
                    // fallback: raw body is text
                    text = body;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "no text uploaded" });

        try
        {
            var id = await ingest.IngestDocumentAsync(
                string.IsNullOrWhiteSpace(title) ? "uploaded" : title,
                string.IsNullOrWhiteSpace(source) ? "upload" : source,
                text,
                tags: tags,
                cancellationToken: ct);
            return Results.Accepted($"/api/rag/{id}", new { documentId = id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ingest document");
            return Results.Problem("ingest failed");
        }
    });

    app.Run();

    Log.Information("Stopping WebhookApi host");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
