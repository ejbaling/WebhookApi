﻿using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using System.IdentityModel.Tokens.Jwt;

namespace GmailTokenGenerator
{
    public class Program
    {
        private const string RedirectUri = "http://127.0.0.1:5000/authorize/";
        private const string Scope = "https://www.googleapis.com/auth/gmail.readonly";

        public static async Task Main(string[] args)
        {
            // # In VS Code terminal, initialize user secrets
            // dotnet user-secrets init
            // dotnet user-secrets set "GoogleAuth:ClientId" "your-gmail-client-id"
            // dotnet user-secrets set "GoogleAuth:ClientSecret" "your-gmail-client-secret"
            // dotnet user-secrets set "GoogleAuth:TopicName" "your-gmail-topic-name"

            var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

            string clientId = configuration["GoogleAuth:ClientId"]
                ?? throw new Exception("Client ID not found in user secrets");

            string clientSecret = configuration["GoogleAuth:ClientSecret"]
                ?? throw new Exception("Client secret not found in user secrets");

            string authUrl = $"https://accounts.google.com/o/oauth2/v2/auth" +
                            $"?response_type=code&client_id={clientId}" +
                            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                            $"&scope={Uri.EscapeDataString(Scope)}" +
                            $"&access_type=offline&prompt=consent";

            // Start listener first
            using var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri);

            try
            {
                listener.Start();
                Console.WriteLine($"Listening on {RedirectUri}");

                // Then open browser
                Console.WriteLine("Opening browser...");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);

                var context = await listener.GetContextAsync();
                string code = context.Request.QueryString["code"] ?? throw new Exception("Authorization code missing");
                await SendResponse(context.Response, "Authorization received. You may close this tab.");
                listener.Stop();

                Console.WriteLine("Authorization code received.");

                using var httpClient = new HttpClient();
                var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "code", code },
                    { "client_id", clientId },
                    { "client_secret", clientSecret },
                    { "redirect_uri", RedirectUri },
                    { "grant_type", "authorization_code" }
                });

                var tokenResponse = await httpClient.PostAsync("https://oauth2.googleapis.com/token", tokenRequest);
                var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
                var tokenData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tokenJson);

                if (tokenData == null || !tokenData.ContainsKey("access_token"))
                {
                    Console.WriteLine("Failed to retrieve access token.");
                    return;
                }

                var accessToken = tokenData["access_token"].GetString();
                var refreshToken = tokenData.TryGetValue("refresh_token", out var rt) ? rt.GetString() : "(none)";

                Console.WriteLine($"Access Token: {accessToken}");
                Console.WriteLine($"Refresh Token: {refreshToken}");

                var expiresIn = tokenData["expires_in"].GetInt32();
                var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                Console.WriteLine($"Token expires at: {expiresAt.ToLocalTime()}");
                Console.WriteLine($"Token valid for: {expiresIn / 60.0:F1} minutes");

                // Create credential from access token
                var credential = GoogleCredential.FromAccessToken(accessToken);

                // Initialize Gmail service with the credential
                var gmailService = new GmailService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Gmail Watch App"
                });

                // Create watch request
                string topicName = configuration["GoogleAuth:TopicName"]
                ?? throw new Exception("Topic name not found in user secrets");
                var watchRequest = new WatchRequest
                {
                    TopicName = topicName,
                    LabelIds = new[] { "INBOX" },
                    LabelFilterAction = "include"
                };

                try
                {
                    try
                    {
                        // Stop watching the mailbox
                        await gmailService.Users.Stop("me").ExecuteAsync();
                        Console.WriteLine("\nStopped watching mailbox successfully.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\nFailed to stop watching mailbox: {ex.Message}");
                        Console.WriteLine($"Stack trace: {ex.StackTrace}");
                    }

                    // Start watching the mailbox
                    var watchResponse = await gmailService.Users.Watch(watchRequest, "me").ExecuteAsync();

                    Console.WriteLine("Gmail watch setup successful!");
                    Console.WriteLine($"History ID: {watchResponse.HistoryId}");
                    // Convert Unix timestamp to human-readable date
                    if (watchResponse.Expiration.HasValue)
                    {
                        var expirationDate = DateTimeOffset.FromUnixTimeMilliseconds((long)watchResponse.Expiration.Value)
                            .LocalDateTime;
                        Console.WriteLine($"Expiration: {expirationDate:f}"); // "f" format gives "Tuesday, April 10, 2024 9:15 PM"
                    }
                    else
                    {
                        Console.WriteLine("No expiration time provided by Gmail API");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to setup Gmail watch: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            }
            catch (HttpListenerException ex)
            {
                Console.WriteLine($"Failed to start listener: {ex.Message}");
                Console.WriteLine("Make sure no other application is using the port.");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return;
            }
        }

        private static async Task SendResponse(HttpListenerResponse response, string message)
        {
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes($"<html><body><h2>{message}</h2></body></html>");
            response.ContentLength64 = buffer.Length;
            response.ContentType = "text/html";
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }
    }
}