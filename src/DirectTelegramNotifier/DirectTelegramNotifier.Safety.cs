#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using CbiAccount = NinjaTrader.Cbi.Account;
using CbiConnection = NinjaTrader.Cbi.Connection;
using CbiExecution = NinjaTrader.Cbi.Execution;
using CbiInstrument = NinjaTrader.Cbi.Instrument;
using CbiOrder = NinjaTrader.Cbi.Order;
using CbiPosition = NinjaTrader.Cbi.Position;
using Dtn = NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifierCore;
#endregion

// DirectTelegramNotifier: Safety checks: exposure, orders, risk floors, daily loss, account locks, machine health.
// One part of a partial class: see DirectTelegramNotifier.cs for the overview.

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier
    {
        #region Exposure, risk and machine checks

        // A condition that has to hold for a while before it is worth an alert; alerts once per episode.
        private sealed class Condition
        {
            public string Key;
            public string Kind;
            public int Seconds;
            public string Instrument;
            public int Net;
            public int Covered;
            public int Count;
            public string Names;
            public TimeSpan Elapsed;
        }

        private sealed class StateStamp
        {
            public string State;
            public DateTime Since;
        }

        // ---- Unprotected positions, orphan exit orders, position limit ----

        private void CheckExposure()
        {
            if (cfgProtectiveCheckSec <= 0 && cfgOrphanOrderSec <= 0 && cfgMaxPositionContracts <= 0)
                return;
            try
            {
                DateTime now = DateTime.UtcNow;
                string[] tokens = (cfgOrphanOrderNames ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
                List<string> messages = new List<string>();

                foreach (CbiAccount a in SnapshotTrackedAccounts())
                {
                    Dictionary<string, int> nets = NetPositions(a);
                    List<CbiOrder> working = WorkingOrders(a).Where(o => o.Instrument != null).ToList();

                    HashSet<string> instruments = new HashSet<string>(nets.Keys, StringComparer.OrdinalIgnoreCase);
                    foreach (CbiOrder w in working)
                        instruments.Add(w.Instrument.FullName);

                    List<Condition> active = new List<Condition>();
                    foreach (string inst in instruments)
                    {
                        int net;
                        nets.TryGetValue(inst, out net);
                        List<CbiOrder> here = working.Where(o => o.Instrument.FullName.Equals(inst, StringComparison.OrdinalIgnoreCase)).ToList();

                        if (cfgProtectiveCheckSec > 0 && net != 0)
                        {
                            int covered = ProtectiveQuantity(here, net > 0);
                            if (covered < Math.Abs(net))
                                active.Add(new Condition { Key = "P|" + a.Name + "|" + inst, Kind = "P", Seconds = cfgProtectiveCheckSec, Instrument = inst, Net = net, Covered = covered });
                        }

                        if (cfgOrphanOrderSec > 0 && net == 0 && tokens.Length > 0)
                        {
                            List<CbiOrder> exits = here.Where(o => LooksLikeExitOrder(o, tokens)).ToList();
                            if (exits.Count > 0)
                            {
                                string names = string.Join(", ", exits.Select(o => string.IsNullOrEmpty(o.Name) ? o.OrderType.ToString() : o.Name).Distinct().ToArray());
                                active.Add(new Condition { Key = "O|" + a.Name + "|" + inst, Kind = "O", Seconds = cfgOrphanOrderSec, Instrument = inst, Count = exits.Count, Names = names });
                            }
                        }
                    }

                    if (cfgMaxPositionContracts > 0)
                    {
                        int total = nets.Values.Sum(v => Math.Abs(v));
                        if (total > cfgMaxPositionContracts)
                            active.Add(new Condition { Key = "L|" + a.Name, Kind = "L", Seconds = 0, Net = total });
                    }

                    List<Condition> firing = new List<Condition>();
                    lock (episodeLock)
                    {
                        HashSet<string> activeKeys = new HashSet<string>(active.Select(c => c.Key));
                        foreach (Condition cond in active)
                        {
                            DateTime start;
                            if (!episodeStart.TryGetValue(cond.Key, out start))
                            {
                                start = now;
                                episodeStart[cond.Key] = start;
                            }
                            cond.Elapsed = now - start;
                            if (cond.Elapsed.TotalSeconds >= cond.Seconds && episodeAlerted.Add(cond.Key))
                                firing.Add(cond);
                        }

                        List<string> ended = episodeStart.Keys.Where(k => EpisodeBelongsTo(k, a.Name) && !activeKeys.Contains(k)).ToList();
                        foreach (string key in ended)
                        {
                            episodeStart.Remove(key);
                            episodeAlerted.Remove(key);
                        }
                    }

                    foreach (Condition cond in firing)
                    {
                        if (cond.Kind == "P")
                            messages.Add(MsgUnprotected(a.Name, cond.Instrument, cond.Net, cond.Covered, cond.Elapsed));
                        else if (cond.Kind == "O")
                            messages.Add(MsgOrphan(a.Name, cond.Instrument, cond.Count, cond.Names, cond.Elapsed));
                        else
                            messages.Add(MsgPositionLimit(a.Name, cond.Net, cfgMaxPositionContracts));
                    }
                }

                foreach (string text in messages)
                    EnqueueText(text);
            }
            catch (Exception ex)
            {
                WriteError("CheckExposure", ex);
            }
        }

        private static bool EpisodeBelongsTo(string key, string account)
        {
            string[] parts = key.Split('|');
            return parts.Length >= 2 && parts[1].Equals(account, StringComparison.OrdinalIgnoreCase);
        }

        // Quantity covered by working stop orders on the closing side (a profit-target limit does not protect).
        private static int ProtectiveQuantity(List<CbiOrder> orders, bool positionIsLong)
        {
            int qty = 0;
            foreach (CbiOrder o in orders)
            {
                if (!o.OrderType.ToString().StartsWith("Stop", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool closes = positionIsLong
                    ? o.OrderAction == OrderAction.Sell
                    : (o.OrderAction == OrderAction.BuyToCover || o.OrderAction == OrderAction.Buy);
                if (closes)
                    qty += Convert.ToInt32(o.Quantity);
            }
            return qty;
        }

        private static bool LooksLikeExitOrder(CbiOrder o, string[] tokens)
        {
            string name = o.Name ?? string.Empty;
            foreach (string token in tokens)
            {
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // ---- Orders stuck in a transitional state ----

        private bool IsStuckState(string state)
        {
            return stuckStates.Contains(state);
        }

        private static HashSet<string> ParseStuckStates(string text, List<string> warnings)
        {
            HashSet<string> valid = new HashSet<string>(Enum.GetNames(typeof(OrderState)), StringComparer.OrdinalIgnoreCase);
            HashSet<string> states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (text ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim();
                if (valid.Contains(name))
                    states.Add(name);
                else
                    warnings.Add("StuckOrderStates: '" + name + "' is not an order state");
            }
            return states;
        }

        private void CheckStuckOrder(CbiAccount a, CbiOrder o, string id)
        {
            DateTime now = DateTime.UtcNow;
            string state = o.OrderState.ToString();
            StateStamp stamp;
            bool alert = false;

            lock (orderLock)
            {
                if (!orderStateSince.TryGetValue(id, out stamp) || stamp.State != state)
                {
                    stamp = new StateStamp { State = state, Since = now };
                    orderStateSince[id] = stamp;
                    stuckAlerted.Remove(id);
                }
                if (IsStuckState(state) && (now - stamp.Since).TotalSeconds >= cfgStuckOrderSec)
                    alert = stuckAlerted.Add(id);
            }

            if (alert)
                EnqueueText(MsgStuck(a.Name, o.Instrument != null ? o.Instrument.FullName : "?", o.OrderAction.ToString(), o.OrderType.ToString(),
                    Convert.ToString(o.Quantity, CultureInfo.InvariantCulture), state, now - stamp.Since));
        }

        // ---- Drawdown-floor buffer ----

        private static double ParseOptionalDouble(string text)
        {
            return Dtn.ParseOptionalDouble(text);
        }

        private static List<double> ParseDollarList(string text)
        {
            return Dtn.ParseDollarList(text);
        }

        private static string N2(double value)
        {
            return value.ToString("N2", CultureInfo.InvariantCulture);
        }

        private static string N0(double value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private double FloorFor(string account)
        {
            string source;
            return FloorFor(account, out source);
        }

        // Precedence (in the core): a /floor setting, then the account's drawdown mode (static, eod, intraday), then RiskFloor.
        private double FloorFor(string account, out string source)
        {
            DtnRiskSettings settings = BuildRiskSettings(account);
            lock (riskLock)
            {
                DtnRiskState rs;
                riskStates.TryGetValue(account, out rs);
                return Dtn.EffectiveFloor(rs, settings, out source);
            }
        }

        // The account's rules as plain values for the core, from the config file, per-account lines and /floor.
        private DtnRiskSettings BuildRiskSettings(string name)
        {
            DtnRiskSettings s = new DtnRiskSettings();
            s.Mode = ModeFor(name);
            s.Basis = BasisFor(name);
            s.Drawdown = AccountNumber("DrawdownAmount", name, cfgDrawdownAmount);
            s.FloorLock = AccountNumber("FloorLock", name, cfgFloorLock);
            s.StartingBalance = AccountNumber("StartingBalance", name, cfgStartingBalance);
            s.ManualFloor = AccountNumber("RiskFloor", name, cfgRiskFloor);
            lock (riskLock)
            {
                double over;
                if (floorOverrides.TryGetValue(name, out over) || floorOverrides.TryGetValue("*", out over))
                    s.OverrideFloor = over;
            }
            s.ConsistencyPercent = AccountNumber("ConsistencyPercent", name, cfgConsistencyPercent);
            s.ConsistencyTarget = AccountNumber("ConsistencyProfitTarget", name, cfgConsistencyTarget);
            s.DailyLimitText = AccountSetting("DailyLossLimit", name, cfgDailyLossLimit) ?? string.Empty;
            s.RiskBuffers = riskBuffers;
            s.DailyLossAlerts = dailyLossAlerts;
            s.ConsistencyAlerts = consistencyAlerts;
            s.RiskAutoFlatten = cfgRiskAutoFlatten;
            s.FlattenBuffer = cfgRiskFlattenBuffer;
            s.DailyLossAutoFlatten = cfgDailyLossAutoFlatten;
            s.ConsistencyAutoFlatten = cfgConsistencyAutoFlatten;
            s.ConfirmSeconds = cfgRiskConfirmSec;
            s.UnknownFlattenSeconds = cfgUnknownFlattenSec;
            return s;
        }

        private string AccountSetting(string key, string account, string fallback)
        {
            lock (riskLock)
            {
                string v;
                if (accountSettings.TryGetValue(key.ToLowerInvariant() + "|" + account, out v))
                    return v;
            }
            return fallback;
        }

        private bool AnyAccountSetting(string key)
        {
            string prefix = key.ToLowerInvariant() + "|";
            lock (riskLock)
                return accountSettings.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
        }

        private double AccountNumber(string key, string account, double fallback)
        {
            string v = AccountSetting(key, account, null);
            return v == null ? fallback : ParseOptionalDouble(v);
        }

        private string ModeFor(string account)
        {
            return Dtn.NormalizeMode(AccountSetting("DrawdownMode", account, cfgDrawdownMode)) ?? Dtn.ModeManual;
        }

        private double DailyLimitFor(string account, double sessionStart)
        {
            return Dtn.ResolveAmountOrPercent(AccountSetting("DailyLossLimit", account, cfgDailyLossLimit), sessionStart);
        }

        private bool DailyLimitConfigured(string account)
        {
            return !string.IsNullOrWhiteSpace(AccountSetting("DailyLossLimit", account, cfgDailyLossLimit));
        }

        private string CurrentSessionKey()
        {
            return Dtn.SessionKey(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eodTimeZone), eodTimeOfDay);
        }

        private DtnRiskState RiskStateFor(string account)
        {
            lock (riskLock)
            {
                DtnRiskState rs;
                if (!riskStates.TryGetValue(account, out rs))
                {
                    rs = new DtnRiskState();
                    riskStates[account] = rs;
                }
                return rs;
            }
        }

        // Read by name so a NinjaTrader version without this account item cannot break the compile.
        private static double NetLiquidationOf(CbiAccount a)
        {
            try
            {
                object item = Enum.Parse(typeof(AccountItem), "NetLiquidation");
                return a.Get((AccountItem)item, Currency.UsDollar);
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        private void RiskTimerCallback(object state)
        {
            if (Interlocked.CompareExchange(ref riskBusy, 1, 0) != 0)
                return;
            try
            {
                if (terminated || !started)
                    return;
                RiskTick(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                WriteError("Risk check", ex);
            }
            finally
            {
                Interlocked.Exchange(ref riskBusy, 0);
            }
        }

        private void RiskTick(DateTime now)
        {
            List<string> loud = new List<string>();
            List<string> quiet = new List<string>();
            List<CriticalAlert> critical = new List<CriticalAlert>();
            string session = CurrentSessionKey();

            ExpireHolds(now);

            foreach (CbiAccount a in SnapshotTrackedAccounts())
            {
                // NinjaTrader is read here, never while riskLock is held.
                double netLiq = NetLiquidationOf(a);
                if (double.IsInfinity(netLiq))
                    netLiq = double.NaN;
                double unrealized = SafeAccountValue(a, AccountItem.UnrealizedProfitLoss);
                double balance = Dtn.BalanceFromNetLiq(netLiq, unrealized);
                bool exposed = NetPositions(a).Count > 0;
                DtnOpenPnlWatch pnlWatch = PnlWatchFor(a.Name);
                if (pnlWatch.Observe(now, exposed, unrealized, netLiq))
                    loud.Add(MsgOpenPnlSuspect(a.Name, pnlWatch.Move));
                bool dataUp = DataUpFor(a);                       // every tick, so the check in use is logged before any position is held
                string staleInstrument = exposed && dataUp ? StaleHeldFeed(a) : null;

                string why = null;
                if (exposed && RiskMonitored(a.Name))
                {
                    if (!dataUp)
                        why = "this account's data connection is down";
                    else if (double.IsNaN(netLiq))
                        why = "net liquidation cannot be read";
                    else if (staleInstrument != null)
                        why = "market data for " + staleInstrument + " is stale";
                }

                string lockReason = EvaluateAccountRisk(a.Name, netLiq, balance, why, session, now, loud, quiet, critical);
                if (lockReason != null)
                {
                    string locked = LockAndFlatten(a, lockReason);
                    loud.Add(locked);
                    critical.Add(new CriticalAlert { Full = locked, Minimal = "LOCKED: account " + Dtn.MaskAccount(a.Name) + " was flattened and locked. Open NinjaTrader now." });
                }
                else
                    EnforceLock(a, now, loud, critical);
            }

            foreach (string text in loud)
                EnqueueText(text);
            foreach (string text in quiet)
                EnqueueQuiet(text);
            foreach (CriticalAlert alert in critical)
                SendBackup(alert.Full, alert.Minimal);
            SaveRiskStateIfDue(now);
        }

        private bool RiskMonitored(string account)
        {
            return !double.IsNaN(FloorFor(account)) || DailyLimitConfigured(account);
        }

        private string BasisFor(string account)
        {
            return Dtn.NormalizeBasis(AccountSetting("HighWaterBasis", account, cfgHighWaterBasis)) ?? Dtn.BasisBalance;
        }

        private static System.Reflection.PropertyInfo accountConnectionProperty;
        private static bool accountConnectionLooked;

        private readonly object dataCheckLock = new object();
        private readonly Dictionary<string, string> dataCheckPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Is the market-data side of THIS account's connection up? Falls back to "every connection" when NinjaTrader
        // does not say which connection the account uses. Which of the two is in use is written to the Output window and /diag.
        private bool DataUpFor(CbiAccount a)
        {
            string path;
            bool up;
            try
            {
                if (!accountConnectionLooked)
                {
                    accountConnectionProperty = typeof(CbiAccount).GetProperty("Connection");
                    accountConnectionLooked = true;
                }
                object value = null;
                if (accountConnectionProperty != null)
                    value = accountConnectionProperty.GetValue(a, null);
                else
                {
                    System.Reflection.FieldInfo field = typeof(CbiAccount).GetField("Connection");
                    if (field != null)
                        value = field.GetValue(a);
                }
                CbiConnection connection = value as CbiConnection;
                if (connection != null)
                {
                    path = "the account's own connection '" + (connection.Options != null && !string.IsNullOrEmpty(connection.Options.Name) ? connection.Options.Name : "unnamed") + "'";
                    up = connection.PriceStatus == ConnectionStatus.Connected;
                }
                else
                {
                    path = "ANY data connection (NinjaTrader does not say which connection this account uses)";
                    up = AllDataConnectionsUp();
                }
            }
            catch (Exception)
            {
                path = "ANY data connection (looking up the account's connection failed)";
                up = AllDataConnectionsUp();
            }

            bool changed;
            lock (dataCheckLock)
            {
                string previous;
                changed = !dataCheckPath.TryGetValue(a.Name, out previous) || previous != path;
                dataCheckPath[a.Name] = path;
            }
            if (changed)
                WriteOutput("Risk data check for " + a.Name + ": using " + path + ".");
            return up;
        }

        // Runs the core state machine for one account under riskLock and turns its events into messages.
        // Returns a lock reason when auto-flatten should fire. `critical` collects the alerts that also go to the backup channel.
        private string EvaluateAccountRisk(string name, double netLiq, double balance, string unknownWhy, string session, DateTime now, List<string> loud, List<string> quiet, List<CriticalAlert> critical)
        {
            if (double.IsNaN(netLiq) && !riskReadProblemReported)
            {
                riskReadProblemReported = true;
                WriteOutput("Risk: net liquidation could not be read for " + name + ".");
            }

            DtnRiskSettings settings = BuildRiskSettings(name);
            DtnRiskInput input;
            string fireReason;
            lock (riskLock)
            {
                DtnRiskState rs;
                if (!riskStates.TryGetValue(name, out rs))
                {
                    rs = new DtnRiskState();
                    riskStates[name] = rs;
                }
                AccountLock current;
                string lockedSession = null;
                if (accountLocks.TryGetValue(name, out current) && !current.IsHold)
                    lockedSession = current.Session ?? string.Empty;

                input = new DtnRiskInput
                {
                    Now = now, Session = session, NetLiq = netLiq, Balance = balance,
                    UnknownWhy = unknownWhy, LockedSession = lockedSession, Settings = settings
                };
                DtnRiskResult result = Dtn.EvaluateRisk(rs, input);
                if (result.Dirty)
                    riskStateDirty = true;
                if (result.ReleaseSessionLock)
                    accountLocks.Remove(name);
                foreach (DtnRiskEvent e in result.Events)
                {
                    string text = RenderRiskEvent(name, e);
                    if (e.Loud)
                        loud.Add(text);
                    else
                        quiet.Add(text);
                    if (Dtn.IsCriticalEvent(e, settings))
                        critical.Add(new CriticalAlert { Full = text, Minimal = Dtn.BackupMinimalText(e, Dtn.MaskAccount(name)) });
                }
                fireReason = result.FireReason;
            }
            RecordRiskReading(name, now, input);                // after the lock: it writes a file
            return fireReason;
        }

        private static string RenderRiskEvent(string account, DtnRiskEvent e)
        {
            switch (e.Kind)
            {
                case DtnRiskEventKind.Unknown:          return MsgRiskUnknown(account, e.Text, TimeSpan.FromSeconds(e.A), e.N);
                case DtnRiskEventKind.DataBack:         return MsgRiskKnown(account);
                case DtnRiskEventKind.FloorTrailed:     return MsgFloorTrailed(account, e.A, e.B, e.C);
                case DtnRiskEventKind.ClosingUncertain: return MsgClosingUnknown(account, e.A, e.B);
                case DtnRiskEventKind.HighWaterStarted: return MsgHighWaterStarted(account, e.A, e.B, e.Flag);
                case DtnRiskEventKind.RiskBuffer:       return MsgRiskBuffer(account, e.A, e.B, e.C, e.D, e.Text);
                case DtnRiskEventKind.RiskRecovered:    return MsgRiskRecovered(account, e.A);
                case DtnRiskEventKind.DailyLoss:        return MsgDailyLoss(account, e.A, e.B, e.N);
                case DtnRiskEventKind.DailyLossBack:    return MsgDailyLossRecovered(account, e.A, e.B);
                case DtnRiskEventKind.Consistency:      return MsgConsistency(account, e.A, e.B, e.N, e.C);
                case DtnRiskEventKind.ConsistencyBack:  return MsgConsistencyBack(account, e.A, e.B);
                case DtnRiskEventKind.LockReleased:     return MsgUnlocked(account, "the session ended");
                default:                                return string.Empty;
            }
        }

        // ---- account locks

        private bool IsAccountLocked(string account)
        {
            lock (riskLock)
                return accountLocks.ContainsKey(account);
        }

        // Records a lock. A session lock replaces a hold; a hold never replaces a session lock. Returns false if nothing changed.
        private bool SetLock(string account, string reason, bool sessionLock, int holdSeconds)
        {
            DateTime now = DateTime.UtcNow;
            string session = CurrentSessionKey();
            lock (riskLock)
            {
                AccountLock existing;
                if (accountLocks.TryGetValue(account, out existing) && !existing.IsHold)
                    return false;
                accountLocks[account] = new AccountLock
                {
                    Reason = reason, Session = session, SinceUtc = now, LastEnforceUtc = now,
                    UntilUtc = sessionLock ? DateTime.MaxValue : now.AddSeconds(holdSeconds)
                };
                if (sessionLock)
                    riskStateDirty = true;
            }
            return true;
        }

        private void ExpireHolds(DateTime now)
        {
            lock (riskLock)
            {
                List<string> done = accountLocks.Where(kv => kv.Value.IsHold && kv.Value.UntilUtc <= now).Select(kv => kv.Key).ToList();
                foreach (string key in done)
                    accountLocks.Remove(key);
            }
        }

        private string LockAndFlatten(CbiAccount a, string reason)
        {
            lastFlattenUtc = DateTime.UtcNow;
            reentryAlerted = true;                              // lock enforcement reports re-entries from here on
            SetLock(a.Name, reason, true, 0);
            string text = MsgLocked(a.Name, reason, FlattenAndCancel(a));

            // LockGroup: a copier would keep re-opening trades on a locked follower; locking every account stops that.
            if (cfgLockGroup)
            {
                List<string> others = new List<string>();
                foreach (CbiAccount other in SnapshotTrackedAccounts())
                {
                    if (other.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (SetLock(other.Name, "group lock after " + a.Name, true, 0))
                        others.Add(Html(other.Name) + ": " + Html(FlattenAndCancel(other)));
                }
                if (others.Count > 0)
                    text += "\n<b>Group lock</b> (LockGroup=true) " + string.Join("; ", others.ToArray());
            }
            SaveRiskState();
            List<CbiAccount> flattened = new List<CbiAccount> { a };
            if (cfgLockGroup)
                foreach (CbiAccount other in SnapshotTrackedAccounts())
                    if (!other.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                        flattened.Add(other);
            WatchFlat(flattened, "automatic flatten");
            return text;
        }

        // Flattens every exposed instrument (which also cancels their orders) and cancels any other working orders.
        private string FlattenAndCancel(CbiAccount a)
        {
            List<string> done = new List<string>();
            try
            {
                List<CbiInstrument> instruments = CollectExposedInstruments(a);
                if (instruments.Count > 0)
                {
                    a.Flatten(instruments);
                    done.Add("flattened " + string.Join(", ", instruments.Select(i => i.FullName).ToArray()));
                }
                HashSet<string> flattened = new HashSet<string>(instruments.Select(i => i.FullName), StringComparer.OrdinalIgnoreCase);
                List<CbiOrder> others = WorkingOrders(a).Where(o => o.Instrument == null || !flattened.Contains(o.Instrument.FullName)).ToList();
                if (others.Count > 0)
                {
                    string how;
                    done.Add(TryCancelOrders(a, others, out how) ? "cancelled " + Num(others.Count) + " other order(s)" : "cancel failed (" + how + ")");
                }
            }
            catch (Exception ex)
            {
                WriteError("Flatten", ex);
                done.Add("failed: " + ex.Message);
            }
            return done.Count == 0 ? "already flat, no working orders" : string.Join("; ", done.ToArray());
        }

        private void EnforceLockSoon(CbiAccount a)
        {
            Task.Run(() =>
            {
                try
                {
                    List<string> loud = new List<string>();
                    List<CriticalAlert> critical = new List<CriticalAlert>();
                    EnforceLock(a, DateTime.UtcNow, loud, critical);
                    foreach (string text in loud)
                        EnqueueText(text);
                    foreach (CriticalAlert alert in critical)
                        SendBackup(alert.Full, alert.Minimal);
                }
                catch (Exception ex)
                {
                    WriteError("Lock enforcement", ex);
                }
            });
        }

        private void EnforceLock(CbiAccount a, DateTime now, List<string> loud, List<CriticalAlert> critical)
        {
            AccountLock lk;
            lock (riskLock)
            {
                if (!accountLocks.TryGetValue(a.Name, out lk))
                    return;
                if ((now - lk.LastEnforceUtc).TotalMilliseconds < LockEnforceGapMs)
                    return;
            }

            bool open = NetPositions(a).Count > 0;
            List<CbiOrder> working = WorkingOrders(a);
            if (!open && working.Count == 0)
                return;
            // A flatten is still being worked (NinjaTrader names its closing orders "Close"): flattening again could overshoot.
            if (working.Any(o => string.Equals(o.Name, "Close", StringComparison.OrdinalIgnoreCase)))
                return;

            int count;
            bool alert;
            lock (riskLock)
            {
                if ((now - lk.LastEnforceUtc).TotalMilliseconds < LockEnforceGapMs)
                    return;                                     // another thread got here first
                lk.LastEnforceUtc = now;
                lk.Enforced++;
                count = lk.Enforced;
                alert = count == 1 || (now - lk.LastAlertUtc).TotalSeconds >= LockAlertGapSec;
                if (alert)
                    lk.LastAlertUtc = now;
            }

            string done = FlattenAndCancel(a);
            if (alert)
            {
                string enforced = MsgLockEnforced(a.Name, done, count, lk.IsHold);
                loud.Add(enforced);
                critical.Add(new CriticalAlert
                {
                    Full = enforced,
                    Minimal = (lk.IsHold ? "RE-ENTRY CLOSED: account " : "LOCK ENFORCED: account ") + Dtn.MaskAccount(a.Name)
                        + " traded again and was closed. Something keeps trading it. Open NinjaTrader now."
                });
            }
        }

        private string ExecuteLockCommand(string filter)
        {
            string problem;
            List<CbiAccount> targets = ResolveAccounts(filter, out problem);
            if (targets == null)
                return EmInfo + " " + Html(problem);
            if (targets.Count == 0)
                return EmInfo + " No tracked accounts.";

            StringBuilder sb = new StringBuilder();
            sb.Append(EmLock).Append(" <b>LOCKED</b>\n");
            foreach (CbiAccount a in targets)
            {
                bool changed = SetLock(a.Name, "/lock", true, 0);
                sb.Append(Html(a.Name)).Append(": ").Append(changed ? Html(FlattenAndCancel(a)) : "already locked").Append("\n");
            }
            SaveRiskState();
            WatchFlat(targets, "/lock");
            sb.Append("New positions are closed automatically until /unlock or the session ends.\n")
              .Append("<i>Strategies and copiers are not disabled: turn them off, or each new entry is closed at a loss.</i>");
            return sb.ToString();
        }

        private string ExecuteUnlockCommand(string filter)
        {
            string problem;
            List<CbiAccount> targets = ResolveAccounts(filter, out problem);
            if (targets == null)
                return EmInfo + " " + Html(problem);

            List<string> released = new List<string>();
            lock (riskLock)
            {
                foreach (CbiAccount a in targets)
                {
                    if (accountLocks.Remove(a.Name))
                    {
                        released.Add(a.Name);
                        DtnRiskState rs;
                        if (riskStates.TryGetValue(a.Name, out rs))
                        {
                            rs.FloorBreachSince = DateTime.MinValue;
                            rs.DailyBreachSince = DateTime.MinValue;
                        }
                    }
                }
                riskStateDirty = true;
            }
            SaveRiskState();
            if (released.Count == 0)
                return EmInfo + " Nothing was locked.";
            return MsgUnlocked(string.Join(", ", released.ToArray()), "/unlock")
                + "\n<i>Auto-flatten can lock it again if the floor or daily limit is still breached.</i>";
        }

        // ---- floors set by hand

        // Inside this distance from net liquidation a new floor triggers an alert or auto-flatten at once.
        private double ImmediateActionBuffer()
        {
            double smallest = riskBuffers.Count > 0 ? riskBuffers[0] : 0.0;
            return cfgRiskAutoFlatten ? Math.Max(smallest, cfgRiskFlattenBuffer) : smallest;
        }

        private string FloorProblems(string target, double value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (CbiAccount a in SnapshotTrackedAccounts())
            {
                if (target != "*" && !a.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                    continue;
                string reason = Dtn.ValidateFloor(value, NetLiquidationOf(a), ImmediateActionBuffer(), FloorFor(a.Name), cfgFloorJumpConfirm);
                if (reason != null)
                    sb.Append(Html(a.Name)).Append(": ").Append(Html(reason)).Append("\n");
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private string ApplyFloor(string target, double value)
        {
            lock (riskLock)
                floorOverrides[target] = value;
            bool saved = SaveFloorOverrides();
            string scope = target == "*" ? "every tracked account" : Html(target);
            return (saved ? EmOk : EmWarn + " (not saved to disk)") + " Floor " + N2(value) + " set for " + scope + ".\n\n" + BuildRiskReport();
        }



        private static string FloorsPath()
        {
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigPath()), "DirectTelegramNotifier.floors");
        }

        private void LoadFloorOverrides()
        {
            try
            {
                string path = FloorsPath();
                if (!File.Exists(path))
                    return;
                lock (riskLock)
                {
                    floorOverrides.Clear();
                    foreach (string line in File.ReadAllLines(path))
                    {
                        int eq = line.LastIndexOf('=');
                        if (eq <= 0)
                            continue;
                        double v = ParseOptionalDouble(line.Substring(eq + 1));
                        string account = line.Substring(0, eq).Trim();
                        if (!double.IsNaN(v) && account.Length > 0)
                            floorOverrides[account] = v;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("Loading floors", ex);
            }
        }

        private bool SaveFloorOverrides()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                lock (riskLock)
                {
                    foreach (KeyValuePair<string, double> kv in floorOverrides)
                        sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
                }
                string path = FloorsPath();
                if (sb.Length == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else
                {
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
                return true;
            }
            catch (Exception ex)
            {
                WriteError("Saving floors", ex);
                return false;
            }
        }

        // /floor            show floors and buffers
        // /floor 48250      floor for every tracked account
        // /floor 002 48500  floor for one account (part of its name)
        // /floor 002 clear  remove that account's /floor setting;  /floor clear  removes all /floor settings
        // Returns null when it asked for confirmation instead of replying.
        private string HandleFloorCommand(string argument)
        {
            string arg = (argument ?? string.Empty).Trim();
            string[] parts = arg.Length == 0 ? new string[0] : arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            const string usage = " Usage: /floor 48250  \u00B7  /floor 002 48500  \u00B7  /floor 002 clear  \u00B7  /floor clear";

            if (parts.Length == 0)
                return BuildRiskReport();
            if (parts.Length > 2)
                return EmInfo + usage;

            if (parts.Length == 1 && parts[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                lock (riskLock)
                    floorOverrides.Clear();
                return (SaveFloorOverrides() ? EmOk : EmWarn + " (not saved to disk)") + " All /floor settings removed.\n\n" + BuildRiskReport();
            }

            string target = "*";
            string valueText = parts[0];
            if (parts.Length == 2)
            {
                string problem;
                List<CbiAccount> targets = ResolveAccounts(parts[0], out problem);
                if (targets == null)
                    return EmInfo + " " + Html(problem);
                target = targets[0].Name;
                valueText = parts[1];

                if (valueText.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    lock (riskLock)
                        floorOverrides.Remove(target);
                    return (SaveFloorOverrides() ? EmOk : EmWarn + " (not saved to disk)") + " " + Html(target) + ": /floor setting removed.\n\n" + BuildRiskReport();
                }
            }

            double v = ParseOptionalDouble(valueText);
            if (double.IsNaN(v) || v <= 0)
                return EmInfo + usage;

            // A typo here can flatten accounts: anything suspicious needs a button press.
            string problems = FloorProblems(target, v);
            if (problems != null)
            {
                RequestConfirmation("floor", target, v,
                    EmWarn + " <b>Confirm floor " + N2(v) + "?</b>\nScope: " + (target == "*" ? "every tracked account" : Html(target)) + "\n" + problems
                    + (cfgRiskAutoFlatten ? "<i>Auto-flatten is on: a floor this close can lock the account right away.</i>" : string.Empty),
                    "Set floor");
                return null;
            }
            return ApplyFloor(target, v);
        }

        // /risk                          every account
        // /risk reset [account]          restart a trailing floor from StartingBalance (asks to confirm)
        // /risk seed ACCOUNT 51000       set the high-water mark to what the firm shows (asks to confirm)
        // /risk seed ACCOUNT bestday 800 set the best completed day, for the consistency rule (asks to confirm)
        // Returns null when it asked for confirmation instead of replying.
        private string HandleRiskCommand(string argument)
        {
            string arg = (argument ?? string.Empty).Trim();
            if (arg.Length == 0)
                return BuildRiskReport();

            const string usage = " Usage: /risk  \u00B7  /risk seed ACCOUNT 51000  \u00B7  /risk seed ACCOUNT bestday 800  \u00B7  /risk reset [ACCOUNT]";
            string[] parts = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();

            if (verb == "reset" && parts.Length <= 2)
            {
                string filter = parts.Length == 2 ? parts[1] : string.Empty;
                string problem;
                List<CbiAccount> targets = ResolveAccounts(filter, out problem);
                if (targets == null)
                    return EmInfo + " " + Html(problem);
                if (targets.Count == 0)
                    return EmInfo + " No tracked accounts.";
                RequestConfirmation("riskreset", filter, double.NaN,
                    EmWarn + " <b>Confirm trailing reset?</b>\nAccounts: " + Html(string.Join(", ", targets.Select(t => t.Name).ToArray())) + "\n"
                    + "The high-water mark and floor restart from StartingBalance (or the current balance). If the account has already banked profit "
                    + "the floor will be too LOW: /risk seed sets the real value instead.", "Reset");
                return null;
            }

            if (verb == "seed" && (parts.Length == 3 || parts.Length == 4))
            {
                string kind = parts.Length == 4 ? parts[2].ToLowerInvariant() : "highwater";
                double value = ParseOptionalDouble(parts[parts.Length - 1]);
                if ((kind != "highwater" && kind != "bestday") || double.IsNaN(value) || value <= 0)
                    return EmInfo + usage;

                string problem;
                List<CbiAccount> targets = ResolveAccounts(parts[1], out problem);
                if (targets == null)
                    return EmInfo + " " + Html(problem);
                CbiAccount a = targets[0];

                if (kind == "bestday")
                {
                    RequestConfirmation("riskbest", a.Name, value,
                        EmWarn + " <b>Confirm best day " + N2(value) + "?</b>\n" + Html(a.Name) + ": the largest single-day profit so far, for the consistency rule.", "Set");
                    return null;
                }

                string mode = ModeFor(a.Name);
                if (mode != Dtn.ModeEod && mode != Dtn.ModeIntraday)
                    return EmInfo + " " + Html(a.Name) + " uses " + ModeLabel(mode) + ": there is no trailing high-water mark to set. Use DrawdownMode=eod or intraday.";
                double drawdown = AccountNumber("DrawdownAmount", a.Name, cfgDrawdownAmount);
                if (double.IsNaN(drawdown) || drawdown <= 0)
                    return EmInfo + " Set DrawdownAmount for " + Html(a.Name) + " first.";

                double newFloor = Dtn.TrailingFloor(value, drawdown, AccountNumber("FloorLock", a.Name, cfgFloorLock), double.NaN);
                double netLiq = NetLiquidationOf(a);
                double oldFloor = FloorFor(a.Name);
                string warn = Dtn.ValidateFloor(newFloor, netLiq, ImmediateActionBuffer(), oldFloor, 0.0);
                RequestConfirmation("riskseed", a.Name, value,
                    EmWarn + " <b>Confirm high-water " + N2(value) + "?</b>\n" + Html(a.Name) + "\n"
                    + "Floor " + (double.IsNaN(oldFloor) ? string.Empty : N2(oldFloor) + " \u2192 ") + "<b>" + N2(newFloor) + "</b>"
                    + (double.IsNaN(netLiq) ? string.Empty : Sep + "net liq " + N2(netLiq) + Sep + N2(netLiq - newFloor) + " buffer") + "\n"
                    + (warn != null ? EmSiren + " " + Html(warn) + "\n" : string.Empty)
                    + "<i>This is the highest " + (mode == Dtn.ModeEod ? "closing balance" : "net liquidation") + " your firm has counted. Compare the floor with your firm's dashboard.</i>", "Set high-water");
                return null;
            }

            return EmInfo + usage;
        }

        private string ApplyRiskReset(string filter)
        {
            string problem;
            List<CbiAccount> targets = ResolveAccounts(filter, out problem);
            if (targets == null)
                return EmInfo + " " + Html(problem);
            lock (riskLock)
            {
                foreach (CbiAccount a in targets)
                {
                    DtnRiskState rs;
                    if (riskStates.TryGetValue(a.Name, out rs))
                    {
                        rs.HighWater = double.NaN;
                        rs.TrailFloor = double.NaN;
                    }
                }
                riskStateDirty = true;
            }
            SaveRiskState();
            return EmOk + " Trailing high-water and floor reset for " + Html(string.Join(", ", targets.Select(t => t.Name).ToArray()))
                + ". They restart from StartingBalance, or from the current balance if none is set, within a second.";
        }

        private string ApplySeed(string account, double value, bool bestDay)
        {
            lock (riskLock)
            {
                DtnRiskState rs;
                if (!riskStates.TryGetValue(account, out rs))
                {
                    rs = new DtnRiskState();
                    riskStates[account] = rs;
                }
                if (bestDay)
                {
                    rs.BestDay = value;
                }
                else
                {
                    rs.HighWater = value;
                    rs.TrailFloor = Dtn.TrailingFloor(value, AccountNumber("DrawdownAmount", account, cfgDrawdownAmount),
                        AccountNumber("FloorLock", account, cfgFloorLock), double.NaN);
                }
                riskStateDirty = true;
            }
            SaveRiskState();
            return EmOk + " " + Html(account) + ": " + (bestDay ? "best day " : "high-water ") + N2(value) + " set.\n\n" + BuildRiskReport();
        }

        private string HandleLeaderCommand(string argument)
        {
            string arg = (argument ?? string.Empty).Trim();
            string configured = (cfgLeaderAccount ?? string.Empty).Trim();
            if (arg.Length == 0)
            {
                string over;
                lock (riskLock)
                    over = leaderOverride;
                string current = ResolveLeaderName();
                return EmInfo + " Leader: " + (current.Length > 0 ? "<b>" + Html(current) + "</b>" : "none")
                    + (over.Length > 0 ? " (set with /leader)" : " (from the config file)")
                    + "\n<i>/leader 002 to change it, /leader clear to go back to the config file.</i>";
            }

            if (arg.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                lock (riskLock)
                    leaderOverride = string.Empty;
                SaveRiskState();
                return EmOk + " Leader back to the config file: " + Html(ResolveLeaderName().Length > 0 ? ResolveLeaderName() : "none");
            }

            string problem;
            List<CbiAccount> targets = ResolveAccounts(arg, out problem);
            if (targets == null)
                return EmInfo + " " + Html(problem);
            lock (riskLock)
                leaderOverride = targets[0].Name;
            SaveRiskState();
            return EmOk + " Leader is now <b>" + Html(targets[0].Name) + "</b>. Copy checks and fill comparisons use it from now on (saved across restarts).";
        }

        private string BuildRiskReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(EmChart).Append(" <b>RISK</b>\n");
            string leaderName = ResolveLeaderName();
            bool first = true;
            foreach (CbiAccount a in SnapshotTrackedAccounts())
            {
                if (!first)
                    sb.Append("\n");
                first = false;
                sb.Append("<b>").Append(Html(a.Name)).Append("</b>");
                if (leaderName.Length > 0 && a.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (leader)");
                sb.Append("\n").Append(BuildRiskLines(a, true));
            }
            if (first)
                sb.Append("No tracked accounts.\n");

            sb.Append("\n<i>Floor alerts at ").Append(riskBuffers.Count > 0 ? string.Join(", ", riskBuffers.Select(l => N2(l)).ToArray()) : "no levels")
              .Append(cfgRiskAutoFlatten ? "; auto-flatten within " + N2(cfgRiskFlattenBuffer) + " for " + Num(cfgRiskConfirmSec) + "s" : string.Empty).Append(".");
            if (!string.IsNullOrWhiteSpace(cfgDailyLossLimit) || AnyAccountSetting("DailyLossLimit"))
                sb.Append(" Daily loss alerts at ").Append(string.Join("/", dailyLossAlerts.Select(p => p.ToString("0", CultureInfo.InvariantCulture)).ToArray()))
                  .Append("% of the limit").Append(cfgDailyLossAutoFlatten ? "; auto-flatten at the limit" : string.Empty).Append(".");
            sb.Append("</i>\n<i>Compare with your broker's or firm's own numbers: they are the ones that count.</i>");
            return sb.ToString();
        }

        // detailed: /risk;  otherwise the short form used by /status.
        private string BuildRiskLines(CbiAccount a, bool detailed)
        {
            StringBuilder sb = new StringBuilder();
            string name = a.Name;
            string mode = ModeFor(name);
            string basis = BasisFor(name);
            double netLiq = NetLiquidationOf(a);
            string source;
            double floor = FloorFor(name, out source);
            double drawdown = AccountNumber("DrawdownAmount", name, cfgDrawdownAmount);
            double lockLevel = AccountNumber("FloorLock", name, cfgFloorLock);
            double startBalance = AccountNumber("StartingBalance", name, cfgStartingBalance);

            DtnRiskState snap = new DtnRiskState();
            AccountLock lk = null;
            lock (riskLock)
            {
                DtnRiskState rs;
                if (riskStates.TryGetValue(name, out rs))
                {
                    snap.HighWater = rs.HighWater;
                    snap.SessionStart = rs.SessionStart;
                    snap.SessionStartBalance = rs.SessionStartBalance;
                    snap.BestDay = rs.BestDay;
                    snap.BaselineMidSession = rs.BaselineMidSession;
                    snap.Unknown = rs.Unknown;
                }
                accountLocks.TryGetValue(name, out lk);
            }

            if (detailed)
            {
                sb.Append("  Mode: ").Append(ModeLabel(mode));
                if ((mode == Dtn.ModeStatic || mode == Dtn.ModeEod || mode == Dtn.ModeIntraday) && !double.IsNaN(drawdown))
                    sb.Append(Sep).Append("drawdown ").Append(N2(drawdown));
                if ((mode == Dtn.ModeEod || mode == Dtn.ModeIntraday) && !double.IsNaN(lockLevel))
                    sb.Append(Sep).Append("locks at ").Append(N2(lockLevel));
                if (mode == Dtn.ModeEod)
                    sb.Append(Sep).Append("high-water on ").Append(basis == Dtn.BasisBalance ? "closing balance" : "net liquidation");
                sb.Append("\n");
                if ((mode == Dtn.ModeStatic || mode == Dtn.ModeEod || mode == Dtn.ModeIntraday) && (double.IsNaN(drawdown) || drawdown <= 0))
                    sb.Append("  ").Append(EmWarn).Append(" Set DrawdownAmount").Append(mode == Dtn.ModeStatic ? " and StartingBalance" : string.Empty).Append(" for this mode\n");
            }

            if (mode != Dtn.ModeOff && (!double.IsNaN(floor) || detailed))
            {
                sb.Append("  Net liq ").Append(double.IsNaN(netLiq) ? "unavailable" : N2(netLiq));
                // The closing-balance basis is net liquidation minus this: a reading of exactly 0 while you hold a position is worth a look.
                if (detailed && mode == Dtn.ModeEod && basis == Dtn.BasisBalance && !double.IsNaN(netLiq))
                    sb.Append(Sep).Append("open P&amp;L ").Append(Money(SafeAccountValue(a, AccountItem.UnrealizedProfitLoss)));
                if (detailed && (mode == Dtn.ModeEod || mode == Dtn.ModeIntraday) && !double.IsNaN(snap.HighWater))
                    sb.Append(Sep).Append("high-water ").Append(N2(snap.HighWater));
                if (!double.IsNaN(floor))
                {
                    sb.Append(Sep).Append("floor ").Append(N2(floor));
                    if (detailed && source != mode)
                        sb.Append(" (").Append(Html(source)).Append(")");
                    if (!double.IsNaN(netLiq))
                        sb.Append(Sep).Append("<b>").Append(N2(netLiq - floor)).Append(" buffer</b>");
                }
                else
                {
                    sb.Append(Sep).Append("no floor");
                }
                sb.Append("\n");
            }

            double limit = DailyLimitFor(name, snap.SessionStart);
            if (!double.IsNaN(limit) && !double.IsNaN(snap.SessionStart) && !double.IsNaN(netLiq))
            {
                double result = netLiq - snap.SessionStart;
                sb.Append("  Today ").Append(Money(result)).Append(" of ").Append(N2(limit)).Append(" daily loss limit");
                if (result < 0)
                    sb.Append(" (").Append(Num((int)Math.Round(-result / limit * 100.0))).Append("%)");
                if (detailed && snap.BaselineMidSession)
                    sb.Append("\n  <i>Counted from when tracking started this session, not the session open.</i>");
                sb.Append("\n");
            }

            double consistencyPct = AccountNumber("ConsistencyPercent", name, cfgConsistencyPercent);
            if (!double.IsNaN(consistencyPct) && consistencyPct > 0 && consistencyPct < 100)
            {
                string pctText = consistencyPct.ToString("0.#", CultureInfo.InvariantCulture);
                if (double.IsNaN(startBalance) || double.IsNaN(snap.SessionStartBalance))
                {
                    sb.Append("  ").Append(EmWarn).Append(" Consistency ").Append(pctText).Append("% needs StartingBalance\n");
                }
                else
                {
                    double cap = Dtn.ConsistencyCap(snap.SessionStartBalance - startBalance, consistencyPct,
                        AccountNumber("ConsistencyProfitTarget", name, cfgConsistencyTarget));
                    double today = double.IsNaN(netLiq) ? double.NaN : netLiq - snap.SessionStartBalance;
                    sb.Append("  Consistency ").Append(pctText).Append("%: ");
                    if (double.IsNaN(cap))
                        sb.Append("no cap yet (no profit banked)");
                    else
                        sb.Append("today ").Append(Money(today)).Append(" of ").Append(N2(cap)).Append(" allowed");
                    double best = snap.BestDay;
                    if (!double.IsNaN(today) && today > 0 && (double.IsNaN(best) || today > best))
                        best = today;
                    double share = Dtn.ProfitShare(best, double.IsNaN(netLiq) ? double.NaN : netLiq - startBalance);
                    if (!double.IsNaN(share))
                        sb.Append(Sep).Append("best day ").Append(N2(best)).Append(" = ").Append(Num((int)Math.Round(share * 100.0))).Append("% of total profit");
                    sb.Append("\n");
                    // An earlier day can already be too big: the cap above only limits today.
                    double cure = Dtn.ConsistencyCure(best, double.IsNaN(netLiq) ? double.NaN : netLiq - startBalance, consistencyPct,
                        AccountNumber("ConsistencyProfitTarget", name, cfgConsistencyTarget));
                    if (!double.IsNaN(cure) && cure > 0.005)
                        sb.Append("  ").Append(EmWarn).Append(" Over the limit: <b>").Append(N2(cure)).Append("</b> more total profit, earned on smaller days, brings the best day back to ")
                          .Append(pctText).Append("%\n");
                }
            }

            double inactiveLimit = InactivityLimitFor(name);
            if (!double.IsNaN(inactiveLimit))
            {
                DateTime lastTrade;
                bool known;
                lock (riskLock)
                    known = lastActivity.TryGetValue(name, out lastTrade);
                if (known)
                {
                    double remaining = Dtn.InactivityRemainingDays(lastTrade, DateTime.UtcNow, inactiveLimit);
                    sb.Append("  Last trade ").Append(FormatDuration(DateTime.UtcNow - lastTrade)).Append(" ago").Append(Sep).Append("<b>")
                      .Append(remaining > 0 ? DaysText(remaining) + " left" : DaysText(-remaining) + " past the limit").Append("</b> of ").Append(DaysText(inactiveLimit)).Append("\n");
                }
            }

            if (lk != null)
            {
                if (lk.IsHold)
                    sb.Append("  ").Append(EmLock).Append(" Hold after /flatten, ").Append(FormatDuration(lk.UntilUtc - DateTime.UtcNow)).Append(" left\n");
                else
                    sb.Append("  ").Append(EmLock).Append(" <b>LOCKED</b> ").Append(Ago(lk.SinceUtc)).Append(" (").Append(Html(lk.Reason)).Append("): /unlock\n");
            }
            if (snap.Unknown)
                sb.Append("  ").Append(EmWarn).Append(" <b>Risk UNKNOWN</b>: data stale or disconnected while holding a position\n");
            return sb.ToString();
        }

        private static string ModeLabel(string mode)
        {
            switch (mode)
            {
                case Dtn.ModeOff:      return "off";
                case Dtn.ModeStatic:   return "static drawdown";
                case Dtn.ModeEod:      return "end-of-day trailing";
                case Dtn.ModeIntraday: return "intraday trailing";
                default:               return "manual floor";
            }
        }

        private string RiskFeaturesText()
        {
            List<string> parts = new List<string>();
            bool floors;
            bool daily;
            lock (riskLock)
                floors = floorOverrides.Count > 0;
            floors |= AnyAccountSetting("RiskFloor") || AnyAccountSetting("DrawdownMode");
            daily = AnyAccountSetting("DailyLossLimit");
            string mode = Dtn.NormalizeMode(cfgDrawdownMode) ?? Dtn.ModeManual;
            if (mode == Dtn.ModeStatic || mode == Dtn.ModeEod || mode == Dtn.ModeIntraday)
                parts.Add(ModeLabel(mode) + " floor");
            else if (mode == Dtn.ModeManual && (floors || !double.IsNaN(cfgRiskFloor)))
                parts.Add("risk floor");
            else if (floors)
                parts.Add("risk floor");
            if (daily || !string.IsNullOrWhiteSpace(cfgDailyLossLimit))
                parts.Add("daily loss");
            if (!double.IsNaN(cfgConsistencyPercent) || AnyAccountSetting("ConsistencyPercent"))
                parts.Add("consistency");
            if (!double.IsNaN(cfgInactivityDays) || AnyAccountSetting("InactivityDays"))
                parts.Add("inactivity clock");
            if (parts.Count > 0 && (cfgRiskAutoFlatten || cfgDailyLossAutoFlatten || cfgConsistencyAutoFlatten || cfgUnknownFlattenSec > 0))
                parts.Add("auto-lock");
            return string.Join(" + ", parts.ToArray());
        }

        private string StaleHeldFeed(CbiAccount a)
        {
            if (cfgStaleDataSeconds <= 0)
                return null;
            Dictionary<string, int> nets = NetPositions(a);
            lock (feedLock)
            {
                foreach (string instrument in nets.Keys)
                {
                    FeedWatch w;
                    if (feeds.TryGetValue(instrument, out w) && w.Stale)
                        return instrument;
                }
            }
            return null;
        }

        // ---- state file: trailing high-water marks, daily baselines, session locks, /leader

        private static string StatePath()
        {
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigPath()), StateFileName);
        }

        private void LoadRiskState()
        {
            try
            {
                string path = StatePath();
                if (!File.Exists(path))
                    return;
                string session = CurrentSessionKey();
                lock (riskLock)
                {
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        string key = raw.Substring(0, eq).Trim();
                        string value = raw.Substring(eq + 1).Trim();
                        if (key == "leader")
                        {
                            leaderOverride = value;
                            continue;
                        }
                        int bar = key.IndexOf('|');
                        if (bar <= 0)
                            continue;
                        string field = key.Substring(0, bar);
                        string account = key.Substring(bar + 1);

                        if (field == "lock")
                        {
                            // session|sinceTicks|reason: only this session's locks survive a restart
                            string[] p = value.Split(new[] { '|' }, 3);
                            long ticks;
                            if (p.Length == 3 && p[0] == session && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                                accountLocks[account] = new AccountLock { Session = p[0], SinceUtc = new DateTime(ticks, DateTimeKind.Utc), Reason = p[2], LastEnforceUtc = DateTime.MinValue };
                            continue;
                        }
                        if (field == "act" || field == "assumed")
                        {
                            long actTicks;
                            if (field == "act" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out actTicks))
                                lastActivity[account] = new DateTime(actTicks, DateTimeKind.Utc);
                            else if (field == "assumed" && value == "1")
                                activityAssumed.Add(account);
                            continue;
                        }

                        DtnRiskState rs;
                        if (!riskStates.TryGetValue(account, out rs))
                        {
                            rs = new DtnRiskState();
                            riskStates[account] = rs;
                        }
                        switch (field)
                        {
                            case "hw":      rs.HighWater = ParseOptionalDouble(value); break;
                            case "trail":   rs.TrailFloor = ParseOptionalDouble(value); break;
                            case "start":   rs.SessionStart = ParseOptionalDouble(value); break;
                            case "sbal":    rs.SessionStartBalance = ParseOptionalDouble(value); break;
                            case "best":    rs.BestDay = ParseOptionalDouble(value); break;
                            case "session": rs.Session = value; break;
                            case "mid":     rs.BaselineMidSession = value == "1"; break;
                            case "last":    rs.LastNetLiq = ParseOptionalDouble(value); break;
                            case "bal":     rs.LastBalance = ParseOptionalDouble(value); break;
                            case "seen":
                                long seenTicks;
                                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seenTicks))
                                    rs.LastSeenUtc = new DateTime(seenTicks, DateTimeKind.Utc);
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("Loading state", ex);
            }
        }

        private void SaveRiskStateIfDue(DateTime now)
        {
            double since = (now - lastRiskSaveUtc).TotalSeconds;
            if ((riskStateDirty && since >= 5) || since >= 60)
                SaveRiskState();
        }

        private void SaveRiskState()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("# DirectTelegramNotifier state. Written automatically; delete it to reset trailing floors, daily baselines, activity clocks and locks.\r\n");
                lock (riskLock)
                {
                    riskStateDirty = false;
                    lastRiskSaveUtc = DateTime.UtcNow;
                    if (leaderOverride.Length > 0)
                        sb.Append("leader=").Append(leaderOverride).Append("\r\n");
                    foreach (KeyValuePair<string, DtnRiskState> kv in riskStates)
                    {
                        DtnRiskState rs = kv.Value;
                        AppendStateNumber(sb, "hw", kv.Key, rs.HighWater);
                        AppendStateNumber(sb, "trail", kv.Key, rs.TrailFloor);
                        AppendStateNumber(sb, "start", kv.Key, rs.SessionStart);
                        AppendStateNumber(sb, "sbal", kv.Key, rs.SessionStartBalance);
                        AppendStateNumber(sb, "best", kv.Key, rs.BestDay);
                        AppendStateNumber(sb, "last", kv.Key, rs.LastNetLiq);
                        AppendStateNumber(sb, "bal", kv.Key, rs.LastBalance);
                        if (rs.LastSeenUtc != DateTime.MinValue)
                            sb.Append("seen|").Append(kv.Key).Append('=').Append(rs.LastSeenUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                        if (rs.Session.Length > 0)
                            sb.Append("session|").Append(kv.Key).Append('=').Append(rs.Session).Append("\r\n");
                        if (rs.BaselineMidSession)
                            sb.Append("mid|").Append(kv.Key).Append("=1\r\n");
                    }
                    foreach (KeyValuePair<string, AccountLock> kv in accountLocks)
                    {
                        if (kv.Value.IsHold)
                            continue;
                        sb.Append("lock|").Append(kv.Key).Append('=').Append(kv.Value.Session).Append('|')
                          .Append(kv.Value.SinceUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|').Append(kv.Value.Reason).Append("\r\n");
                    }
                    foreach (KeyValuePair<string, DateTime> kv in lastActivity)
                        sb.Append("act|").Append(kv.Key).Append('=').Append(kv.Value.Ticks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                    foreach (string account in activityAssumed)
                        sb.Append("assumed|").Append(account).Append("=1\r\n");
                }

                // Write a temp file first so a crash mid-write can't leave a half-written state file.
                string path = StatePath();
                string temp = path + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                WriteThrottled("Saving state failed: " + Describe(ex));
            }
        }

        private static void AppendStateNumber(StringBuilder sb, string field, string account, double value)
        {
            if (!double.IsNaN(value))
                sb.Append(field).Append('|').Append(account).Append('=').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        }

        // ---- NinjaTrader process health ----

        private void CheckMachineHealth()
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    double cpuSeconds = p.TotalProcessorTime.TotalSeconds;
                    if (lastCpuSampleUtc != DateTime.MinValue)
                    {
                        double wall = (now - lastCpuSampleUtc).TotalSeconds;
                        if (wall >= 1.0)
                            lastCpuPercent = Math.Max(0.0, (cpuSeconds - lastCpuSeconds) / wall / Math.Max(1, Environment.ProcessorCount) * 100.0);
                    }
                    lastCpuSampleUtc = now;
                    lastCpuSeconds = cpuSeconds;
                    lastMemoryMb = (int)(p.WorkingSet64 / (1024 * 1024));
                }

                EvaluateCpu(lastCpuPercent, now);
                EvaluateMemory(lastMemoryMb, now);

                if ((now - lastDiskCheckUtc).TotalMinutes >= 5)
                {
                    lastDiskCheckUtc = now;
                    lastDiskFreeMb = DiskFreeMb(out lastDiskDrive);
                    EvaluateDisk(lastDiskDrive, lastDiskFreeMb, now);
                }
            }
            catch (Exception ex)
            {
                WriteThrottled("Machine health check failed: " + Describe(ex));
            }
        }

        private string lastDiskDrive = string.Empty;

        private void EvaluateCpu(double percent, DateTime now)
        {
            if (cfgCpuAlertPercent <= 0)
                return;
            if (percent < cfgCpuAlertPercent)
            {
                cpuHighSince = DateTime.MinValue;
                return;
            }
            if (cpuHighSince == DateTime.MinValue)
                cpuHighSince = now;
            if ((now - cpuHighSince).TotalSeconds >= cfgCpuAlertSeconds && (now - lastCpuAlertUtc).TotalMinutes >= 30)
            {
                lastCpuAlertUtc = now;
                EnqueueText(MsgCpuHigh(percent, now - cpuHighSince));
            }
        }

        private void EvaluateMemory(int memoryMb, DateTime now)
        {
            if (cfgMemoryAlertMb > 0 && memoryMb >= cfgMemoryAlertMb && (now - lastMemAlertUtc).TotalHours >= 6)
            {
                lastMemAlertUtc = now;
                EnqueueText(MsgMemoryHigh(memoryMb, cfgMemoryAlertMb));
            }
        }

        private void EvaluateDisk(string drive, long freeMb, DateTime now)
        {
            if (cfgDiskAlertMb > 0 && freeMb >= 0 && freeMb < cfgDiskAlertMb && (now - lastDiskAlertUtc).TotalHours >= 6)
            {
                lastDiskAlertUtc = now;
                EnqueueText(MsgDiskLow(drive, freeMb));
            }
        }

        private static long DiskFreeMb(out string drive)
        {
            drive = string.Empty;
            try
            {
                string root = System.IO.Path.GetPathRoot(System.IO.Path.GetDirectoryName(ConfigPath()));
                if (string.IsNullOrEmpty(root))
                    return -1;
                drive = root.TrimEnd('\\');
                return new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        #endregion
    }
}
