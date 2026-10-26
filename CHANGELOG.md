# Changelog

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
- All times now use the NinjaTrader clock, including the restart report and the alert log.
- `/status` is easier to read: Accounts header, spacing, leader marked.
- "Connecting" shows ⏳ instead of ⚠️.

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