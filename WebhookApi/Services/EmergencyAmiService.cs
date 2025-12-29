using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WebhookApi.Services;

public class EmergencyAmiService : IEmergencyAmiService
{
    private readonly ILogger<EmergencyAmiService> _logger;
    private readonly IConfiguration _config;

    public EmergencyAmiService(ILogger<EmergencyAmiService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task TriggerEmergencyAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        var amiHost = _config["Asterisk:Host"] ?? "127.0.0.1";
        var amiPort = int.TryParse(_config["Asterisk:Port"], out var p) ? p : 5038;
        var amiUser = _config["Asterisk:User"] ?? string.Empty;
        var amiSecret = _config["Asterisk:Secret"] ?? string.Empty;

        // Read extensions from config (JSON array "Asterisk:Extensions" only)
        var extensions = _config.GetSection("Asterisk:Extensions").Get<string[]>();
        if (extensions == null || extensions.Length == 0)
        {
            _logger.LogWarning("No Asterisk:Extensions configured; falling back to default extension 105");
            extensions = new[] { "105" };
        }

        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(amiHost, amiPort, cts.Token);

            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            // Login once
            await writer.WriteLineAsync("Action: Login");
            await writer.WriteLineAsync($"Username: {amiUser}");
            await writer.WriteLineAsync($"Secret: {amiSecret}");
            await writer.WriteLineAsync(string.Empty);

            // allow login response
            await Task.Delay(150, cts.Token);

            foreach (var ext in extensions)
            {
                _logger.LogInformation("Originating to extension {Ext}", ext);

                await writer.WriteLineAsync("Action: Originate");
                await writer.WriteLineAsync($"Channel: Local/{ext}@from-internal");
                await writer.WriteLineAsync("Context: from-internal");
                await writer.WriteLineAsync($"Exten: {ext}");
                await writer.WriteLineAsync("Priority: 1");
                await writer.WriteLineAsync("CallerID: Airbnb Emergency <911>");
                await writer.WriteLineAsync("Async: true");
                await writer.WriteLineAsync(string.Empty);

                // best-effort read one response line per originate
                var response = await reader.ReadLineAsync();
                _logger.LogInformation("AMI response for {Ext} first line: {Response}", ext, response ?? "<none>");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AMI originate cancelled or timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to originate AMI call for emergency alert");
        }
    }
}
