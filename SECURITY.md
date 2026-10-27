# Security

## Reporting a vulnerability

Please use GitHub's **private vulnerability reporting** (the repository's **Security** tab, then **Report a vulnerability**) instead of a public issue. Include the version, what you found and how to reproduce it.

**Never post your bot token, Telegram user ID, heartbeat URL or account numbers** in an issue, pull request or screenshot. If you did, revoke the token in @BotFather (`/mybots`, then API Token, then Revoke) and replace the heartbeat URL.

## Design notes

- The add-on only talks to `api.telegram.org` and, if you configure them, your heartbeat URL and your backup alert URL.
- Commands are accepted only from the numeric user ID in `AuthorizedChatID`. Everyone else is ignored.
- Telegram messages queued before startup are discarded and commands older than 120 seconds are ignored, so a stale `/flatten` never runs after a restart.
- The config file is plain text. Prefer the `NT_TELEGRAM_BOT_TOKEN` environment variable for the token, and turn on two-step verification on your Telegram account: whoever controls it can send `/flatten`.

## Secrets and data on disk (0.5)

- **`BackupAlertUrl` is a secret.** With ntfy the topic name is effectively the password: anyone who knows the URL can read and send to it. Use a long random topic, do not paste the URL into issues or screenshots, and treat a leaked URL like a leaked token (change the topic). It is redacted from the NinjaScript Output window and the log.
- **The risk log (`DirectTelegramNotifier-risk.csv`) holds account names and balances.** It stays on your machine and is in `.gitignore`. Scrub it before attaching any part of it to an issue; set `EnableRiskLog=false` if you do not want it written.
- **What the backup channel receives.** By default a short message (the kind of trouble and the last three characters of the account name, no amounts) goes to the push service you chose. Even that tells anyone who can read the topic that an account is in trouble. `BackupAlertDetail=full` sends the complete alert, with account names and balances: only use it with a service you run yourself.
- **Telegram carries the full alerts**: account names, balances, floors, positions. That is inherent in using Telegram. The add-on has no relay or server of its own.
- **The backup channel is outbound only.** Nothing sent to it can control the notifier.
