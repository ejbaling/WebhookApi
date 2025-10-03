using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using Telegram.Bot;
using WebhookApi.Services;
using WebhookApi.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var hostName = builder.Configuration.GetValue<string>("RabbitMQ:HostName") 
        ?? throw new InvalidOperationException("RabbitMQ:HostName configuration is required");
    
    return new ConnectionFactory { HostName = hostName };
});

// Add RabbitMQ consumer service
builder.Services.AddHostedService<GmailNotificationConsumer>();
builder.Services.AddHostedService<WebhookApi.Services.TelegramReceiverService>();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();

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


app.MapPost("sms/notifications", async (HttpContext context, IConnectionFactory connectionFactory, ILogger<Program> logger, IConfiguration config) =>
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
        logger.LogInformation("Phone number {PhoneNumber} is blacklisted. Not sending message to Telegram.", phoneNumber);
        return Results.Ok("Phone number is blacklisted.");
    }

    await botClient.SendTextMessageAsync(
        new Telegram.Bot.Types.ChatId(chatId),
        text: $"{phoneNumber} {messageToSend}".Trim(),
        cancellationToken: CancellationToken.None);

    return Results.Ok("Webhook received and forwarded to Telegram");
})
.WithName("ProcessSmsWebhook");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
