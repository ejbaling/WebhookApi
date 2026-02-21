using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace WebhookApi.Services
{
    public record TokenResult(string AccessToken, DateTimeOffset ExpiresAt);
    public record TokenWithRefresh(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken);

    public interface ITokenService
    {
        Task<TokenResult?> RefreshAsync(string refreshToken, string? clientId = null, string? clientSecret = null);
        Task<TokenResult?> GetClientCredentialsAsync();
        Task<TokenWithRefresh?> ExchangeAuthorizationCodeAsync(string code, string redirectUri);
    }

    public class TokenService : ITokenService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;

        public TokenService(IHttpClientFactory httpFactory, IConfiguration config)
        {
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        private string TokenUrl => "https://oauth2.googleapis.com/token";
        private string ClientId => _config["Auth:ClientId"] ?? string.Empty;
        private string ClientSecret => _config["Auth:ClientSecret"] ?? string.Empty;

        public async Task<TokenResult?> RefreshAsync(string refreshToken, string? clientId = null, string? clientSecret = null)
        {
            var client = _httpFactory.CreateClient();
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("grant_type","refresh_token"),
                new KeyValuePair<string,string>("refresh_token", refreshToken),
                new KeyValuePair<string,string>("client_id", clientId ?? ClientId),
                new KeyValuePair<string,string>("client_secret", clientSecret ?? ClientSecret)
            });

            var resp = await client.PostAsync(TokenUrl, content);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var at = root.GetProperty("access_token").GetString() ?? string.Empty;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            return new TokenResult(at, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }

        public async Task<TokenResult?> GetClientCredentialsAsync()
        {
            var client = _httpFactory.CreateClient();
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("grant_type","client_credentials"),
                new KeyValuePair<string,string>("client_id", ClientId),
                new KeyValuePair<string,string>("client_secret", ClientSecret)
            });

            var resp = await client.PostAsync(TokenUrl, content);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var at = root.GetProperty("access_token").GetString() ?? string.Empty;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            return new TokenResult(at, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }

        public async Task<TokenWithRefresh?> ExchangeAuthorizationCodeAsync(string code, string redirectUri)
        {
            var client = _httpFactory.CreateClient();
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("grant_type","authorization_code"),
                new KeyValuePair<string,string>("code", code),
                new KeyValuePair<string,string>("redirect_uri", redirectUri),
                new KeyValuePair<string,string>("client_id", ClientId),
                new KeyValuePair<string,string>("client_secret", ClientSecret)
            });

            var resp = await client.PostAsync(TokenUrl, content);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var at = root.GetProperty("access_token").GetString() ?? string.Empty;
            var rt = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            return new TokenWithRefresh(at, DateTimeOffset.UtcNow.AddSeconds(expiresIn), rt);
        }
    }
}
