# Changelog

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
