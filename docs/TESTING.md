# Testing

There are three levels of testing, and they prove different things. The README's *Verified* table says which level each feature has reached.

| Level | What it proves | How |
|---|---|---|
| **Unit tests** | The pure logic is right: floor formulas, risk levels, daily loss, floor validation, quiet windows, config-key checks, command parsing, JSON | `tests\run-tests.ps1` (153 checks, no NinjaTrader needed) |
| **Seen on Sim** | It behaves correctly inside real NinjaTrader with real events | The checklist below |
| **Seen live** | The same, on a funded or evaluation account | Only after Sim |

The maintainer also runs behavioural tests of the whole add-on against a stand-in for the NinjaTrader API. These catch logic and wiring mistakes, but they cannot prove how NinjaTrader or your broker connection behaves, which is why the Sim checklist matters.

## Unit tests

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

It compiles `DirectTelegramNotifier.Core.cs` with the C# compiler built into Windows and prints `153 passed, 0 failed`.

## Sim checklist (run before trusting a safety feature)

Use Sim accounts, with the copier running if you use one. Tick each box only after you have seen the result yourself.

### Auto-flatten and the account lock
Settings: `RiskAutoFlatten=true`, `RiskFlattenBuffer=150`, `RiskConfirmSeconds=2`.
- [ ] Open a small position on a Sim follower. Send `/floor <follower> <net liq - 100>`, then press **Set floor** on the confirmation. Within about 3 s you get **ACCOUNT LOCKED** and the account is flat.
- [ ] Let the copier re-sync the follower, or enter manually. You get **LOCK ENFORCED** and the position closes again within a few seconds, without overshooting into the opposite side.
- [ ] `/risk` shows **LOCKED**. `/unlock <follower>` releases it.
- [ ] Restart NinjaTrader while an account is locked: it is still locked afterwards (same session).

### /flatten with the copier actively copying
Settings: `FlattenHoldSeconds=30`.
- [ ] With the leader and followers in a copied position, send `/flatten`. Afterwards every account is flat.
- [ ] Open `DirectTelegramNotifier-fills.csv` and check that no follower briefly flipped to the other side. If one did, you should also have received **RE-ENTRY CLOSED** for it.

### Floor validation
- [ ] `/floor` with a value above net liquidation asks for confirmation, and **Cancel** changes nothing.
- [ ] A floor that jumps more than `FloorJumpConfirm` asks for confirmation.

### Drawdown modes (pick the one matching your rules)
- [ ] `DrawdownMode=static` with `StartingBalance` and `DrawdownAmount`: `/risk` shows the expected floor.
- [ ] `eod`: after the session boundary (`EodReportTime`), a profitable day gives **FLOOR TRAILED** and the new floor matches your firm's dashboard.
- [ ] `intraday`: `/risk` shows the high-water mark following net liquidation upward.

### Daily loss limit
Settings: `DailyLossLimit=` small (for example 100), `DailyLossAutoFlatten=true`.
- [ ] Losing trades give **DAILY LOSS 50% / 75% / 90%**, then **DAILY LOSS LIMIT REACHED**, then **ACCOUNT LOCKED**.

### Risk UNKNOWN
- [ ] Holding a Sim position with a floor set, disconnect the data connection. You get **RISK UNKNOWN** once, and nothing is auto-flattened. Reconnect: **RISK DATA BACK**.

### Delivery
- [ ] With `HeartbeatUrl` set, unplug the network for longer than the Healthchecks grace time. The check goes down; reconnect and it recovers.
- [ ] Put a misspelled key in the config and press F5: you get **CONFIG WARNING** naming it.
- [ ] `/leader <account>` changes the leader shown in `/status`, and it survives a restart.

Report anything that differs as an issue, with the Output window lines (redact tokens, IDs and account numbers).
