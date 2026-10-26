# NT8 Direct Telegram Notifier / Share Service

Telegram alerts and a small remote-control surface for **NinjaTrader 8**, as a single-file AddOn. It talks straight to the Telegram Bot API over HTTPS: no NinjaTrader Share Services, no third-party servers, no extra DLLs.

> **Status: beta (v0.1.0).** It can flatten positions on command, so read [Limitations](#limitations) and the [Disclaimer](#disclaimer) before running it anywhere near live money. Not affiliated with or endorsed by NinjaTrader or Telegram.

## What it does

**Alerts**
- **Fills:** side, quantity, instrument, price, account. Partial fills of one order are merged into one message.
- **Connection lost / disconnected / reconnected**, for orders and market data, merged per connection. A flapping connection is muted after a few drops.
- **NinjaTrader UI freeze.**
- **Rejected orders**, and an **order-rate alert** (default: 30 new orders in 60 s).
- **Strategy enabled / disabled / removed**, read from NinjaTrader's own strategy list.
- **Stale market data:** an instrument you hold or trade gets no ticks while the data connection still says Connected.
- **Trade-copier check:** followers drift away from the leader's position. It compares account positions, so it does not depend on any copier's API.
- **Restart report:** on startup, how long NinjaTrader was down and whether the last exit was normal.
- **End-of-day summary:** trades, wins/losses, gross/net P&L, commissions, best/worst trade, open positions.

**Commands** (accepted only from your Telegram user ID)

| Command | Does |
|---|---|
| `/status` | Connections, positions and P&L per account, copier status, strategies, data feeds |
| `/flatten` | Closes positions and cancels working orders on the tracked accounts, then reports whether you are flat |
| `/help` | Lists the commands |

**Also:** every alert is appended to a local log file, and an optional external heartbeat (dead-man's switch) can tell you when NinjaTrader stops responding altogether.

Example messages:

```
🟢 BUY MNQ DEC26 · 10:42:07
MFFUEVREOD…001 (leader) · 3 @ 29411.00
MFFUEVREOD…002 · 3 @ 29410.00 · 4 ticks better
MFFUEVREOD…003 · 3 @ 29410.00 · 4 ticks better

CONNECTION LOST
MyConnection · orders + data
14:32:07

COPY MISMATCH
MNQ 12-26 · leader Account1 Long 2
• Account2: Flat, expected Long 2
Out of sync for 25s. Check the copier and the follower connections.
```

## Requirements

- Windows with NinjaTrader 8 (tested on: **8.1.8.4**)
- A Telegram account and a bot token from [@BotFather](https://t.me/BotFather)
- Outbound HTTPS to `api.telegram.org`

## Install

1. **Telegram.** Message @BotFather, send `/newbot`, and copy the token. Open your new bot and press **Start**. Message @userinfobot to get your numeric user ID.
2. **Copy the code.** Put `src/DirectTelegramNotifierAddOn.cs` in `Documents\NinjaTrader 8\bin\Custom\AddOns\`.
3. **Compile.** In NinjaTrader open **New > NinjaScript Editor** and press **F5**.
4. **Configure.** The first compile creates `Documents\NinjaTrader 8\DirectTelegramNotifier.config` (or copy `config/DirectTelegramNotifier.config.example` there). Set `AuthorizedChatID`, `AccountName`, and the token (see below). Press **F5** again.
5. **Check.** You should get a "Notifier online" message in Telegram. Send `/status`.

**Token:** either put it after `BotToken=`, or leave that blank and set the environment variable `NT_TELEGRAM_BOT_TOKEN` (`setx NT_TELEGRAM_BOT_TOKEN "123456:ABC..."`, then fully restart NinjaTrader). The environment variable keeps it out of a plain-text file. If both are set, the config file wins.

The config is read at startup only. After editing it, press F5 in the NinjaScript Editor or restart NinjaTrader.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `BotToken` | blank | Bot token. Blank = use `NT_TELEGRAM_BOT_TOKEN` |
| `AuthorizedChatID` | none | Your numeric Telegram user ID. Alerts go here; only this user can send commands |
| `AccountName` | `Sim101` | One account, a comma-separated list, or `*`. The first listed account is the copier leader |
| `EnableWatchdog` | `true` | Connection, UI-freeze, online/stopped and restart alerts |
| `EnableExecutions` | `true` | Fill alerts |
| `EnableRemoteCommands` | `true` | Poll Telegram for `/status`, `/flatten`, `/help` |
| `EnableOrderRejectAlerts` | `true` | Rejected-order alerts |
| `RunawayMaxOrders` | `30` | Alert at this many new orders within `RunawayWindowSec`. `0` = off |
| `RunawayWindowSec` | `60` | Window for the order-rate alert |
| `UseGlobalFlattenEverything` | `false` | `true` makes `/flatten` hit every connected account, not only the tracked ones |
| `EodReportTime` | `170000` | End-of-day report time, HHMMSS |
| `EodTimeZoneId` | `Eastern Standard Time` | Windows time zone id for the report and session boundary |
| `EstimatedCommissionPerSide` | `0` | Used only when NinjaTrader reports zero commission |
| `HeartbeatUrl` | blank | Optional dead-man's-switch ping URL. Treat it like a secret |
| `HeartbeatIntervalSec` | `60` | Ping interval (minimum 15) |
| `RestartGapThresholdSec` | `45` | Report downtime longer than this at startup |
| `EnableStrategyWatch` | `true` | Strategy enabled/disabled/removed alerts |
| `StaleDataSeconds` | `120` | No-tick threshold for stale-data alerts. `0` = off, minimum otherwise 30 |
| `StaleDataSchedule` | `cme` | `cme` ignores the 17:00-18:00 ET break and the weekend; `always` never ignores |
| `StaleDataInstruments` | blank | Extra instruments to watch. Held and traded instruments are watched automatically |
| `EnableAlertLog` | `true` | Append alerts to `DirectTelegramNotifier.log` |
| `LeaderAccount` | blank | Copier leader. Blank = first account in `AccountName` |
| `FollowerRatio` | `1` | Expected follower size as a multiple of the leader's |
| `MismatchSeconds` | `20` | Alert when followers stay out of sync this long. `0` = off |

## Heartbeat (optional, recommended)

The add-on cannot tell you that NinjaTrader or the whole machine died, because it dies with it. An external dead-man's switch can: the add-on pings a URL every minute, and the service alerts you when the pings stop.

1. Create a free check at [Healthchecks.io](https://healthchecks.io): period 1 minute, grace 5 minutes.
2. Add a notification integration (Telegram, email or push).
3. Paste the check's ping URL into `HeartbeatUrl=`, then press F5.

Pings are skipped while NinjaTrader's UI is frozen, so a hung NinjaTrader also triggers the alert. A planned reboot longer than the grace time will alert too.

## Security

- Network traffic goes only to `api.telegram.org` and, if configured, your heartbeat URL.
- Commands run only for the numeric user ID in `AuthorizedChatID`; everything else is ignored.
- Telegram messages queued before startup are discarded, and commands older than 120 s are ignored, so a stale `/flatten` never runs after a restart.
- The config file is plain text. Prefer the environment variable for the token, turn on two-step verification on your Telegram account, and in @BotFather run `/setjoingroups` and choose Disable.
- If a token leaks, revoke it in @BotFather (`/mybots`, API Token, Revoke) and update your config or environment variable. See [SECURITY.md](SECURITY.md).

## Limitations

- **It cannot report its own crash or power loss.** On the next start it reports the gap; for "never came back" use the heartbeat.
- **The "Notifier stopped" message when NinjaTrader closes is best effort** and may not arrive.
- **It does not enable or disable strategies, and cannot place orders.** NinjaTrader exposes no supported way to start a strategy from an add-on. `/flatten` does not disable strategies, so an enabled strategy can re-enter.
- **Copier check** assumes followers trade the same instrument at a fixed ratio of the leader's size.
- **Stale-data alerts are heuristic.** The `cme` schedule is hard-coded to US Eastern time and ignores holidays and early closes, so those can alert.
- **End-of-day P&L** comes from the fills this add-on has seen since its last report. Positions carried in from before it started are not included, so compare with the broker's number, which is printed too.
- One notifier per NinjaTrader process, one bot and chat.
- Commands and alerts depend on Telegram and your internet connection being up.

## Verified so far

| Feature | Status |
|---|---|
| Compiles | ✅ |
| Fill alerts, partial fills | ✅ earlier build |
| `/status`, `/flatten` | ✅ earlier build |
| Connection drop / restore alerts | ✅ earlier build |
| Heartbeat ping | ✅ |
| Restart report | ✅ |
| Rejected-order and order-rate alerts | ✅ |
| Strategy enabled / disabled alerts | ✅ |
| Stale-data alerts | ⬜ |
| Copier mismatch alerts | ⬜ |
| End-of-day summary | ⬜ |

Tested with NinjaTrader **8.1.8.4**, **Tradovate / MFFU**, **Sim**.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Compile error mentioning `System.Net.Http` | In the NinjaScript Editor: right-click, **References > Add > Framework**, tick `System.Net.Http`, recompile |
| Output shows `Unauthorized Check BotToken` (401) | The token is wrong, revoked, or an old environment variable is overriding what you expect. Remember F5 does not reload environment variables |
| `chat not found` (400) | Open your bot in Telegram and press **Start**, and check `AuthorizedChatID` is your user ID, not a group ID |
| `Ignored message from unauthorized Telegram user id N` | That is the ID Telegram sees for you: put N in `AuthorizedChatID` |
| Nothing happens, no output | The file must be in `bin\Custom\AddOns`, not `Indicators`. Check the NinjaScript Output window for `[DirectTelegramNotifier]` lines |
| Copier check does nothing | List several accounts in `AccountName`, leader first (or set `LeaderAccount`) |

Telegram allows only one `getUpdates` consumer per bot token. If another program uses the same bot, you will see a 409 error: give each program its own bot.

## Uninstall

Delete `DirectTelegramNotifierAddOn.cs` from `AddOns`, press F5, and delete `DirectTelegramNotifier.config`, `.log`, `.log.old`, `.heartbeat` from `Documents\NinjaTrader 8`. Remove the `NT_TELEGRAM_BOT_TOKEN` environment variable and revoke the bot token if you no longer need it.

## Disclaimer

This software is provided as is, without warranty (see [LICENSE](LICENSE)). It is not trading advice. Automated trading and remote order control can lose money, and a bug or a missed alert could cost you. **Some prop firms and brokers restrict automation, trade copying or remote order management; checking the rules that apply to you is your responsibility.** Not affiliated with or endorsed by NinjaTrader, Telegram or any broker.

Parts of the code were written with AI assistance (Claude) and tested by the maintainer on the scenarios in the table above. The whole add-on is one readable file; read it before you run it.

## Contributing

Issues and pull requests are welcome. For bugs, include the add-on version, your NinjaTrader version and the Output window lines, with tokens, IDs, account numbers and heartbeat URLs redacted.

## License

MIT, see [LICENSE](LICENSE).
