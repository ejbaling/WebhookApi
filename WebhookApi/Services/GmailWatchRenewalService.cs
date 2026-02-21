using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Amazon;
using Amazon.Runtime.CredentialManagement;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace WebhookApi.Services
{
    public class GmailWatchRenewalService : BackgroundService
    {
        private readonly ILogger<GmailWatchRenewalService> _logger;
        private readonly IConfiguration _config;
        private readonly ITokenService _tokenService;
        private TelegramBotClient? _telegramClient;
        private string? _telegramChatId;

        public GmailWatchRenewalService(ILogger<GmailWatchRenewalService> logger, IConfiguration config, ITokenService tokenService)
        {
            _logger = logger;
            _config = config;
            _tokenService = tokenService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Default to once per 6 days if not configured
            var intervalMinutes = _config.GetValue<int?>("GmailWatchRenewalService:RenewIntervalMinutes") ?? 6 * 24 * 60;
            var delay = TimeSpan.FromMinutes(intervalMinutes);

            _logger.LogInformation("GmailWatchRenewalService starting, interval={Minutes}m", intervalMinutes);

            // Initialize Telegram client if configured (mirror TailscaleMonitorService behaviour)
            var botToken = _config["Telegram:AiBotToken"]?.Trim();
            _telegramChatId = _config["Telegram:AiChatId"]?.Trim();
            if (!string.IsNullOrEmpty(botToken) && !string.IsNullOrEmpty(_telegramChatId))
            {
                try
                {
                    _telegramClient = new TelegramBotClient(botToken);
                    var me = await _telegramClient.GetMeAsync(stoppingToken);
                    _logger.LogInformation("GmailWatchRenewal: Telegram bot validated: {Username} ({Id})", me.Username, me.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GmailWatchRenewal: Telegram bot could not be initialized; continuing without alerts.");
                    _telegramClient = null;
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RenewWatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error renewing Gmail watch");
                }

                await Task.Delay(delay, stoppingToken);
            }

            _logger.LogInformation("GmailWatchRenewalService stopping");
        }

        private async Task RenewWatchAsync(CancellationToken cancellationToken)
        {
            if (!bool.TryParse(_config["GmailWatchRenewalService:Enabled"], out var isEnabled) || !isEnabled)
            {
                _logger.LogDebug("GmailWatchRenewalService disabled; skipping watch renewal.");
                return;
            }

            // Try Parameter Store first (default name /gmail/GoogleAuth). Can override via env SSM_PARAMETER_NAME.
            string ssmParamName = Environment.GetEnvironmentVariable("SSM_PARAMETER_NAME") ?? "/gmail/GoogleAuth";

            string? sClientId = null;
            string? sClientSecret = null;
            string? sTopicName = null;
            string? sRefreshToken = null;

            try
            {
                var profileName = Environment.GetEnvironmentVariable("AWS_PROFILE_NAME")
                                  ?? Environment.GetEnvironmentVariable("AWS_PROFILE")
                                  ?? "config-manager";

                AmazonSimpleSystemsManagementClient ssm;
                var chain = new CredentialProfileStoreChain();
                if (chain.TryGetAWSCredentials(profileName, out var awsCreds))
                {
                    ssm = new AmazonSimpleSystemsManagementClient(awsCreds, RegionEndpoint.APSoutheast2);
                    _logger.LogInformation("Using AWS profile '{Profile}' for SSM", profileName);
                }
                else
                {
                    ssm = new AmazonSimpleSystemsManagementClient();
                    _logger.LogInformation("Using default AWS credentials for SSM");
                }

                var resp = await ssm.GetParameterAsync(new GetParameterRequest { Name = ssmParamName, WithDecryption = true }, cancellationToken);
                var secretJson = resp.Parameter?.Value;
                if (!string.IsNullOrWhiteSpace(secretJson))
                {
                    using var doc = JsonDocument.Parse(secretJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("ClientId", out var cid)) sClientId = cid.GetString();
                    if (root.TryGetProperty("ClientSecret", out var csec)) sClientSecret = csec.GetString();
                    if (root.TryGetProperty("TopicName", out var t)) sTopicName = t.GetString();
                    if (root.TryGetProperty("RefreshToken", out var rt)) sRefreshToken = rt.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SSM lookup failed (parameter={Param})", ssmParamName);
            }

            var clientId = sClientId ?? _config["Google:ClientId"];
            var clientSecret = sClientSecret ?? _config["Google:ClientSecret"];
            var topicName = sTopicName ?? _config["Google:TopicName"] ?? _config["GoogleAuth:TopicName"];
            var refreshToken = sRefreshToken ?? _config["Google:RefreshToken"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(topicName))
            {
                _logger.LogWarning("Missing Google credentials or topic name; cannot renew Gmail watch.");
                return;
            }

            // Use TokenService to exchange refresh token for an access token (no browser needed)
            var refreshed = await _tokenService.RefreshAsync(refreshToken, clientId, clientSecret);
            if (refreshed == null)
            {
                _logger.LogWarning("Failed to refresh access token via TokenService");
                return;
            }

            var accessToken = refreshed.AccessToken;
            var credential = GoogleCredential.FromAccessToken(accessToken);

            var gmailService = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = _config["Google:ApplicationName"] ?? "Gmail Watch App"
            });

            var watchRequest = new WatchRequest
            {
                TopicName = topicName,
                LabelIds = new[] { "INBOX" },
                LabelFilterAction = "include"
            };

            try
            {
                _logger.LogInformation("Renewing Gmail watch for topic {Topic}", topicName);
                // Stop existing watch (best-effort)
                try
                {
                    await gmailService.Users.Stop("me").ExecuteAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ignore error stopping existing watch");
                }

                var resp = await gmailService.Users.Watch(watchRequest, "me").ExecuteAsync(cancellationToken);
                if (resp != null)
                {
                    if (resp.Expiration.HasValue)
                    {
                        try
                        {
                            var expirationMillis = (long)resp.Expiration.Value;
                            var expirationDate = DateTimeOffset.FromUnixTimeMilliseconds(expirationMillis).UtcDateTime;
                            var timeLeft = expirationDate - DateTime.UtcNow;
                            _logger.LogInformation("Gmail watch renewed. HistoryId={HistoryId} Expiration={Expiration} UTC (in {Minutes} minutes)", resp.HistoryId, expirationDate.ToString("u"), (int)timeLeft.TotalMinutes);
                            if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                            {
                                _ = SendRenewalSuccessAsync(topicName, expirationDate, resp.HistoryId, cancellationToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogInformation(ex, "Gmail watch renewed but failed to parse Expiration (raw={Expiration}). HistoryId={HistoryId}", resp.Expiration, resp.HistoryId);
                            if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                            {
                                _ = SendRenewalSuccessAsync(topicName, null, resp.HistoryId, cancellationToken);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Gmail watch renewed. HistoryId={HistoryId} Expiration not provided by Gmail API", resp.HistoryId);
                        if (_telegramClient is not null && !string.IsNullOrEmpty(_telegramChatId))
                        {
                            _ = SendRenewalSuccessAsync(topicName, null, resp.HistoryId, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Gmail watch");
            }
        }

        private async Task SendRenewalSuccessAsync(string topic, DateTime? expirationUtc, ulong? historyId, CancellationToken ct)
        {
            try
            {
                if (_telegramClient is null || string.IsNullOrEmpty(_telegramChatId))
                    return;

                string text;
                if (expirationUtc.HasValue)
                {
                    var mins = (int)Math.Max(0, (expirationUtc.Value - DateTime.UtcNow).TotalMinutes);
                    text = $"✅ Gmail watch renewed for topic {topic}. Expiration: {expirationUtc.Value:yyyy-MM-dd HH:mm:ss} UTC (in {mins} minutes). HistoryId: {historyId}";
                }
                else
                {
                    text = $"✅ Gmail watch renewed for topic {topic}. Expiration: not available. HistoryId: {historyId}.";
                }

                await _telegramClient.SendTextMessageAsync(new ChatId(_telegramChatId), text, cancellationToken: ct);
                _logger.LogInformation("Sent Telegram notification for Gmail watch renewal (topic={Topic})", topic);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram notification for Gmail watch renewal (topic={Topic})", topic);
            }
        }
    }
}
