using System.Text;
using RabbitMQ.Client;
using WebhookApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var hostName = builder.Configuration.GetValue<string>("RabbitMQ:HostName") 
        ?? throw new InvalidOperationException("RabbitMQ:HostName configuration is required");
    
    return new ConnectionFactory { HostName = hostName };
});

// Add RabbitMQ consumer service
builder.Services.AddHostedService<GmailNotificationConsumer>();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

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


app.MapPost("sms/notifications", async (HttpContext context, IConnectionFactory connectionFactory, ILogger<Program> logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var requestBody = await reader.ReadToEndAsync();
    logger.LogInformation("Received SMS webhook: {Payload}", requestBody);

    return Results.Ok("Webhook received");
})
.WithName("ProcessSmsWebhook");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
