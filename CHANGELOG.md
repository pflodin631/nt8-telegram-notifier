# Changelog

## 0.5.1 (2026-10-27)

Fixes from the review of 0.5.0. No new features beyond what the fixes need. **Still not verified on Sim.** From here, a new version should contain only fixes found on Sim.

**Upgrading from 0.5.0:** copy the folder over. One new optional key, `BackupAlertDetail`.

- **The backup channel no longer sends the full alert to a third party.** It carried account names, net liquidation and floors to a public service such as ntfy.sh, which the README's "no third-party servers" did not admit. By default it now sends a short message: the kind of trouble and the last three characters of the account name, no amounts, positions or reasons (`BELOW FLOOR: account ...001. Open NinjaTrader now.`). `BackupAlertDetail=full` restores the whole alert, for a service you run yourself. The README's opening claim, the second-channel section and SECURITY.md now say exactly what goes where.
- **STILL EXPOSED repeats.** A single alert after 15 s was easy to miss. It now repeats every minute, on both channels, with the elapsed time, until the account is flat (a quiet FLAT CONFIRMED) or unlocked (a quiet note): up to 12 hours for a locked account, up to 10 minutes after a `/flatten`. It only watches; it sends nothing to the broker.
- **Open P&L check.** The self-test cannot prove the open P&L read works (a flat account legitimately reads 0). New: if you hold a position, net liquidation keeps moving and open P&L stays exactly 0.00 for 30 s, you get **OPEN P&L LOOKS WRONG**, and `/selftest` says so and hints whenever it sees exactly 0.00 on a held position. A heuristic: a fee can set it off.

Testing
- 416 unit tests (was 382): masking, the minimal texts (no digits), the open-P&L watch and its boundaries. A separate 31-mutant sweep of the new functions found two real gaps, now pinned.
- Behavioural suite 06 grows to 88 checks: nothing but the minimal text reaches the backup channel, repeats and their endings, the watch end to end, `BackupAlertDetail`. The timing-based tests were run repeatedly to check they are stable.

## 0.5.0 (2026-10-27)

A third external review, answered in three parts: make the safety code observable (self-test, flatten follow-up, risk log and replay), close a single point of failure (a second alert channel), and test harder (a mutation sweep). Still **not verified on Sim**: see `docs/TESTING.md`. The version is 0.5.0 because there are new commands and config keys.

**Upgrading from 0.4:** copy the `DirectTelegramNotifier` folder over the old one. A new file, `DirectTelegramNotifier.Assurance.cs`, is added. New keys (`BackupAlertUrl`, `BackupAlertFormat`, `EnableRiskLog`) have safe defaults: the backup channel is off, the risk log is on.

Delivery
- **Second alert channel (optional, off by default):** `BackupAlertUrl` POSTs the critical alerts to a push service (ntfy works as is; Slack and Discord webhooks with `BackupAlertFormat=json`). What is critical: the first Risk UNKNOWN, the tightest floor level and below, the daily limit, a lock and a lock being enforced, STILL EXPOSED, and **"Telegram alerts are not getting through"** (and when they are again). Deduplicated for a minute, retried once, URL redacted from the Output window and logs. `/testbackup` checks it.
- A refused send is recognized more broadly: 401, 403, 404 and the permanent kinds of 400 (chat not found, bot blocked, deactivated user, no rights, not a member, chat upgraded), not three cases. Message-specific errors (too long, bad markup) still are not.

Safety
- **Flatten follow-up:** `/flatten` no longer judges after a fixed 2.5 s: it checks until the positions are gone (up to 15 s) and reports how long it took, or **STILL EXPOSED** with what to do. An automatic flatten or `/lock` now follows up too: **FLAT CONFIRMED** (quiet) or **STILL EXPOSED** (loud, and to the backup channel). Before, the highest-stakes path never reported its outcome. The follow-up only watches: a second flatten while the first is working could reverse the position (the lock still re-sends one when no close is working).
- **Self-test:** `/selftest`, a report 20 s after startup, and a line in `/status`: can it read net liquidation and open P&L for each account, which data check is in use, what rules apply, and is auto-flatten **ARMED** or **NOT ARMED** (a trailing mode without `DrawdownAmount`, `RiskAutoFlatten` with no floor, unreadable net liquidation, and so on). Loud when auto-flatten is on and cannot act. It reports and does not gate: the risk check already does nothing without those inputs.
- `/unlock` asks for a button press when `FlattenConfirm=true`, like `/lock`.
- **Audit log:** every command, every button press (and expired or unknown ones) and every message from another Telegram user is written to `DirectTelegramNotifier.log`.

Testing
- **Risk log and replay:** with `EnableRiskLog` (default on) every reading the risk check sees is written to `DirectTelegramNotifier-risk.csv` (rules when they change, readings when they change and at least every 30 s; capped at 50 MB). `bash tests/run-replay.sh` feeds a log back through the same risk logic, with three starter cases (a weekend, an overnight restart, a data outage recorded by the add-on itself). A bug found on Sim becomes a permanent test: see `docs/TESTING.md`. A behavioural test checks that live and replay agree.
- **382 unit tests** (was 277) and a **mutation sweep**: every comparison, boolean operator, sign and `Math.Max`/`Math.Min` in the risk functions mutated one at a time (273 mutants): 212 caught, 39 invalid, 22 survived. The survivors were classified by hand: 19 equivalent, 3 real gaps in the new critical-event routing, closed. Earlier rounds of the sweep found real gaps that are now pinned: `DrawdownAmount=0`, a limit of 0, exact-boundary timers (2.000 s, 10 minutes, 1.00 tolerance), "a locked account is not flattened again" for the daily and consistency rules, and "a trailing floor never moves down" when the rules change.
- New behavioural suite `06-v05-delivery-and-readiness` (66 checks). All six suites and the replay cases run from the repo.
- Replay output lines carry the date, so a weekend log is unambiguous.

## 0.4.0 (2026-10-27)

Fixes from a second external review, plus two rule trackers. **Not yet verified on Sim**: see `docs/TESTING.md`.

**Upgrading from 0.3:** copy the `DirectTelegramNotifier` folder over the old one. A new file, `DirectTelegramNotifier.Rules.cs`, is added. Your config and state carry over; new keys use their defaults. (From 0.2 or earlier, delete `DirectTelegramNotifierAddOn.cs` first.)

Delivery
- **The external heartbeat no longer vouches for a dead alert path.** A refused send (bot blocked, chat not found, bad token) is now recorded; while it is newer than the last successful send, the ping is held even if polling and `getMe` still work. A silent test message is tried every 5 minutes, so the heartbeat resumes once Telegram accepts one. `/diag` shows it. The README now says to route the Healthchecks alert to email or SMS, not Telegram.

Risk
- **Risk UNKNOWN is scoped to the account's own data connection** (read from the account when NinjaTrader exposes it; otherwise the old "any data connection" check). Another connection dropping no longer pauses every account.
- **UNKNOWN repeats every minute** with how long it has lasted, instead of alerting once and going quiet.
- **`UnknownFlattenSeconds`** (default off): flatten and lock an account whose risk data has been unavailable that long while holding a position. Flatten orders do not need market data.
- **End-of-day high-water mark now uses the closing balance** (net liquidation minus open profit) by default (`HighWaterBasis=balance|netliq`, per account). Open profit at the close no longer lifts the floor above the firm's.
- **An unwatched session boundary is not guessed.** If NinjaTrader did not see the close and the balance changed since, you get **CLOSING BALANCE UNCERTAIN** and the high-water mark and best day are left alone. If the balance is unchanged (an overnight restart), the last reading is used.
- **`/risk seed ACCOUNT value`** sets the real high-water mark for an account that already has profit (and `/risk seed ACCOUNT bestday value`). It shows the resulting floor and asks for a button press, as does `/risk reset`. When a mark starts at `StartingBalance` on an account above it, you are told the floor may be too low.
- **Several confirmation buttons can be pending at once.** A `/floor` prompt no longer cancels a `/flatten` prompt.
- **`LockGroup`** (default off): when one account is locked, lock them all, so a copier stops re-opening trades on a locked follower.

New rule trackers (both off until you set them)
- **Consistency rule** (`ConsistencyPercent`, `ConsistencyProfitTarget`, `ConsistencyAlerts`, `ConsistencyAutoFlatten`): how much more profit today can add before today exceeds the percentage of total profit; best-day share in `/risk`. The percentage is yours to enter: sources disagree about firms' values.
- **Inactivity clock** (`InactivityDays`, `InactivityAlerts`, per account): warns as the days without a trade run out, tells you when a trade resets it, and `/activity` shows last trades and corrects them. Trade times are saved across restarts. The notifier never places trades.

Smaller
- New config keys are validated and misspellings suggested. `/simulate` gains 8 samples (47 in total).
- `/help` and the header list the new commands.

Review follow-ups (also in 0.4.0)
- **The risk state machine moved into `Core.cs`** as plain inputs and outputs (`EvaluateRisk`), so the riskiest code is unit-tested: weekend rollover (best day not inflated, floor unchanged), overnight restart with and without account changes, data outage while holding a position (alert, minute reminders, flatten timing, frozen readings ignored), floor levels and auto-flatten timing, daily loss, consistency, session-lock release. The add-on's own copy was removed; both code paths now use this one. Mutation-checked: six deliberate breakages are each caught.
- **The behavioural tests are in the repo** (`tests/behavioral`, with the stand-in NinjaTrader API) and run with `bash tests/run-behavioral.sh`; before, the README cited tests nobody else could run.
- The Output window and `/diag` now say which data check each account uses: its own connection, or any connection.
- `/risk` shows the open P&L the closing balance is derived from, so a connection that reports it as 0 is visible.
- `/risk` shows how much more total profit cures a best day that is already over the consistency limit.
- `InactivityAlerts` defaults to `3,2,1` (a calendar-day count can end sooner than 24-hour days); README and the config template say to treat the last alert day as the deadline. `ConsistencyProfitTarget` is documented as "leave blank unless your firm measures against the target".

Testing
- 277 core unit tests (was 153 in 0.3.0): balance basis, consistency cap and cure, inactivity days, new keys, and the risk state machine.
- 86 behavioural checks in the new `05-v04-rules` suite, plus the four earlier suites, all in the repo.

## 0.3.0 (2026-10-27)

Risk enforcement, delivery guarantees, configurable drawdown models, and real unit tests. Based partly on an external code review.

**Upgrading from 0.2:** the add-on is now a folder of files. **Delete `AddOns\DirectTelegramNotifierAddOn.cs`** before copying the new `DirectTelegramNotifier` folder into `AddOns`, or NinjaTrader will report the class as defined twice. Your config, floors and logs carry over; new keys use their defaults.

Risk
- **Account lock.** Auto-flatten now *latches*: the account stays locked, and anything that opens on it is flattened again at once (from the execution event, with a 1 s fallback) until `/unlock` or the session ends. Before, a copier or strategy could re-enter and trade through the floor during a 10-minute cooldown. Locks survive a restart within the same session.
- `/lock [account]` and `/unlock [account]`: keep an account flat on demand.
- **Hold after `/flatten`** (`FlattenHoldSeconds=30`): a copier or strategy re-entry, or a follower flipped by the copier copying the leader's close, is closed again.
- **Drawdown modes** (`DrawdownMode=off|manual|static|eod|intraday`, with `StartingBalance`, `DrawdownAmount`, `FloorLock`): enter your rule once instead of a number every day. High-water marks are saved across restarts. `/floor` still overrides.
- **Daily loss limit** (`DailyLossLimit`, dollars or percent; `DailyLossAlerts`; optional `DailyLossAutoFlatten`).
- **Risk UNKNOWN:** holding a position while data is disconnected or stale (net liquidation frozen) alerts once and pauses automatic action for that account.
- Faster: risk is checked every second (was 5 s), and confirmation is time-based (`RiskConfirmSeconds=2`) instead of two readings.
- **Floor validation:** a `/floor` above net liquidation, inside your alert levels, or moving more than `FloorJumpConfirm` needs a button press.
- Per-account overrides for `DrawdownMode`, `RiskFloor`, `StartingBalance`, `DrawdownAmount`, `FloorLock`, `DailyLossLimit` (`Key:Account=value`).
- `/risk` report; `/risk reset [account]` restarts a trailing floor.

Delivery
- **Priority lane:** problem alerts and command replies are sent before routine messages; four or more waiting routine messages are merged into one. A Telegram rate limit pauses all sending; a new priority alert is tried immediately during a network backoff.
- **Heartbeat vouches for delivery:** the external ping is held while Telegram is unreachable, the token is rejected, or alerts are stuck in the queue, so your dead-man's switch fires. Uses `getMe` when there is no other traffic.
- TCP keepalive on the Telegram connection; shorter long-poll timeout.

Smaller
- **Unrecognized config keys are reported** (with "did you mean" suggestions), as are invalid modes, quiet windows and order states.
- `/leader [account|clear]`, saved across restarts.
- `/flatten` and `/cancel` no longer hold up other commands during their verification wait.
- Custom stale-data schedules (`StaleDataSchedule=custom`, `StaleDataQuietWindows`, `StaleDataTimeZone`) for non-CME markets.
- `StuckOrderStates` and `FillGroupMs` are configurable.
- The re-entry alert points to `/lock`.

Structure and testing
- Split into partial-class files plus `DirectTelegramNotifier.Core.cs`, which holds the pure logic with no NinjaTrader types.
- `tests/CoreTests.cs`: 153 unit tests, run with `tests\run-tests.ps1` using the compiler built into Windows. See `docs/TESTING.md`.
- The README's Verified table now separates unit-tested, seen on Sim and seen live.


## 0.2.0 (2026-10-26)

Safety checks, account-level risk and targeted commands.

- Unprotected-position alert (position without a working stop; off by default).
- Orphan exit orders alert (account flat, stop/target-type orders still working).
- Stuck-order alert (Submitted / change pending / cancel pending too long).
- Position limit alert (`MaxPositionContracts`).
- Risk buffer: alerts as Net Liquidation approaches a drawdown floor, "below floor" and recovery notices, optional per-account auto-flatten (`RiskAutoFlatten`, off by default).
- `/floor` command to show and set floors (saved across restarts); `/status` shows net liquidation, floor and buffer.
- `/flatten [account]` and `/cancel [account]`; the confirm buttons name the account.
- NinjaTrader CPU, memory and disk alerts; CPU, memory and disk shown in `/diag`.
- Fixed: falling below the floor is now its own, tightest alert level.

## 0.1.2 (2026-10-26)

- Commands arrive almost instantly: Telegram long polling replaces polling every few seconds.
- Per-account statistics in `/status` and the end-of-day report (previously summed across accounts).
- Copy quality: follower fills compared with the leader (ticks, dollars, lag), in the end-of-day report and `/copy`.
- `EnableFillCsv`: every fill and leader-vs-follower comparison is appended to `DirectTelegramNotifier-fills.csv`.
- `SilentRoutineAlerts`: routine messages arrive without a notification sound; problems still ring.
- `FlattenConfirm`: optional Confirm / Cancel buttons for `/flatten`.
- New commands: `/copy`, `/eod`, `/diag`, `/version`, `/simulate` (a sample of every alert, built by the real code).
- New alert: a position opened shortly after `/flatten` (a strategy or the copier is trading again).
- New alert: PC clock differs from Telegram's by more than 15 seconds.
- Memory use is bounded: tracking sets are pruned by order lifetime and by age.
- Alerts that Telegram cannot parse are re-sent as plain text instead of being lost.
- Every network call now has its own timeout.

## 0.1.1 (2026-10-26)

- Fills of one instrument and side are grouped into one message, with each follower's price difference from the leader in ticks.
- Internal copier order IDs are hidden from fill alerts.
- All times use the NinjaTrader clock, including the restart report and the alert log.
- `/status` is easier to read: Accounts header, spacing, leader marked.
- "Connecting" shows an hourglass instead of a warning sign.

## 0.1.0 (2026-10-26) - first public beta

- Fill alerts with partial-fill merging.
- Connection lost / disconnected / restored alerts for orders and market data, merged per connection, with flap muting.
- NinjaTrader UI-freeze detection.
- Rejected-order alerts and an order-rate alert.
- Strategy enabled / disabled / removed alerts.
- Stale market-data alerts.
- Trade-copier mismatch alerts (leader vs followers).
- Restart / crash report on startup, plus an optional external heartbeat ping.
- End-of-day summary, `/status`, `/flatten`, `/help`.
- Local alert log.
