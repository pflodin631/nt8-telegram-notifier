# NT8 Direct Telegram Notifier

Telegram alerts, risk enforcement and a small remote-control surface for **NinjaTrader 8**, as an AddOn. It talks straight to the Telegram Bot API over HTTPS: no NinjaTrader Share Services, no third-party servers, no extra DLLs. It works for personal accounts and prop-firm accounts alike: you describe your own risk rules in the config, and it has no firm presets.

> **Status: beta (v0.3.0).** It can flatten and lock accounts on command and, if you switch it on, automatically. The 0.3 risk features are unit-tested but **not yet verified on Sim**: see [Verified so far](#verified-so-far). Read [Limitations](#limitations) and the [Disclaimer](#disclaimer) before running it anywhere near live money. Not affiliated with or endorsed by NinjaTrader or Telegram.

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

**Safety checks** (each one has a switch)
- **Unprotected position:** an open position without a working stop order (off by default; only for strategies that place real stops).
- **Orphan exit orders:** an account is flat but stop/target-type orders are still working.
- **Stuck orders:** an order sits in Submitted / change pending / cancel pending too long.
- **Position limit:** more contracts open on an account than you allow.
- **Risk floor:** how close each account's Net Liquidation is to its drawdown floor, alerting as it approaches. The floor is entered by hand, or computed from your rule: static, end-of-day trailing or intraday trailing.
- **Daily loss limit:** in dollars or percent, alerting at 50/75/90% and at the limit.
- **Account lock:** optional auto-flatten at the floor or daily limit. The account then stays locked, and anything that opens on it is closed again until `/unlock` or the session ends.
- **Risk UNKNOWN:** you hold a position while market data is disconnected or stale, so the risk numbers are frozen.
- **Machine health:** NinjaTrader's CPU, memory and free disk space.
- **Config check:** misspelled or invalid settings are reported at startup instead of silently ignored.

**Commands** (accepted only from your Telegram user ID)

| Command | Does |
|---|---|
| `/status` | Connections, positions, P&L, risk and locks per account, copier status, strategies, data feeds |
| `/flatten [account]` | Closes positions and cancels working orders on the tracked accounts, or only on the account whose name contains the text you add (`/flatten 002`). Reports whether you are flat afterwards, and closes re-entries for `FlattenHoldSeconds` |
| `/cancel [account]` | Cancels working orders and leaves positions alone |
| `/lock [account]` | Flattens and keeps the account flat until `/unlock` or the session ends |
| `/unlock [account]` | Ends a lock |
| `/risk` | Mode, net liquidation, high-water mark, floor, buffer, daily loss and lock for each account. `/risk reset [account]` restarts a trailing floor |
| `/floor [account] [amount]` | Sets a floor by hand: `/floor 48250`, `/floor 002 48500`, `/floor 002 clear`, `/floor clear`. Suspicious values need a button press |
| `/leader [account]` | Shows or changes the copier leader (`/leader clear` returns to the config) |
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
2. **Copy the code.** Copy the whole `src/DirectTelegramNotifier` folder into `Documents\NinjaTrader 8\bin\Custom\AddOns\`, so you end up with `AddOns\DirectTelegramNotifier\DirectTelegramNotifier.cs` and its siblings.
3. **Compile.** In NinjaTrader open **New > NinjaScript Editor** and press **F5**.
4. **Configure.** The first compile creates `Documents\NinjaTrader 8\DirectTelegramNotifier.config` (or copy `config/DirectTelegramNotifier.config.example` there). Set `AuthorizedChatID`, `AccountName` (leader first), and the token (see below). Press **F5** again.
5. **Check.** You should get a "Notifier online" message in Telegram. Send `/status`, then `/simulate` to see every alert type.

**Upgrading from 0.2 or earlier:** first **delete `AddOns\DirectTelegramNotifierAddOn.cs`**, the old single file. If you keep it, NinjaTrader reports the class as defined twice. Your config, floors and logs carry over, and new settings use their defaults.

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
| `StaleDataSchedule` | `cme` | When silence is expected: `cme` (CME futures week, US Eastern), `always` (around the clock), or `custom` |
| `StaleDataQuietWindows` | blank | For `custom`: e.g. `Weekdays 00:00-09:30; Weekdays 16:00-24:00; Weekends 00:00-24:00`. Windows can't cross midnight |
| `StaleDataTimeZone` | `Eastern Standard Time` | Windows time zone for the custom windows |
| `StaleDataInstruments` | blank | Extra instruments to watch. Held and traded instruments are watched automatically |
| `EnableAlertLog` | `true` | Append alerts to `DirectTelegramNotifier.log` |
| `LeaderAccount` | blank | Copier leader. Blank = first account in `AccountName` |
| `FollowerRatio` | `1` | Expected follower size as a multiple of the leader's |
| `MismatchSeconds` | `20` | Alert when followers stay out of sync this long. `0` = off |
| `ProtectiveCheckSeconds` | `0` | Alert when a position has no working stop this long. `0` = off |
| `OrphanOrderSeconds` | `20` | Alert when a flat account still has exit-type orders working this long. `0` = off |
| `OrphanOrderNames` | `Stop,Target` | Order names containing any of these count as exit-type |
| `StuckOrderSeconds` | `30` | Alert when an order stays in one of `StuckOrderStates` this long. `0` = off |
| `StuckOrderStates` | `Submitted,ChangePending,ChangeSubmitted,CancelPending,CancelSubmitted` | Order states that should pass quickly |
| `MaxPositionContracts` | `0` | Alert when an account holds more contracts than this in total. `0` = off |
| `DrawdownMode` * | `manual` | `off`, `manual` (`RiskFloor` or `/floor`), `static`, `eod` or `intraday`. See [Risk](#risk-floors-daily-loss-and-locks) |
| `RiskFloor` * | blank | The floor for `manual` mode, in Net Liquidation dollars |
| `StartingBalance` * | blank | Account size. Needed for `static`; trailing modes start from it |
| `DrawdownAmount` * | blank | Maximum drawdown in dollars, for `static`, `eod` and `intraday` |
| `FloorLock` * | blank | A trailing floor stops rising at this value. Blank = never locks |
| `RiskBufferAlerts` | `1500,750,300` | Alert as Net Liquidation comes within these dollars of the floor |
| `RiskAutoFlatten` | `false` | `true` flattens **and locks** an account that stays within `RiskFlattenBuffer` of its floor for `RiskConfirmSeconds` |
| `RiskFlattenBuffer` | `150` | Dollar distance from the floor that triggers auto-flatten |
| `RiskConfirmSeconds` | `2` | How long a breach must last before auto-flatten acts (1-30) |
| `FloorJumpConfirm` | `1000` | `/floor` asks for confirmation when it moves a floor more than this. `0` = only when the floor is inside your alert levels |
| `DailyLossLimit` * | blank | Maximum loss per session: dollars (`1000`) or percent of the session-start value (`2%`) |
| `DailyLossAlerts` | `50,75,90` | Percentages of the limit that alert |
| `DailyLossAutoFlatten` | `false` | `true` flattens and locks an account at its daily loss limit |
| `CpuAlertPercent` | `90` | NinjaTrader CPU level that triggers an alert. `0` = off |
| `CpuAlertSeconds` | `180` | How long CPU must stay at that level (minimum 30) |
| `MemoryAlertMB` | `0` | NinjaTrader memory level that triggers an alert. `0` = off |
| `DiskAlertMB` | `2000` | Free space on the config folder's drive below which you are alerted. `0` = off |
| `FlattenConfirm` | `false` | `true` makes `/flatten` and `/lock` show Confirm / Cancel buttons first (they expire after 30 s) |
| `FlattenHoldSeconds` | `30` | After `/flatten`, anything that opens on those accounts this soon is closed again. `0` = off |
| `FillGroupMs` | `2000` | Fills of the same instrument and side within this window become one message (500-10000) |
| `SilentRoutineAlerts` | `false` | `true` sends fills, reconnects, reports and command replies without a notification sound |
| `EnableFillCsv` | `true` | Append every fill and leader-vs-follower comparison to `DirectTelegramNotifier-fills.csv` |

\* Can also be set for one account: `Key:AccountName=value`, for example `DrawdownAmount:Acct002=2500`. Unrecognized keys are reported in Telegram at startup.

## Risk: floors, daily loss and locks

The notifier watches each account's **Net Liquidation** every second. It does not know your broker's or firm's rules: you describe them, and you check `/risk` against the numbers your broker or firm shows. Theirs are the ones that count.

**1. Pick how the floor is found** (`DrawdownMode`, per account if your accounts differ):

| Mode | Floor | Typical use |
|---|---|---|
| `off` | none | No floor checks |
| `manual` | `RiskFloor`, or whatever you set with `/floor` | You keep it current yourself |
| `static` | `StartingBalance - DrawdownAmount`, never moves | Static drawdown |
| `eod` | highest **end-of-day** balance - `DrawdownAmount`, capped at `FloorLock`, never moves down | End-of-day trailing drawdown |
| `intraday` | highest **live** Net Liquidation (open profit included) - `DrawdownAmount`, capped at `FloorLock` | Intraday trailing drawdown |

Example, a 50k account with a 2,000 end-of-day trailing drawdown that stops trailing at 50,100:

```
DrawdownMode=eod
StartingBalance=50000
DrawdownAmount=2000
FloorLock=50100
```

The end of day is `EodReportTime` in `EodTimeZoneId`. If your firm closes its day at a different time, set those to match. High-water marks are saved in `DirectTelegramNotifier.state`, so a restart does not reset them. After the first session, compare the floor in `/risk` with your firm's dashboard; if it is off, fix the config and send `/risk reset`. A `/floor` value always overrides the mode until `/floor clear`.

**2. Optional daily loss limit:** `DailyLossLimit=1000` or `2%`, measured from Net Liquidation at the start of each session. It alerts at `DailyLossAlerts` (50/75/90%) and at the limit.

**3. Optional enforcement:** `RiskAutoFlatten=true` and/or `DailyLossAutoFlatten=true`. When an account stays in breach for `RiskConfirmSeconds`, it is flattened, its orders are cancelled and it is **locked**. While locked, any new position on it is closed again at once, and you get **LOCK ENFORCED**. The lock ends with `/unlock` or at the session boundary. **Strategies and copiers are not disabled** (NinjaTrader offers no supported way). If one keeps trading a locked account, each entry is closed at a small loss, so turn it off.

**Safeguards:**
- **Frozen numbers:** while you hold a position and the data connection is down or the instrument's feed is stale, the account shows **Risk UNKNOWN**, and automatic action pauses until data is back.
- **Floor typos:** `/floor` asks for a button press when the value is above net liquidation, inside your alert levels, or moves the floor more than `FloorJumpConfirm`.

A wrong rule gives false comfort or false alarms. **Test on Sim first** ([docs/TESTING.md](docs/TESTING.md)), and never treat this as your only protection.

## Heartbeat (optional, recommended)

The add-on cannot tell you that NinjaTrader or the whole machine died, because it dies with it. An external dead-man's switch can: the add-on pings a URL every minute, and the service alerts you when the pings stop.

1. Create a free check at [Healthchecks.io](https://healthchecks.io): period 1 minute, grace 5 minutes.
2. Add a notification integration (Telegram, email or push).
3. Paste the check's ping URL into `HeartbeatUrl=`, then press F5.

Pings are also held back when alerts can't reach you: while NinjaTrader's UI is frozen, Telegram is unreachable, the bot token is rejected, or alerts have been stuck in the queue for 3 minutes. The dead-man's switch therefore covers "my alerts stopped arriving", not just "NinjaTrader died". A planned reboot longer than the grace time will alert too.

## Security

- Network traffic goes only to `api.telegram.org` and, if configured, your heartbeat URL.
- Commands run only for the numeric user ID in `AuthorizedChatID`; everything else is ignored. Button taps are checked the same way.
- Telegram messages queued before startup are discarded, and commands older than 120 s are ignored, so a stale `/flatten` never runs after a restart.
- The config file is plain text. Prefer the environment variable for the token, turn on two-step verification on your Telegram account, and in @BotFather run `/setjoingroups` and choose Disable.
- If a token leaks, revoke it in @BotFather (`/mybots`, API Token, Revoke) and update your config or environment variable. See [SECURITY.md](SECURITY.md).

## Limitations

- **It cannot report its own crash or power loss.** On the next start it reports the gap; for "never came back" use the heartbeat.
- **The "Notifier stopped" message when NinjaTrader closes is best effort** and may not arrive.
- **It does not enable or disable strategies.** NinjaTrader exposes no supported way to start or stop a strategy from an add-on. A lock *reacts* to new positions within about a second; it cannot prevent the order. Each blocked entry costs a round trip.
- **Risk:** it uses NinjaTrader's Net Liquidation and the rule you enter. Firms differ in details it cannot know, such as what counts as the closing balance, intraday versus closing-only trailing, and fees. Treat `/risk` as an estimate to compare with your firm's numbers.
- **Daily loss:** measured from Net Liquidation at the session boundary. If tracking starts mid-session, `/risk` says so.
- **Stop check:** it sees real working stop orders only. Strategies that manage stops internally look unprotected, which is why it is off by default.
- **Orphan check:** it matches order names (`OrphanOrderNames`), not intent. Stuck-order and orphan checks can be noisy on some connections; set them to `0` if so.
- **Copier checks** assume followers trade the same instrument at a fixed ratio of the leader's size. Copy-quality figures need fills within about 2 seconds of each other to be paired.
- **Stale-data alerts are heuristic.** Schedules ignore holidays and early closes, so those can alert. Use `custom` windows for other markets.
- **End-of-day P&L** comes from the fills this add-on has seen since its last report. NinjaTrader's execution records often show zero commission, so reports say "fees not in fills"; set `EstimatedCommissionPerSide` for an estimate. The broker-reported figure is printed too.
- Amounts are shown without a currency symbol, as reported by NinjaTrader. Messages are in English.
- One notifier per NinjaTrader process, one bot and chat. Commands and alerts depend on Telegram and your internet connection being up.

## Verified so far

Tested with NinjaTrader **8.1.8.4**, a **Tradovate** connection (MFFU), Sim101 and MFFU evaluation accounts. Last updated for **0.3.0**.

- **Unit-tested:** the logic is covered by `tests/CoreTests.cs`. "beh." means the maintainer's behavioural tests against a stand-in NinjaTrader API, which cannot prove broker behaviour.
- **Sim / Live:** seen working inside NinjaTrader. "0.x" is the version it was seen in. A feature changed since then needs re-checking.

| Feature | Unit-tested | Sim | Live |
|---|---|---|---|
| Compiles (NinjaTrader 8.1.8.4) | C# 5 compile | ⬜ 0.3 | ⬜ |
| Fill alerts, partial fills, grouping | beh. | ✅ 0.1.x | ⬜ |
| `/flatten` (leader and followers) | beh. | ✅ 0.1.x | ⬜ |
| Hold after `/flatten` (copier flips) | beh. | ⬜ | ⬜ |
| Connection drop / restore alerts | — | ✅ 0.1.x | ⬜ |
| Restart report, online message | — | ✅ 0.1.x | ⬜ |
| `/status`, `/copy`, `/simulate` | beh. | ✅ 0.1.x | ⬜ |
| Heartbeat ping | — | ✅ 0.1.x | ⬜ |
| Heartbeat held when delivery fails | beh. | ⬜ | ⬜ |
| Priority lane and merging | beh. | ⬜ | ⬜ |
| `FlattenConfirm` buttons | beh. | ⬜ | ⬜ |
| Rejected-order and order-rate alerts | — | ⬜ | ⬜ |
| Strategy enabled / disabled alerts | — | ⬜ | ⬜ |
| Stale-data alerts, custom schedules | ✅ windows | ⬜ | ⬜ |
| Copier mismatch alerts, `/leader` | beh. | ⬜ | ⬜ |
| Unprotected, orphan, stuck, position-limit alerts | beh. | ⬜ | ⬜ |
| Floor alerts and `/floor` validation | ✅ | ⬜ | ⬜ |
| Drawdown modes (static, eod, intraday) | ✅ | ⬜ | ⬜ |
| Daily loss limit | ✅ | ⬜ | ⬜ |
| Auto-flatten and account lock | beh. | ⬜ | ⬜ |
| Risk UNKNOWN on stale data | beh. | ⬜ | ⬜ |
| Config warnings | ✅ | ⬜ | ⬜ |
| CPU, memory and disk alerts | beh. | ⬜ | ⬜ |
| End-of-day summary | beh. | ⬜ | ⬜ |

## Troubleshooting

| Symptom | Fix |
|---|---|
| Compile error mentioning `System.Net.Http` | In the NinjaScript Editor: right-click, **References > Add > Framework**, tick `System.Net.Http`, recompile |
| Output shows `Unauthorized Check BotToken` (401) | The token is wrong, revoked, or an old environment variable is overriding what you expect. F5 does not reload environment variables |
| `chat not found` (400) | Open your bot in Telegram and press **Start**, and check `AuthorizedChatID` is your user ID, not a group ID |
| `Ignored message from unauthorized Telegram user id N` | That is the ID Telegram sees for you: put N in `AuthorizedChatID` |
| Nothing happens, no output | The folder must be in `bin\Custom\AddOns`, not `Indicators`. Check the NinjaScript Output window for `[DirectTelegramNotifier]` lines |
| Compile error: type or member defined twice (CS0101 / CS0111) | The old `DirectTelegramNotifierAddOn.cs` is still in `AddOns`. Delete it |
| `CONFIG WARNING` in Telegram | A key is misspelled or a value is invalid. The message names it and suggests the correct key |
| A follower keeps getting `LOCK ENFORCED` | The copier keeps re-syncing it. Disable that follower in the copier, or `/unlock` it |
| Copier check does nothing | List several accounts in `AccountName`, leader first (or set `LeaderAccount`) |
| A check alerts too often | Set its seconds/percent key to `0` (see the table above), or tune it |
| `getUpdates failed ... 409` right after F5 | The old instance's poll is still ending. It clears in a few seconds. If it persists, another program uses the same bot token |

Telegram allows only one `getUpdates` consumer per bot token: give each program its own bot.

## Uninstall

Delete the `AddOns\DirectTelegramNotifier` folder, press F5, and delete `DirectTelegramNotifier.config`, `.log`, `.log.old`, `.heartbeat`, `.floors`, `.state` and `-fills.csv` from `Documents\NinjaTrader 8`. Remove the `NT_TELEGRAM_BOT_TOKEN` environment variable and revoke the bot token if you no longer need it.

## Disclaimer

This software is provided as is, without warranty (see [LICENSE](LICENSE)). It is not trading advice. Automated trading and remote order control can lose money, and a bug or a missed alert could cost you. **Some prop firms and brokers restrict automation, trade copying or remote order management; checking the rules that apply to you is your responsibility.** Not affiliated with or endorsed by NinjaTrader, Telegram or any broker.

Parts of the code were written with AI assistance (Claude) and tested as the table above describes. The source is plain C# split into a few readable files; read it before you run it. To run the unit tests: `tests\run-tests.ps1`.

## Contributing

Issues and pull requests are welcome. For bugs, include the add-on version, your NinjaTrader version and the Output window lines, with tokens, IDs, account numbers and heartbeat URLs redacted.

## License

MIT, see [LICENSE](LICENSE).
