# NT8 Direct Telegram Notifier

Telegram alerts, safety checks and a small remote-control surface for **NinjaTrader 8**, as a single-file AddOn. It talks straight to the Telegram Bot API over HTTPS: no NinjaTrader Share Services, no third-party servers, no extra DLLs.

> **Status: beta (v0.2.0).** It can flatten positions on command and, if you switch it on, automatically. Read [Limitations](#limitations) and the [Disclaimer](#disclaimer) before running it anywhere near live money. Not affiliated with or endorsed by NinjaTrader or Telegram.

**Tested with:** NinjaTrader **8.1.8.4**, a **Tradovate** connection (MFFU), Sim101 and MFFU evaluation accounts.

<!-- Add redacted screenshots here, e.g.: ![Fill alert](docs/images/fill-alert.png) -->

## What it does

**Alerts**
- **Fills:** a leader fill and its followers' fills arrive as one message, with each follower's price difference from the leader in ticks. Partial fills are merged.
- **Connection lost / disconnected / reconnected**, for orders and market data, merged per connection. A flapping connection is muted after a few drops.
- **NinjaTrader UI freeze**, **rejected orders**, and an **order-rate alert** (default: 30 new orders in 60 s).
- **Strategy enabled / disabled / removed**, read from NinjaTrader's own strategy list.
- **Stale market data:** an instrument you hold or trade gets no ticks while the data connection still says Connected.
- **Trade-copier check:** followers drift away from the leader's position. It compares account positions, so it does not depend on any copier's API.
- **Restart report:** on startup, how long NinjaTrader was down and whether the last exit was normal.
- **Re-entry after `/flatten`:** a position opens shortly after a flatten, so a strategy (or the copier) is trading again.
- **End-of-day summary** per account: trades, wins/losses, gross P&L, commissions, best/worst trade, open positions, and the copier's fill quality.

**Safety checks** (new in 0.2; each one has a switch)
- **Unprotected position:** an open position without a working stop order (off by default; only for strategies that place real stops).
- **Orphan exit orders:** an account is flat but stop/target-type orders are still working.
- **Stuck orders:** an order sits in Submitted / change pending / cancel pending too long.
- **Position limit:** more contracts open on an account than you allow.
- **Risk buffer:** how close each account's Net Liquidation is to a drawdown floor you set, with alerts as it approaches and optional automatic flatten for that account.
- **Machine health:** NinjaTrader's CPU, memory and free disk space.

**Commands** (accepted only from your Telegram user ID)

| Command | Does |
|---|---|
| `/status` | Connections, positions, P&L and risk buffer per account, copier status, strategies, data feeds |
| `/flatten [account]` | Closes positions and cancels working orders on the tracked accounts, or only on the account whose name contains the text you add (`/flatten 002`). Reports whether you are flat afterwards |
| `/cancel [account]` | Cancels working orders and leaves positions alone |
| `/floor [amount] [account]` | Shows or sets drawdown floors: `/floor 48250`, `/floor 002 48500`, `/floor 002 clear`, `/floor clear` |
| `/copy` | Follower fills compared with the leader today: price difference, dollars, lag |
| `/eod` | Preview the end-of-day report without resetting anything |
| `/diag` | Health: last Telegram send and poll, queue, clock offset, CPU, memory, disk, settings, recent log |
| `/simulate` | One sample of every alert type, built by the same code as the real ones |
| `/version`, `/help` | Version and command list |

**Also:** every alert is appended to a local log, every fill to a CSV file, and an optional external heartbeat (dead-man's switch) can tell you when NinjaTrader stops responding altogether.

Example messages:

```
BUY MNQ DEC26 · 10:42:07
Account1 (leader) · 3 @ 29411.00
Account2 · 3 @ 29410.00 · 4 ticks better
Account3 · 3 @ 29410.00 · 4 ticks better

CONNECTION LOST
MFFU · orders + data
10:42:07

RISK BUFFER
Account2
Net liq 48,730.10 · floor 48,000.00
730.10 above the floor (alert level 750.00)
```

## Requirements

- Windows with NinjaTrader 8 (tested on 8.1.8.4)
- A Telegram account and a bot token from [@BotFather](https://t.me/BotFather)
- Outbound HTTPS to `api.telegram.org`

## Install

1. **Telegram.** Message @BotFather, send `/newbot`, and copy the token. Open your new bot and press **Start**. Message @userinfobot to get your numeric user ID.
2. **Copy the code.** Put `src/DirectTelegramNotifierAddOn.cs` in `Documents\NinjaTrader 8\bin\Custom\AddOns\`.
3. **Compile.** In NinjaTrader open **New > NinjaScript Editor** and press **F5**.
4. **Configure.** The first compile creates `Documents\NinjaTrader 8\DirectTelegramNotifier.config` (or copy `config/DirectTelegramNotifier.config.example` there). Set `AuthorizedChatID`, `AccountName` (leader first), and the token (see below). Press **F5** again.
5. **Check.** You should get a "Notifier online" message in Telegram. Send `/status`, then `/simulate` to see every alert type.

**Token:** either put it after `BotToken=`, or leave that blank and set the environment variable `NT_TELEGRAM_BOT_TOKEN` (`setx NT_TELEGRAM_BOT_TOKEN "123456:ABC..."`, then fully restart NinjaTrader). The environment variable keeps it out of a plain-text file. If both are set, the config file wins.

The config is read at startup only. After editing it, press F5 in the NinjaScript Editor or restart NinjaTrader. Keys you leave out use their defaults, so an older config file keeps working after an upgrade.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `BotToken` | blank | Bot token. Blank = use `NT_TELEGRAM_BOT_TOKEN` |
| `AuthorizedChatID` | none | Your numeric Telegram user ID. Alerts go here; only this user can send commands |
| `AccountName` | `Sim101` | One account, a comma-separated list, or `*`. The first listed account is the copier leader |
| `EnableWatchdog` | `true` | Connection, UI-freeze, online/stopped and restart alerts |
| `EnableExecutions` | `true` | Fill alerts |
| `EnableRemoteCommands` | `true` | Poll Telegram for commands |
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
| `ProtectiveCheckSeconds` | `0` | Alert when a position has no working stop this long. `0` = off |
| `OrphanOrderSeconds` | `20` | Alert when a flat account still has exit-type orders working this long. `0` = off |
| `OrphanOrderNames` | `Stop,Target` | Order names containing any of these count as exit-type |
| `StuckOrderSeconds` | `30` | Alert when an order stays in a transitional state this long. `0` = off |
| `MaxPositionContracts` | `0` | Alert when an account holds more contracts than this in total. `0` = off |
| `RiskFloor` | blank | Net Liquidation at which an account fails. `RiskFloor:<account>=...` overrides it per account |
| `RiskBufferAlerts` | `1500,750,300` | Alert as Net Liquidation comes within these dollars of the floor |
| `RiskAutoFlatten` | `false` | `true` flattens an account that gets within `RiskFlattenBuffer` dollars of its floor |
| `RiskFlattenBuffer` | `150` | Dollar distance from the floor that triggers the auto-flatten |
| `CpuAlertPercent` | `90` | NinjaTrader CPU level that triggers an alert. `0` = off |
| `CpuAlertSeconds` | `180` | How long CPU must stay at that level (minimum 30) |
| `MemoryAlertMB` | `0` | NinjaTrader memory level that triggers an alert. `0` = off |
| `DiskAlertMB` | `2000` | Free space on the config folder's drive below which you are alerted. `0` = off |
| `FlattenConfirm` | `false` | `true` makes `/flatten` show Confirm / Cancel buttons first (they expire after 30 s) |
| `SilentRoutineAlerts` | `false` | `true` sends fills, reconnects, reports and command replies without a notification sound |
| `EnableFillCsv` | `true` | Append every fill and leader-vs-follower comparison to `DirectTelegramNotifier-fills.csv` |

## Risk buffer and `/floor`

Prop firms fail an account when its balance falls to a drawdown floor, and trailing floors move. The notifier watches each account's **Net Liquidation** and alerts as it approaches the floor you give it.

1. Set `RiskFloor=48000` (or send `/floor 48000`) to the value from **your firm's rules**. The notifier does not know your firm's rules or how its floor trails.
2. Update the floor when it moves: `/floor 48250` for every tracked account, or `/floor 002 48500` for one. These settings are saved and survive restarts.
3. `/status` shows each account's net liquidation, floor and buffer.
4. `RiskAutoFlatten=true` flattens an account that is within `RiskFlattenBuffer` dollars of its floor after two readings in a row, at most once every 10 minutes per account. It flattens only that account and does not disable strategies. **Test it on Sim first.**

A wrong floor gives false comfort or false alarms. Treat this as an extra warning, never as your only protection.

## Heartbeat (optional, recommended)

The add-on cannot tell you that NinjaTrader or the whole machine died, because it dies with it. An external dead-man's switch can: the add-on pings a URL every minute, and the service alerts you when the pings stop.

1. Create a free check at [Healthchecks.io](https://healthchecks.io): period 1 minute, grace 5 minutes.
2. Add a notification integration (Telegram, email or push).
3. Paste the check's ping URL into `HeartbeatUrl=`, then press F5.

Pings are skipped while NinjaTrader's UI is frozen, so a hung NinjaTrader also triggers the alert. A planned reboot longer than the grace time will alert too.

## Security

- Network traffic goes only to `api.telegram.org` and, if configured, your heartbeat URL.
- Commands run only for the numeric user ID in `AuthorizedChatID`; everything else is ignored. Button taps are checked the same way.
- Telegram messages queued before startup are discarded, and commands older than 120 s are ignored, so a stale `/flatten` never runs after a restart.
- The config file is plain text. Prefer the environment variable for the token, turn on two-step verification on your Telegram account, and in @BotFather run `/setjoingroups` and choose Disable.
- If a token leaks, revoke it in @BotFather (`/mybots`, API Token, Revoke) and update your config or environment variable. See [SECURITY.md](SECURITY.md).

## Limitations

- **It cannot report its own crash or power loss.** On the next start it reports the gap; for "never came back" use the heartbeat.
- **The "Notifier stopped" message when NinjaTrader closes is best effort** and may not arrive.
- **It does not enable or disable strategies.** NinjaTrader exposes no supported way to start or stop a strategy from an add-on. `/flatten`, `/cancel` and the auto-flatten do not disable strategies, so an enabled strategy can re-enter (the notifier alerts if it does).
- **Risk buffer:** it uses NinjaTrader's Net Liquidation and a floor you enter. It does not know your firm's rules, how its floor trails, or whether the firm uses another measure.
- **Stop check:** it sees real working stop orders only. Strategies that manage stops internally look unprotected, which is why it is off by default.
- **Orphan check:** it matches order names (`OrphanOrderNames`), not intent. Stuck-order and orphan checks can be noisy on some connections; set them to `0` if so.
- **Copier checks** assume followers trade the same instrument at a fixed ratio of the leader's size. Copy-quality figures need fills within about 2 seconds of each other to be paired.
- **Stale-data alerts are heuristic.** The `cme` schedule is hard-coded to US Eastern time and ignores holidays and early closes, so those can alert.
- **End-of-day P&L** comes from the fills this add-on has seen since its last report. NinjaTrader's execution records often show zero commission, so reports say "fees not in fills"; set `EstimatedCommissionPerSide` for an estimate. The broker-reported figure is printed too.
- One notifier per NinjaTrader process, one bot and chat. Commands and alerts depend on Telegram and your internet connection being up.

## Verified so far

Tested with NinjaTrader **8.1.8.4**, a **Tradovate** connection (MFFU), Sim101 and MFFU evaluation accounts. Rows marked "earlier build" were seen working in 0.1.x. Update this table to match what you have actually tested before you publish.

| Feature | Status |
|---|---|
| Compiles | ✅ 0.2.0 |
| Fill alerts, partial fills | ✅ earlier build |
| `/flatten` (leader and followers) | ✅ earlier build |
| Connection drop / restore alerts | ✅ earlier build |
| Restart report, online message | ✅ earlier build |
| `/status` (accounts, strategies, data feeds, copy check) | ✅ earlier build |
| Grouped fill alerts with leader comparison | ✅ |
| Long polling: commands answered immediately | ✅ |
| `/simulate`, `/diag`, `/copy`, `/eod` | ✅ |
| `FlattenConfirm` buttons | ✅ |
| Heartbeat ping | ✅ |
| Rejected-order and order-rate alerts | ✅ |
| Strategy enabled / disabled alerts | ✅ |
| Stale-data alerts | ✅ |
| Copier mismatch alerts | ✅ |
| Re-entry-after-flatten alert | ✅ |
| Fill CSV export | ✅ |
| `/flatten <account>`, `/cancel` | ✅ |
| `/floor` and the risk-buffer alerts | ✅ |
| Unprotected-position, orphan-order, stuck-order and position-limit alerts | ✅ |
| CPU, memory and disk alerts | ✅ |
| End-of-day summary | ✅ |

## Troubleshooting

| Symptom | Fix |
|---|---|
| Compile error mentioning `System.Net.Http` | In the NinjaScript Editor: right-click, **References > Add > Framework**, tick `System.Net.Http`, recompile |
| Output shows `Unauthorized Check BotToken` (401) | The token is wrong, revoked, or an old environment variable is overriding what you expect. F5 does not reload environment variables |
| `chat not found` (400) | Open your bot in Telegram and press **Start**, and check `AuthorizedChatID` is your user ID, not a group ID |
| `Ignored message from unauthorized Telegram user id N` | That is the ID Telegram sees for you: put N in `AuthorizedChatID` |
| Nothing happens, no output | The file must be in `bin\Custom\AddOns`, not `Indicators`. Check the NinjaScript Output window for `[DirectTelegramNotifier]` lines |
| Copier check does nothing | List several accounts in `AccountName`, leader first (or set `LeaderAccount`) |
| A check alerts too often | Set its seconds/percent key to `0` (see the table above), or tune it |
| `getUpdates failed ... 409` right after F5 | The old instance's poll is still ending. It clears in a few seconds. If it persists, another program uses the same bot token |

Telegram allows only one `getUpdates` consumer per bot token: give each program its own bot.

## Uninstall

Delete `DirectTelegramNotifierAddOn.cs` from `AddOns`, press F5, and delete `DirectTelegramNotifier.config`, `.log`, `.log.old`, `.heartbeat`, `.floors` and `-fills.csv` from `Documents\NinjaTrader 8`. Remove the `NT_TELEGRAM_BOT_TOKEN` environment variable and revoke the bot token if you no longer need it.

## Disclaimer

This software is provided as is, without warranty (see [LICENSE](LICENSE)). It is not trading advice. Automated trading and remote order control can lose money, and a bug or a missed alert could cost you. **Some prop firms and brokers restrict automation, trade copying or remote order management; checking the rules that apply to you is your responsibility.** Not affiliated with or endorsed by NinjaTrader, Telegram or any broker.

Parts of the code were written with AI assistance (Claude) and tested by the maintainer on the scenarios in the table above. The whole add-on is one readable file; read it before you run it.

## Contributing

Issues and pull requests are welcome. For bugs, include the add-on version, your NinjaTrader version and the Output window lines, with tokens, IDs, account numbers and heartbeat URLs redacted.

## License

MIT, see [LICENSE](LICENSE).
