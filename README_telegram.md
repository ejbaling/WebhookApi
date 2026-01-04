# Telegram webhook (step-by-step)

This document shows how to register and test Telegram webhooks for this project's
`POST /telegram/webhook` endpoint.

## 1 — Expose the app over HTTPS

- Ensure the app is reachable over HTTPS (deployed to a public HTTPS host with a valid TLS certificate). Configure any required firewall/NAT, load balancer, or port-forwarding so Telegram can reach `https://<your-host>/telegram/webhook`.

## 2 — Choose the bot token

- Tokens live in `WebhookApi/appsettings.Development.json` or `WebhookApi/appsettings.Production.json` under `Telegram:AdminBotToken`.

## 3 — Register the webhook with Telegram

Windows (recommended — use the real `curl` binary):

```cmd
curl.exe -X POST "https://api.telegram.org/bot<YOUR_TOKEN>/setWebhook" -d "url=https://your-public-host/telegram/webhook"
# optional: add secret_token
# curl.exe -X POST "https://api.telegram.org/bot<YOUR_TOKEN>/setWebhook" -d "url=https://your-public-host/telegram/webhook" -d "secret_token=mysecret"
```

Telegram returns JSON showing success or an error message.

## 4 — Configure `AllowedUserId` (optional)

- If `Telegram:AllowedUserId` is not `0`, the app will only process updates from that numeric Telegram user id. Set it to `0` to accept updates from any sender during testing.

## 5 — Test the webhook

-- Real test: message the bot from your Telegram client; the app should receive a POSTed update and log it.

- Example: assess an Airbnb guest named "Roderick" by sending the text `Assess Roderick` to the bot.

## 6 — Notes & troubleshooting

- Telegram requires a valid HTTPS endpoint.
- On Windows, use the system `curl` binary `curl.exe`. Quoting rules differ between shells: in `cmd.exe` escape inner double-quotes with backslashes, and in Unix-style shells use single quotes for JSON bodies.
- If you provided `secret_token` when calling `setWebhook`, Telegram will include the `X-Telegram-Bot-Api-Secret-Token` header in webhook requests — verify it in your logs if you enabled it.
