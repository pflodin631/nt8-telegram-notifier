# Security

## Reporting a vulnerability

Please use GitHub's **private vulnerability reporting** (the repository's **Security** tab, then **Report a vulnerability**) instead of a public issue. Include the version, what you found and how to reproduce it.

**Never post your bot token, Telegram user ID, heartbeat URL or account numbers** in an issue, pull request or screenshot. If you did, revoke the token in @BotFather (`/mybots`, then API Token, then Revoke) and replace the heartbeat URL.

## Design notes

- The add-on only talks to `api.telegram.org` and, if you configure one, your heartbeat URL.
- Commands are accepted only from the numeric user ID in `AuthorizedChatID`. Everyone else is ignored.
- Telegram messages queued before startup are discarded and commands older than 120 seconds are ignored, so a stale `/flatten` never runs after a restart.
- The config file is plain text. Prefer the `NT_TELEGRAM_BOT_TOKEN` environment variable for the token, and turn on two-step verification on your Telegram account: whoever controls it can send `/flatten`.
