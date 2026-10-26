# Changelog

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