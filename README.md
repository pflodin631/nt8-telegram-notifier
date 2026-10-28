# NT8 Direct Telegram Notifier

Telegram alerts, risk enforcement and a small remote-control surface for **NinjaTrader 8**, as an AddOn. It talks straight to the Telegram Bot API over HTTPS: no NinjaTrader Share Services, no relay or server of mine, no extra DLLs. Telegram itself carries your alerts, including account names and dollar amounts. If you opt in, a short alert without amounts also goes to a push service you choose (see [Second alert channel](#second-alert-channel-optional)), and optionally a heartbeat URL. It works for personal accounts and prop-firm accounts alike: you describe your own risk rules in the config, and it has no firm presets (the example config file below is a worked example, not a preset).

> **Status: beta (v0.5.1).** It can flatten and lock accounts on command and, if you switch it on, automatically. The 0.3 to 0.5 risk features are unit-tested but **not yet verified on Sim**: see [Verified so far](#verified-so-far). Read [Limitations](#limitations) and the [Disclaimer](#disclaimer) before running it anywhere near live money. Not affiliated with or endorsed by NinjaTrader or Telegram.

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
- **Consistency rule:** tracks how much more profit today can add before today is more than your firm's percentage of your total profit.
- **Inactivity clock:** warns as an account approaches the number of days your firm allows without a trade, and tells you when a trade resets it.
- **Account lock:** optional auto-flatten at the floor or daily limit. The account then stays locked, and anything that opens on it is closed again until `/unlock` or the session ends.
- **Risk UNKNOWN:** you hold a position while the account's data connection is down or its feed is stale, so the risk numbers are frozen. Repeated every minute, with an optional automatic flatten.
- **Machine health:** NinjaTrader's CPU, memory and free disk space.
- **Config check:** misspelled or invalid settings are reported at startup instead of silently ignored.
- **Self-test:** at startup and on `/selftest`, whether it can read net liquidation and open P&L for each account, which data check it uses, and whether auto-flatten is **armed** or **not armed** (and why). `/status` shows the same in one line.
- **Flatten follow-up:** after `/flatten`, an automatic flatten or `/lock`, it keeps checking until the positions are really gone and says **FLAT CONFIRMED** or **STILL EXPOSED**. A STILL EXPOSED is **repeated every minute**, on both channels, until the account is flat or unlocked.
- **Open P&L check:** warns when open P&L reads exactly 0.00 while you hold a position and net liquidation keeps moving, which would mean your connection is not reporting it.
- **Second alert channel (optional):** the critical alerts also go to a push service you choose, so Telegram is not a single point of failure.
- **Risk log:** every reading the risk check sees is recorded, so a real session can be replayed through the same logic.

**Commands** (accepted only from your Telegram user ID)

| Command | Does |
|---|---|
| `/status` | Connections, positions, P&L, risk and locks per account, copier status, strategies, data feeds |
| `/flatten [account]` | Closes positions and cancels working orders on the tracked accounts, or only on the account whose name contains the text you add (`/flatten 002`). Keeps checking until you are flat (up to 15 s) and reports how long it took, or **STILL EXPOSED**. Closes re-entries for `FlattenHoldSeconds` |
| `/cancel [account]` | Cancels working orders and leaves positions alone |
| `/lock [account]` | Flattens and keeps the account flat until `/unlock` or the session ends |
| `/unlock [account]` | Ends a lock (asks to confirm when `FlattenConfirm=true`) |
| `/selftest` | Whether it can read what it needs for each account, which data check is in use, and whether auto-flatten is armed |
| `/testbackup` | Sends a test through the backup alert channel and says whether it worked |
| `/risk` | Mode, net liquidation, high-water mark, floor, buffer, daily loss, consistency and lock for each account |
| `/risk seed ACCOUNT 51000` | Sets a trailing high-water mark to what your firm shows (button to confirm). `/risk seed ACCOUNT bestday 800` sets the best day for the consistency rule. `/risk reset [account]` restarts a trailing floor |
| `/activity [account days]` | Last trade and days left on the inactivity rule. `/activity 002 3` records that the last trade was 3 days ago |
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
4. **Configure.** The first compile creates `Documents\NinjaTrader 8\DirectTelegramNotifier.config` (or copy `config/DirectTelegramNotifier.config.example` there). Set `AuthorizedChatID`, `AccountName` (leader first), and the token (see below). Press **F5** again. *
5. **Check.** You should get a "Notifier online" message in Telegram. Send `/status`, then `/simulate` to see every alert type.

\*For a filled-in example, see config/DirectTelegramNotifier.mffu-rapid-eod-50k.config.example: three prop-firm evaluation accounts, with the reasons for each change.

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
| `HighWaterBasis` * | `balance` | What the end-of-day high-water mark is measured on: `balance` (net liquidation minus open profit) or `netliq`. Intraday trailing always uses net liquidation |
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
| `UnknownFlattenSeconds` | `0` | Flatten and lock an account whose risk data has been unavailable this long while it holds a position. `0` = only alert |
| `LockGroup` | `false` | `true` locks every tracked account when one is locked, so a copier cannot keep re-opening trades on a locked follower |
| `DailyLossLimit` * | blank | Maximum loss per session: dollars (`1000`) or percent of the session-start value (`2%`) |
| `DailyLossAlerts` | `50,75,90` | Percentages of the limit that alert |
| `DailyLossAutoFlatten` | `false` | `true` flattens and locks an account at its daily loss limit |
| `ConsistencyPercent` * | blank | Your firm's best-day limit as a percent of total profit (for example `30`, `40`, `50`). Needs `StartingBalance`. Blank or `0` = off |
| `ConsistencyProfitTarget` * | blank | **Leave blank** unless your firm measures against the larger of total profit and the profit target: setting it raises the allowed daily profit to at least the percentage of the target |
| `ConsistencyAlerts` | `75,90` | Percentages of today's allowed profit that alert |
| `ConsistencyAutoFlatten` | `false` | `true` flattens and locks an account when today's profit reaches the cap |
| `InactivityDays` * | blank | Days without a trade your firm allows. Blank or `0` = off. Per account: `InactivityDays:Acct001=7` |
| `InactivityAlerts` | `3,2,1` | Alert when this many days (or fewer) are left |
| `BackupAlertUrl` | blank | A second channel for critical alerts: the alert is POSTed as plain text to this URL (for example `https://ntfy.sh/your-long-random-topic`). Treat the URL like a password. Check with `/testbackup` |
| `BackupAlertDetail` | `minimal` | `minimal` = a short message with the kind of trouble and the last 3 characters of the account name, no amounts. `full` = the whole alert, only for a service you run yourself |
| `BackupAlertFormat` | `text` | `text` (request body is the alert) or `json` (`{"text":...,"content":...,"message":...}`, for Slack and Discord incoming webhooks) |
| `EnableRiskLog` | `true` | Record every risk reading to `DirectTelegramNotifier-risk.csv` for replay (about 7 MB a day per account while the market moves; capped at 50 MB) |
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

### Worked example
`config/DirectTelegramNotifier.mffu-rapid-eod-50k.config.example` shows one real setup: three 50k end-of-day
evaluation accounts with limits also set in the broker. It is an example, not a preset. Firm rules change,
so check your own plan. Placeholders mark what you must replace.

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

The end of day is `EodReportTime` in `EodTimeZoneId`. If your firm closes its day at a different time, set those to match.

**What the high-water mark is measured on.** For `eod`, `HighWaterBasis=balance` (the default) uses the **closing balance**: net liquidation minus open profit. Open profit would otherwise raise your floor above the firm's if you hold a position at the close. The floor is still compared with live net liquidation, because firms count open losses against it. If your firm measures on net liquidation, set `HighWaterBasis=netliq`. Sources disagree about firms' exact definitions, so **compare `/risk` with your firm's dashboard after the first session**.

**Accounts that already have profit.** A trailing mark starts at `StartingBalance`, which is too low if you have banked profit before installing. The notifier tells you when it starts below the account's balance. Send `/risk seed ACCOUNT 51000` with the highest closing balance your firm has counted; it shows the resulting floor and asks you to confirm. If the session ends while NinjaTrader could not see the account and the balance changed, the mark is not guessed: you are told, and `/risk seed` fixes it. High-water marks are saved in `DirectTelegramNotifier.state`. A `/floor` value always overrides the mode until `/floor clear`.

**2. Optional daily loss limit:** `DailyLossLimit=1000` or `2%`, measured from Net Liquidation at the start of each session. It alerts at `DailyLossAlerts` (50/75/90%) and at the limit.

**3. Optional enforcement:** `RiskAutoFlatten=true` and/or `DailyLossAutoFlatten=true`. When an account stays in breach for `RiskConfirmSeconds`, it is flattened, its orders are cancelled and it is **locked**. While locked, any new position on it is closed again at once, and you get **LOCK ENFORCED**. The lock ends with `/unlock` or at the session boundary. **Strategies and copiers are not disabled** (NinjaTrader offers no supported way). If one keeps trading a locked account, each entry is closed at a small loss, so turn it off.

**Safeguards:**
- **Frozen numbers:** while you hold a position and **that account's** data connection is down or the instrument's feed is stale, the account shows **Risk UNKNOWN**. Floor, daily-loss and consistency checks pause for it, and you are reminded every minute. Another connection dropping does not pause it. Pausing is not always what you want: flattening orders do not need market data, so `UnknownFlattenSeconds=120` flattens and locks the account if the data stays gone that long. It is off by default because a short data blip would then cost you the position.
- **Floor typos:** `/floor` asks for a button press when the value is above net liquidation, inside your alert levels, or moves the floor more than `FloorJumpConfirm`.

A wrong rule gives false comfort or false alarms. **Test on Sim first** ([docs/TESTING.md](docs/TESTING.md)), and never treat this as your only protection.

## Account rules: consistency and inactivity

Prop firms add rules beyond the drawdown. The notifier tracks two of them from numbers you enter. It does not know your firm's rules, they differ between plans and change over time, and **sources I found disagree** (for one firm the consistency limit appears as 30%, 40% and 50% depending on the plan and the page). Enter the values from your own account's rules.

**Consistency rule.** "No single day may be more than X% of total profit." Set `ConsistencyPercent` and `StartingBalance`. With profit banked before today, the most today can add is `X% / (100% - X%)` of that profit: for a 30% rule and 1,000 banked, 428.57. `/risk` shows today's profit against that cap and your best day's share of total profit, and you are alerted at 75% and 90% of the cap. The cap only limits **today**: if an earlier day is already over the limit, `/risk` also shows how much more total profit brings it back (best day ÷ the percentage − total profit), to be earned on smaller days. `ConsistencyAutoFlatten=true` flattens and locks the account when it reaches the cap. Notes:
- Today's profit includes open profit, so it warns early rather than late.
- Early in an evaluation the cap is tiny and the alert is noise. Enable it per account when you near the target. Leave `ConsistencyProfitTarget` blank unless your firm really measures against the profit target; set otherwise it overstates your room.
- The best day is learned as sessions end. For an account with history, send `/risk seed ACCOUNT bestday 800`.

**Inactivity rule.** "No trade for N days closes the account." Set `InactivityDays=7`, or per account (`InactivityDays:Acct001=7`), because some firms apply it only to some account types. Any fill counts as a trade. You are warned with 3, 2 and 1 days left (`InactivityAlerts`), told when the limit passes, and told when a trade resets the clock. `/activity` shows every account's last trade and days left. The time is counted in 24-hour days from your last fill. A firm that counts **calendar days** can end the window sooner than that, so treat the last alert day as your deadline: with a 7-day rule, plan to trade by day 5. If NinjaTrader has no earlier trade on record, the clock starts when the notifier first sees the account and is marked *assumed*: correct it with `/activity 002 3`. **The notifier never places trades.** A planned break is usually something to tell your firm about in advance.

## Self-test: is auto-flatten armed?

`/selftest` reads, for each tracked account: net liquidation, open P&L, which data check Risk UNKNOWN uses (the account's own connection, or any connection), the rules in force (floor, daily loss, consistency, inactivity) and whether each automatic flatten you switched on has what it needs. It runs by itself once, 20 seconds after startup, and **loudly** when you have switched auto-flatten on and it cannot act. `/status` carries a one-line summary.

**NOT ARMED** means the risk check has nothing to act on, for a reason it names: net liquidation unreadable, a trailing mode without `DrawdownAmount`, `RiskAutoFlatten` with no floor, `DailyLossAutoFlatten` with no limit, `ConsistencyPercent` without `StartingBalance`, or open P&L unreadable on the closing-balance basis. It reports; it does not gate. The risk check already does nothing without those inputs, so "not armed" is simply the truth made visible, and a gate would only be one more way to switch protection off silently.

**Open P&L.** The closing balance is net liquidation minus open P&L. A flat account legitimately reads 0, so the self-test cannot prove the read works. What it can catch is the impossible case: you hold a position, net liquidation keeps moving (several changes and a range of at least 5.00 over 30 seconds), and open P&L stays exactly 0.00. Then you get **OPEN P&L LOOKS WRONG** (Telegram only), `/selftest` repeats it, and `/selftest` also hints whenever it sees exactly 0.00 on a held position. It is a heuristic: a fee can set it off, and a connection that reports a wrong but non-zero figure is not caught. After your first close with a position held, compare the result with your firm's number.

**What it does not prove:** that a flatten will succeed. That needs the Sim checklist.

## Second alert channel (optional)

Everything above reaches you through Telegram. If Telegram is down, or the bot is blocked, you find out only through the heartbeat, and only if its alert does not go through Telegram. `BackupAlertUrl` adds a second path for the alerts that matter most, so a floor breach is not lost with Telegram:

- the first **Risk UNKNOWN** for an account (not the reminders)
- the **tightest** floor-buffer level, and anything below the floor
- the **daily loss limit** reached
- an account **locked** or a lock **enforced**
- an account **still exposed** after a flatten
- **Telegram alerts are not getting through** (and when they are again), which Telegram cannot report about itself

Routine alerts, fills and reports never use it. The same text is not sent twice within a minute, and a failed post is retried once.

**What it sends, and why so little.** The push service is a third party, and ntfy.sh is a public one. By default (`BackupAlertDetail=minimal`) the message says only which kind of trouble and which account by its last three characters, with no balances, floors, positions or reasons, for example:

```
BELOW FLOOR: account ...001. Open NinjaTrader now.
STILL EXPOSED (2m 0s): account ...001 did not go flat. Close it by hand now.
```

The full details stay in Telegram. Even this tells whoever can read the topic that an account is in trouble, so keep the topic secret. `BackupAlertDetail=full` sends the whole alert, with account names and dollar amounts: use it only with a service you run yourself.

**Setting it up with [ntfy](https://ntfy.sh)** (free, no account): choose a long random topic name, install the ntfy phone app and subscribe to it, then set `BackupAlertUrl=https://ntfy.sh/your-long-random-topic` and send `/testbackup`. ntfy's own documentation says the topic is effectively a password, so make it unguessable and do not share it. The notifier sends `Title` and `Priority: urgent` headers, which ntfy understands and other services ignore. For Slack or Discord incoming webhooks set `BackupAlertFormat=json`. It is expected to work with them but I have only confirmed ntfy's request format from its documentation: use `/testbackup`. The URL is redacted from the Output window and logs.

It is a one-way alert channel, not a remote control: nothing can be sent to the notifier through it.

## Heartbeat (optional, recommended)

The add-on cannot tell you that NinjaTrader or the whole machine died, because it dies with it. An external dead-man's switch can: the add-on pings a URL every minute, and the service alerts you when the pings stop.

1. Create a free check at [Healthchecks.io](https://healthchecks.io): period 1 minute, grace 5 minutes.
2. Add a notification integration (Telegram, email or push).
3. Paste the check's ping URL into `HeartbeatUrl=`, then press F5.

Pings are also held back when alerts can't reach you: while NinjaTrader's UI is frozen, Telegram is unreachable, **Telegram refuses the notifier's messages** (bot blocked, wrong chat, revoked token), or alerts have been stuck in the queue for 3 minutes. While messages are refused, a silent test message is tried every 5 minutes, so the heartbeat resumes by itself once you fix it. The dead-man's switch therefore covers "my alerts stopped arriving", not just "NinjaTrader died". A planned reboot longer than the grace time will alert too.

**Send the Healthchecks alert somewhere other than Telegram**: email, SMS or a push app. The case this detects is Telegram itself being broken, and an alert routed through Telegram would not reach you.

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
- **Daily loss and consistency:** measured from the values at the session boundary. If tracking starts mid-session, `/risk` says so.
- **The closing-balance basis assumes NinjaTrader reports open P&L.** The balance is net liquidation minus open P&L. If your connection reported open P&L as 0 instead of failing, the "balance" would silently equal net liquidation. `/risk` shows the open P&L it used (`Net liq ... · open P&L ...`): after your first close with a position held, compare it and the resulting floor with your firm's number.
- **The data check is per account, but approximate.** NinjaTrader does not say which connection serves an instrument's data, so the notifier uses the account's own connection plus the stale-feed check. If your build does not expose an account's connection, it falls back to "any data connection down". Which of the two is in use is written to the NinjaScript Output window for each account, and shown in `/diag`. A lock or flatten still needs a working order connection.
- **A flatten is not re-sent by the follow-up.** If a flatten does not complete you are told (STILL EXPOSED, through both channels if set up) and reminded every minute: for a locked account until it is flat or unlocked (capped at 12 hours), after a `/flatten` for up to 10 minutes. A second flatten while the first is still working could reverse the position, so the follow-up only watches. A locked account does re-send when no close is working, from the lock check.
- **Replay is faithful to within a second.** The live check ticks about once a second with some jitter; a replay ticks exactly once a second between recorded readings, so timers can differ by up to a second.
- **The behavioural tests use a stand-in for NinjaTrader's API.** If the real API differs from what it assumes, only the first compile in NinjaTrader (F5) will show it.
- **Inactivity** counts any fill on a tracked account from NinjaTrader's execution list, counted in 24-hour days. It cannot see trades made outside NinjaTrader, and your firm's definition of "a trade" may be narrower.
- **Stop check:** it sees real working stop orders only. Strategies that manage stops internally look unprotected, which is why it is off by default.
- **Orphan check:** it matches order names (`OrphanOrderNames`), not intent. Stuck-order and orphan checks can be noisy on some connections; set them to `0` if so.
- **Copier checks** assume followers trade the same instrument at a fixed ratio of the leader's size. Copy-quality figures need fills within about 2 seconds of each other to be paired.
- **Stale-data alerts are heuristic.** Schedules ignore holidays and early closes, so those can alert. Use `custom` windows for other markets.
- **End-of-day P&L** comes from the fills this add-on has seen since its last report. NinjaTrader's execution records often show zero commission, so reports say "fees not in fills"; set `EstimatedCommissionPerSide` for an estimate. The broker-reported figure is printed too.
- Amounts are shown without a currency symbol, as reported by NinjaTrader. Messages are in English.
- One notifier per NinjaTrader process, one bot and chat. Commands and alerts depend on Telegram and your internet connection being up.

## Verified so far

Tested with NinjaTrader **8.1.8.4**, a **Tradovate** connection (MFFU), Sim101 and MFFU evaluation accounts. Last updated for **0.5.1**.

- **Unit-tested:** the pure logic, including the risk state machine (session rollover, restarts, data outages, floors, daily loss, consistency), is covered by `tests/CoreTests.cs`. "beh." means the behavioural tests in `tests/behavioral`, which compile the whole add-on against a stand-in NinjaTrader API and drive it through its real entry points: anyone can run them (`bash tests/run-behavioral.sh`, needs Mono). They cannot prove how NinjaTrader or a broker behaves. `bash tests/run-replay.sh` replays recorded risk logs (`tests/replay/cases`) through the same logic: a session you record on Sim can become a permanent test.
- **Sim / Live:** seen working inside NinjaTrader. "0.x" is the version it was seen in. A feature changed since then needs re-checking.

| Feature | Unit-tested | Sim / Live |
|---|---|---|
| Compiles (NinjaTrader 8.1.8.4) | C# 5 compile | ✅ 0.5 |
| Fill alerts, partial fills, grouping | beh. | ✅ 0.1.x |
| `/flatten` (leader and followers) | beh. | ✅ 0.1.x |
| Hold after `/flatten` (copier flips) | beh. | ✅ |
| Connection drop / restore alerts | — | ✅ 0.1.x |
| Restart report, online message | — | ✅ 0.1.x |
| `/status`, `/copy`, `/simulate` | beh. | ✅ 0.1.x |
| Heartbeat ping | — | ✅ 0.1.x |
| Heartbeat held when delivery fails (including refused sends) | beh. | ⬜ |
| Priority lane and merging | beh. | ✅ |
| `FlattenConfirm` buttons | beh. | ✅ |
| Rejected-order and order-rate alerts | — | ✅ |
| Strategy enabled / disabled alerts | — | ✅ |
| Stale-data alerts, custom schedules | ✅ windows | ⬜ |
| Copier mismatch alerts, `/leader` | beh. | ✅ |
| Unprotected, orphan, stuck, position-limit alerts | beh. | ✅ |
| Floor alerts and `/floor` validation | ✅ | ✅ |
| Drawdown modes (static, eod, intraday) | ✅ | ✅ |
| Daily loss limit | ✅ | ✅ |
| Auto-flatten and account lock | ✅ decision logic, beh. | ✅ |
| Risk UNKNOWN on stale data or this account's connection down, reminders, optional flatten | ✅ state machine, beh. | ✅ |
| End-of-day trailing on closing balance, `/risk seed`, uncertain-closing warning | ✅ state machine, beh. | ✅ |
| Consistency rule cap, cure line and alerts | ✅ state machine, beh. | ✅ |
| Inactivity clock and `/activity` | ✅ formulas, beh. | ✅ |
| Several confirm buttons pending at once, `LockGroup` | beh. | ⬜ |
| Self-test and the armed / not armed line | ✅ checks, beh. | ✅ |
| Flatten follow-up (FLAT CONFIRMED / STILL EXPOSED) | beh. | ✅ |
| Second alert channel (`/testbackup`, routing, Telegram-down alert) | ✅ routing, beh. | ⬜ |
| Risk log and replay (live = replay) | ✅ replay, beh. | ⬜ |
| `/unlock` confirmation, command audit log | beh. | ✅ |
| Config warnings (including new keys) | ✅ | ⬜ |
| CPU, memory and disk alerts | beh. | ✅ |
| End-of-day summary | beh. | ✅ |

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

Delete the `AddOns\DirectTelegramNotifier` folder, press F5, and delete `DirectTelegramNotifier.config`, `.log`, `.log.old`, `.heartbeat`, `.floors`, `.state`, `-risk.csv` (and `.old`) and `-fills.csv` from `Documents\NinjaTrader 8`. Remove the `NT_TELEGRAM_BOT_TOKEN` environment variable and revoke the bot token if you no longer need it.

## Disclaimer

This software is provided as is, without warranty (see [LICENSE](LICENSE)). It is not trading advice. Automated trading and remote order control can lose money, and a bug or a missed alert could cost you. **Some prop firms and brokers restrict automation, trade copying or remote order management; checking the rules that apply to you is your responsibility.** Not affiliated with or endorsed by NinjaTrader, Telegram or any broker.

Parts of the code were written with AI assistance (Claude) and tested as the table above describes. The source is plain C# split into a few readable files; read it before you run it. To run the unit tests: `tests\run-tests.ps1`. To run the behavioural tests: `bash tests/run-behavioral.sh`, and to replay recorded sessions: `bash tests/run-replay.sh` (both need Mono). `tests\run-behavioral.ps1` and `tests\replay\run-replay.ps1` are the Windows equivalents and have not been run on Windows yet.

## Contributing

Issues and pull requests are welcome. For bugs, include the add-on version, your NinjaTrader version and the Output window lines, with tokens, IDs, account numbers and heartbeat URLs redacted.

## License

MIT, see [LICENSE](LICENSE).
