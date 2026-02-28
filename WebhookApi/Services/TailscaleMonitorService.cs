using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace WebhookApi.Services
{
    public class TailscaleMonitorService : BackgroundService
    {
        private readonly ILogger<TailscaleMonitorService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Dictionary<string, bool> _deviceOnlineState = new();

        // Telegram client (optional if configured)
        private TelegramBotClient? _telegramClient;
        private string? _telegramChatId;
        
        public TailscaleMonitorService(ILogger<TailscaleMonitorService> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var pollIntervalSeconds = _configuration.GetValue<int?>("Tailscale:PollIntervalSeconds") ?? 60;
            var offlineMinutesThreshold = _configuration.GetValue<int?>("Tailscale:OfflineMinutesThreshold") ?? 5;

            var allowed = _configuration.GetSection("Tailscale:AllowedDeviceNames")?.Get<string[]>() ?? Array.Empty<string>();
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

            // Initialize Telegram client if configured
            var botToken = _configuration["Telegram:AiBotToken"]?.Trim();
            _telegramChatId = _configuration["Telegram:AiChatId"]?.Trim();
            if (!string.IsNullOrEmpty(botToken) && !string.IsNullOrEmpty(_telegramChatId))
            {
                try
                {
                    _telegramClient = new TelegramBotClient(botToken);
                    // optional quick validation
                    var me = await _telegramClient.GetMeAsync(stoppingToken);
                    _logger.LogInformation("TailscaleMonitor: Telegram bot validated: {Username} ({Id})", me.Username, me.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TailscaleMonitor: Telegram bot could not be initialized; continuing without alerts.");
                    _telegramClient = null;
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var apiKey = _configuration["Tailscale:ApiKey"];
                    var tailnet = _configuration["Tailscale:Tailnet"];

                    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(tailnet))
                    {
                        _logger.LogWarning("Tailscale API key or tailnet not configured. Set 'Tailscale:ApiKey' and 'Tailscale:Tailnet' in configuration.");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(pollIntervalSeconds, 60)), stoppingToken);
                        continue;
                    }

                    var client = _httpClientFactory.CreateClient(nameof(TailscaleMonitorService));
                    var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:"));
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

                    var url = $"https://api.tailscale.com/api/v2/tailnet/{tailnet}/devices";
                    using var resp = await client.GetAsync(url, stoppingToken);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Tailscale API request failed: {StatusCode}", resp.StatusCode);
                        await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
                        continue;
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync(stoppingToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: stoppingToken);

                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("devices", out JsonElement devicesElement))
                    {
                        // ok
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        devicesElement = doc.RootElement;
                    }
                    else
                    {
                        _logger.LogWarning("Unexpected Tailscale API response format");
                        await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
                        continue;
                    }

                    foreach (var device in devicesElement.EnumerateArray())
                    {
                        string id = device.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                            ? idProp.GetString() ?? "<unknown>"
                            : "<unknown>";

                        string name = device.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                            ? nm.GetString() ?? "<unknown>"
                            : "<unknown>";

                        if (allowedSet.Count > 0 && !allowedSet.Contains(name))
                            continue;

                        bool isOnline = false;
                        if (device.TryGetProperty("online", out var onlineProp) && onlineProp.ValueKind == JsonValueKind.True)
                        {
                            isOnline = true;
                        }
                        else if (device.TryGetProperty("online", out var onlineProp2) && onlineProp2.ValueKind == JsonValueKind.False)
                        {
                            isOnline = false;
                        }
                        else if (device.TryGetProperty("lastSeen", out var lastSeenProp) && lastSeenProp.ValueKind == JsonValueKind.String && DateTime.TryParse(lastSeenProp.GetString(), out var lastSeen))
                        {
                            var age = DateTime.UtcNow - lastSeen.ToUniversalTime();
                            isOnline = age.TotalMinutes <= offlineMinutesThreshold;
                        }
                        else if (device.TryGetProperty("LastSeen", out var lastSeenProp2) && lastSeenProp2.ValueKind == JsonValueKind.String && DateTime.TryParse(lastSeenProp2.GetString(), out var lastSeen2))
                        {
                            var age = DateTime.UtcNow - lastSeen2.ToUniversalTime();
                            isOnline = age.TotalMinutes <= offlineMinutesThreshold;
                        }

                        _deviceOnlineState.TryGetValue(id, out var previousOnline);

                        if (!isOnline && previousOnline)
                        {
                            _logger.LogWarning("Tailscale device went offline: {Name} ({Id})", name, id);
                            // send Telegram alert if configured
                            if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                            {
                                _ = SendOfflineAlertAsync(name, id, stoppingToken);
                            }
                        }
                        else if (isOnline && !previousOnline && _deviceOnlineState.ContainsKey(id))
                        {
                            _logger.LogInformation("Tailscale device back online: {Name} ({Id})", name, id);
                            // send Telegram alert for device back online if configured
                            if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                            {
                                _ = SendOnlineAlertAsync(name, id, stoppingToken);
                            }

                            // If this device matches the configured sync target, attempt to set its device time
                            try
                            {
                                var syncTargetName = _configuration["Tailscale:SyncTargetName"]?.Trim();
                                if (!string.IsNullOrEmpty(syncTargetName) && string.Equals(syncTargetName, name, StringComparison.OrdinalIgnoreCase))
                                    _ = SetDeviceTimeAsync("SONOFF SPM", stoppingToken);

                                
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to trigger SetDeviceTimeAsync for device {Name} ({Id})", name, id);
                            }
                            
                        }
                        
                        _deviceOnlineState[id] = isOnline;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // shutting down
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while polling Tailscale API");
                }

                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
            }
        }

        private async Task SendOfflineAlertAsync(string name, string id, CancellationToken ct)
        {
            try
            {
                if (_telegramClient is null || string.IsNullOrEmpty(_telegramChatId))
                    return;

                var text = $"Tailscale device offline: {name} ({id}) at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
                await _telegramClient.SendTextMessageAsync(new ChatId(_telegramChatId), text, cancellationToken: ct);
                _logger.LogInformation("Sent Telegram alert for offline device {Name} ({Id})", name, id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // ignore cancellation during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram alert for device {Name} ({Id})", name, id);
            }
        }

        private async Task SendOnlineAlertAsync(string name, string id, CancellationToken ct)
        {
            try
            {
                if (_telegramClient is null || string.IsNullOrEmpty(_telegramChatId))
                    return;

                var text = $"Tailscale device back online: {name} ({id}) at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
                await _telegramClient.SendTextMessageAsync(new ChatId(_telegramChatId), text, cancellationToken: ct);
                _logger.LogInformation("Sent Telegram alert for online device {Name} ({Id})", name, id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // ignore cancellation during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram alert for device {Name} ({Id})", name, id);
            }
        }

        private async Task SetDeviceTimeAsync(string name, CancellationToken ct)
        {
            string? deviceIdToSend = null;

            try
            {
                // Require a configured port for the target device
                var port = _configuration.GetValue<int?>("Tailscale:SyncDevicePort");
                if (!port.HasValue)
                {
                    _logger.LogWarning("SetDeviceTimeAsync: no SyncDevicePort configured; skipping time set for {Name}.", name);
                    return;
                }

                // Use configured IP for the target device
                var ip = _configuration["Tailscale:SyncDeviceIp"]?.Trim();
                if (string.IsNullOrWhiteSpace(ip))
                {
                    _logger.LogWarning("SetDeviceTimeAsync: no SyncDeviceIp configured; skipping time set for {Name}.", name);
                    return;
                }

                // Build payload and base address using helpers
                string? tzConfig = _configuration["Tailscale:Timezone"];
                if (string.IsNullOrWhiteSpace(tzConfig))
                {
                    _logger.LogWarning("SetDeviceTimeAsync: no timezone configured (Tailscale:Timezone); skipping time set for {Name}.", name);
                    return;
                }

                deviceIdToSend = _configuration["Tailscale:SyncDeviceId"]?.Trim();
                var nowUtc = DateTime.UtcNow;

                TimePayload payload;
                try
                {
                    payload = TailscaleHelpers.CreateTimePayload(nowUtc, tzConfig, deviceIdToSend);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SetDeviceTimeAsync: configured timezone '{Tz}' is invalid; skipping time set for {Name}.", tzConfig, name);
                    return;
                }

                // Prepare HTTP client
                var client = _httpClientFactory.CreateClient(nameof(TailscaleMonitorService));

                string baseAddress;
                try
                {
                    baseAddress = TailscaleHelpers.BuildDeviceBaseAddress(ip, port.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SetDeviceTimeAsync: failed to build base address from SyncDeviceIp '{Ip}' and port {Port}; skipping time set for {Name}.", ip, port.Value, name);
                    return;
                }

                client.BaseAddress = new Uri(baseAddress);

                _logger.LogInformation("SetDeviceTimeAsync - POST {BaseUrl}zeroconf/time payload: {@Payload}", client.BaseAddress, payload);

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync("zeroconf/time", content, ct);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("SetDeviceTimeAsync succeeded for device {Name} with status {Status}", name, resp.StatusCode);
                    try
                    {
                        if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                        {
                            var text = $"Set device time succeeded: {name} ({deviceIdToSend}) at {nowUtc:yyyy-MM-dd HH:mm:ss} UTC, status {resp.StatusCode}";
                            await _telegramClient.SendTextMessageAsync(new ChatId(_telegramChatId), text, cancellationToken: ct);
                            _logger.LogInformation("Sent Telegram alert for successful time set for device {Name}", name);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // ignore cancellation during shutdown
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send Telegram success alert for device {Name}", name);
                    }
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("SetDeviceTimeAsync returned {Status} for device {Name}: {Body}", resp.StatusCode, name, body);
                    try
                    {
                        if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                        {
                            var text = $"Set device time FAILED: {name} ({deviceIdToSend}) at {nowUtc:yyyy-MM-dd HH:mm:ss} UTC, status {resp.StatusCode}: {body}";
                            await _telegramClient.SendTextMessageAsync(new ChatId(_telegramChatId), text, cancellationToken: ct);
                            _logger.LogInformation("Sent Telegram alert for failed time set for device {Name}", name);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // ignore cancellation during shutdown
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send Telegram failure alert for device {Name}", name);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // ignore cancellation
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SetDeviceTimeAsync failed for device {Name}", name);
                try
                {
                    if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                    {
                        var text = $"Set device time encountered error for {name} ({deviceIdToSend}): {ex.Message}";
                        await _telegramClient.SendTextMessageAsync(new ChatId(_telegramChatId), text, cancellationToken: ct);
                    }
                }
                catch
                {
                    // swallow any exceptions sending alert
                }
            }
        }
    }
}
