#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
#endregion

// ============================================================================================
//  DirectTelegramNotifier - pure logic, no NinjaTrader types.
//
//  Everything here is deterministic and unit-tested outside NinjaTrader (tests/CoreTests.cs):
//  config-key checks, risk levels, drawdown floors, daily loss, floor validation, quiet windows,
//  session keys, fill price comparison, command parsing and the JSON reader.
//  Written in C# 5 so it compiles in every NinjaTrader 8 version and with Windows' built-in csc.
// ============================================================================================

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class DirectTelegramNotifierCore
    {
        #region Config keys

        // Every key the add-on reads. Anything else in the config file is reported as unrecognized.
        public static readonly string[] KnownKeys = new string[]
        {
            "BotToken", "AuthorizedChatID", "AccountName",
            "EnableWatchdog", "EnableExecutions", "EnableRemoteCommands", "EnableOrderRejectAlerts",
            "RunawayMaxOrders", "RunawayWindowSec", "UseGlobalFlattenEverything",
            "EodReportTime", "EodTimeZoneId", "EstimatedCommissionPerSide",
            "HeartbeatUrl", "HeartbeatIntervalSec", "RestartGapThresholdSec",
            "EnableStrategyWatch", "StaleDataSeconds", "StaleDataSchedule", "StaleDataQuietWindows", "StaleDataTimeZone",
            "StaleDataInstruments", "EnableAlertLog",
            "LeaderAccount", "FollowerRatio", "MismatchSeconds",
            "ProtectiveCheckSeconds", "OrphanOrderSeconds", "OrphanOrderNames", "StuckOrderSeconds", "StuckOrderStates",
            "MaxPositionContracts",
            "DrawdownMode", "HighWaterBasis", "RiskFloor", "StartingBalance", "DrawdownAmount", "FloorLock",
            "RiskBufferAlerts", "RiskAutoFlatten", "RiskFlattenBuffer", "RiskConfirmSeconds", "FloorJumpConfirm",
            "UnknownFlattenSeconds", "LockGroup",
            "DailyLossLimit", "DailyLossAlerts", "DailyLossAutoFlatten",
            "ConsistencyPercent", "ConsistencyProfitTarget", "ConsistencyAlerts", "ConsistencyAutoFlatten",
            "InactivityDays", "InactivityAlerts",
            "EnableRiskLog", "BackupAlertUrl", "BackupAlertFormat", "BackupAlertDetail",
            "FlattenHoldSeconds", "FillGroupMs",
            "CpuAlertPercent", "CpuAlertSeconds", "MemoryAlertMB", "DiskAlertMB",
            "FlattenConfirm", "SilentRoutineAlerts", "EnableFillCsv"
        };

        // Keys that may also be given per account as  Key:AccountName=value
        public static readonly string[] PerAccountKeys = new string[]
        {
            "DrawdownMode", "HighWaterBasis", "RiskFloor", "StartingBalance", "DrawdownAmount", "FloorLock", "DailyLossLimit",
            "ConsistencyPercent", "ConsistencyProfitTarget", "InactivityDays"
        };

        public static bool IsKnownKey(string key)
        {
            foreach (string k in KnownKeys)
            {
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool IsPerAccountKey(string key)
        {
            foreach (string k in PerAccountKeys)
            {
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Returns one line per unrecognized key, with a suggestion when a known key is close: "RiskAutoFlaten (did you mean RiskAutoFlatten?)".
        public static List<string> FindUnknownKeys(IEnumerable<string> keys)
        {
            List<string> unknown = new List<string>();
            foreach (string raw in keys)
            {
                string key = (raw ?? string.Empty).Trim();
                if (key.Length == 0)
                    continue;

                int colon = key.IndexOf(':');
                string baseKey = colon > 0 ? key.Substring(0, colon) : key;
                if (colon > 0 ? IsPerAccountKey(baseKey) : IsKnownKey(baseKey))
                    continue;

                string candidates = colon > 0 ? string.Join("|", PerAccountKeys) : string.Join("|", KnownKeys);
                string best = null;
                int bestDistance = int.MaxValue;
                foreach (string k in candidates.Split('|'))
                {
                    int dist = Levenshtein(baseKey.ToLowerInvariant(), k.ToLowerInvariant());
                    if (dist < bestDistance)
                    {
                        bestDistance = dist;
                        best = k;
                    }
                }
                int allowed = Math.Max(2, baseKey.Length / 4);
                if (best != null && bestDistance <= allowed)
                    unknown.Add(key + " (did you mean " + (colon > 0 ? best + key.Substring(colon) : best) + "?)");
                else if (colon > 0 && IsKnownKey(baseKey))
                    unknown.Add(key + " (" + baseKey + " cannot be set per account)");
                else
                    unknown.Add(key);
            }
            return unknown;
        }

        public static int Levenshtein(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[b.Length];
        }

        #endregion

        #region Numbers

        public static double ParseOptionalDouble(string text)
        {
            double v;
            if (!string.IsNullOrWhiteSpace(text)
                && double.TryParse(text.Trim().TrimStart('$').Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return double.NaN;
        }

        // "1500, 750;300" -> ascending list of positive amounts.
        public static List<double> ParseDollarList(string text)
        {
            List<double> levels = new List<double>();
            if (string.IsNullOrWhiteSpace(text))
                return levels;
            foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v = ParseOptionalDouble(part);
                if (!double.IsNaN(v) && v > 0)
                    levels.Add(v);
            }
            levels.Sort();
            return levels;
        }

        // "1000" -> 1000 dollars;  "2%" -> 2 percent of baseValue.  Blank or invalid -> NaN.
        public static double ResolveAmountOrPercent(string text, double baseValue)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0)
                return double.NaN;
            if (t.EndsWith("%", StringComparison.Ordinal))
            {
                double pct = ParseOptionalDouble(t.Substring(0, t.Length - 1));
                if (double.IsNaN(pct) || pct <= 0 || double.IsNaN(baseValue))
                    return double.NaN;
                return baseValue * pct / 100.0;
            }
            double v = ParseOptionalDouble(t);
            return v > 0 ? v : double.NaN;
        }

        public static string N2(double value)
        {
            return value.ToString("N2", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Risk: floors, levels, daily loss

        public const string ModeOff = "off";
        public const string ModeManual = "manual";
        public const string ModeStatic = "static";
        public const string ModeEod = "eod";
        public const string ModeIntraday = "intraday";

        // Blank means manual. Returns null for anything unrecognized.
        public static string NormalizeMode(string text)
        {
            string t = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (t.Length == 0)
                return ModeManual;
            if (t == ModeOff || t == ModeManual || t == ModeStatic || t == ModeEod || t == ModeIntraday)
                return t;
            if (t == "endofday" || t == "end-of-day" || t == "eod-trailing" || t == "eodtrailing")
                return ModeEod;
            if (t == "intraday-trailing" || t == "intradaytrailing" || t == "realtime")
                return ModeIntraday;
            return null;
        }

        // The tightest alert level the buffer has reached: 0 = at or below the floor, +infinity = no level reached.
        public static double RiskLevel(double buffer, IList<double> ascendingLevels)
        {
            if (double.IsNaN(buffer))
                return double.PositiveInfinity;
            if (buffer <= 0)
                return 0.0;
            if (ascendingLevels != null)
            {
                foreach (double level in ascendingLevels)
                {
                    if (buffer <= level)
                        return level;
                }
            }
            return double.PositiveInfinity;
        }

        public static double NextHighWater(double highWater, double observed)
        {
            if (double.IsNaN(observed))
                return highWater;
            if (double.IsNaN(highWater))
                return observed;
            return Math.Max(highWater, observed);
        }

        // Static drawdown: the floor never moves.
        public static double StaticFloor(double startingBalance, double drawdown)
        {
            if (double.IsNaN(startingBalance) || double.IsNaN(drawdown) || drawdown <= 0)
                return double.NaN;
            return startingBalance - drawdown;
        }

        // Trailing drawdown (end-of-day or intraday, depending on which high-water mark is passed in):
        // floor = high-water - drawdown, capped at the lock level, and it never moves down.
        public static double TrailingFloor(double highWater, double drawdown, double lockLevel, double previousFloor)
        {
            if (double.IsNaN(highWater) || double.IsNaN(drawdown) || drawdown <= 0)
                return previousFloor;
            double candidate = highWater - drawdown;
            if (!double.IsNaN(lockLevel))
                candidate = Math.Min(candidate, lockLevel);
            if (double.IsNaN(previousFloor))
                return candidate;
            return Math.Max(previousFloor, candidate);
        }

        // Highest alert percentage the day's loss has reached: 0 = none, 100 = the limit itself.
        public static int DailyLossLevel(double loss, double limit, IList<double> percents)
        {
            if (double.IsNaN(loss) || double.IsNaN(limit) || limit <= 0 || loss <= 0)
                return 0;
            if (loss >= limit)
                return 100;
            int reached = 0;
            if (percents != null)
            {
                foreach (double p in percents)
                {
                    if (p > 0 && p < 100 && loss >= limit * p / 100.0)
                        reached = Math.Max(reached, (int)Math.Round(p));
                }
            }
            return reached;
        }

        // Why a new floor needs confirmation, or null when it looks sane.
        public static string ValidateFloor(double newFloor, double netLiq, double smallestLevel, double previousFloor, double jumpLimit)
        {
            List<string> reasons = new List<string>();
            if (!double.IsNaN(netLiq))
            {
                if (newFloor >= netLiq)
                    reasons.Add("it is at or above the current net liquidation (" + N2(netLiq) + ")");
                else if (newFloor >= netLiq - Math.Max(0.0, smallestLevel))
                    reasons.Add("it is only " + N2(netLiq - newFloor) + " below the current net liquidation (" + N2(netLiq) + "), inside your alert levels");
            }
            if (jumpLimit > 0 && !double.IsNaN(previousFloor) && Math.Abs(newFloor - previousFloor) > jumpLimit)
                reasons.Add("it moves the floor by " + N2(Math.Abs(newFloor - previousFloor)) + " (from " + N2(previousFloor) + ")");
            return reasons.Count == 0 ? null : string.Join("; ", reasons.ToArray());
        }

        // The session a moment belongs to, named by the date its end-of-day boundary falls on.
        public static string SessionKey(DateTime localInEodZone, TimeSpan eodTimeOfDay)
        {
            DateTime day = localInEodZone.TimeOfDay < eodTimeOfDay ? localInEodZone.Date : localInEodZone.Date.AddDays(1);
            return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Balance basis, consistency and inactivity

        public const string BasisBalance = "balance";
        public const string BasisNetLiq = "netliq";

        // Blank means balance. Returns null for anything unrecognized.
        public static string NormalizeBasis(string text)
        {
            string t = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (t.Length == 0 || t == BasisBalance || t == "closing" || t == "realized")
                return BasisBalance;
            if (t == BasisNetLiq || t == "netliquidation" || t == "equity")
                return BasisNetLiq;
            return null;
        }

        // The realized balance estimate: net liquidation without open profit or loss.
        public static double BalanceFromNetLiq(double netLiq, double unrealized)
        {
            if (double.IsNaN(netLiq) || double.IsInfinity(netLiq) || double.IsNaN(unrealized) || double.IsInfinity(unrealized))
                return double.NaN;
            return netLiq - unrealized;
        }

        // Most profit today can reach before today's profit is more than `percent` of the total profit.
        //   today <= p * (prior + today)   gives   today <= p * prior / (1 - p)
        // Firms that measure against the profit target use  max(total profit, target),  which also allows today <= p * target.
        // NaN when the rule is off or there is nothing to measure against yet.
        public static double ConsistencyCap(double priorProfit, double percent, double profitTarget)
        {
            if (double.IsNaN(percent) || percent <= 0 || percent >= 100)
                return double.NaN;
            double p = percent / 100.0;
            double fromProfit = (!double.IsNaN(priorProfit) && priorProfit > 0) ? p * priorProfit / (1.0 - p) : 0.0;
            double fromTarget = (!double.IsNaN(profitTarget) && profitTarget > 0) ? p * profitTarget : 0.0;
            double cap = Math.Max(fromProfit, fromTarget);
            return cap > 0 ? cap : double.NaN;
        }

        // part as a fraction of total; NaN when there is no profit to be a share of.
        public static double ProfitShare(double part, double total)
        {
            if (double.IsNaN(part) || double.IsNaN(total) || total <= 0)
                return double.NaN;
            return part / total;
        }

        // Days left before an inactivity limit; zero or negative once it has passed.
        public static double InactivityRemainingDays(DateTime lastActivityUtc, DateTime nowUtc, double limitDays)
        {
            if (double.IsNaN(limitDays) || limitDays <= 0)
                return double.NaN;
            return limitDays - (nowUtc - lastActivityUtc).TotalDays;
        }

        #endregion

        #region Risk state machine (pure: every NinjaTrader reading is passed in)

        // Which floor applies, and where it came from. Precedence: a /floor setting, then the mode's own floor, then RiskFloor.
        public static double EffectiveFloor(DtnRiskState rs, DtnRiskSettings s, out string source)
        {
            source = s.Mode;
            if (s.Mode == ModeOff)
                return double.NaN;
            if (!double.IsNaN(s.OverrideFloor))
            {
                source = "/floor";
                return s.OverrideFloor;
            }
            if (s.Mode == ModeEod || s.Mode == ModeIntraday)
                return rs == null ? double.NaN : rs.TrailFloor;
            if (s.Mode == ModeStatic)
                return StaticFloor(s.StartingBalance, s.Drawdown);
            source = "config";
            return s.ManualFloor;
        }

        // Profit still needed to bring the best day down to `percent` of total profit: best / p - total.
        // Zero when the rule is met, NaN when it is off. With a profit target the rule is best <= p * max(total, target).
        public static double ConsistencyCure(double bestDay, double totalProfit, double percent, double profitTarget)
        {
            if (double.IsNaN(percent) || percent <= 0 || percent >= 100 || double.IsNaN(bestDay) || double.IsNaN(totalProfit))
                return double.NaN;
            if (bestDay <= 0)
                return 0.0;
            double p = percent / 100.0;
            if (!double.IsNaN(profitTarget) && profitTarget > 0 && bestDay <= p * profitTarget)
                return 0.0;
            double needed = bestDay / p - totalProfit;
            return needed > 0 ? needed : 0.0;
        }

        private static string Whole(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // One tick of per-account risk bookkeeping. Changes `rs` and reports what happened; sends and flattens nothing.
        // The add-on calls it once a second with readings from NinjaTrader and acts on the result.
        public static DtnRiskResult EvaluateRisk(DtnRiskState rs, DtnRiskInput inp)
        {
            DtnRiskSettings s = inp.Settings;
            DtnRiskResult res = new DtnRiskResult();
            DateTime now = inp.Now;
            double netLiq = inp.NetLiq;
            double balance = inp.Balance;
            string mode = s.Mode;
            string basis = s.Basis;
            bool unknown = inp.UnknownWhy != null;
            bool consistencyOn = !double.IsNaN(s.ConsistencyPercent) && s.ConsistencyPercent > 0 && s.ConsistencyPercent < 100;
            bool sessionLocked = inp.LockedSession != null;

            // ---- can the numbers be trusted? Alert once, remind every minute, and optionally flatten.
            if (unknown && !rs.Unknown)
            {
                rs.Unknown = true;
                rs.UnknownSince = now;
                rs.LastUnknownAlertUtc = now;
                res.Add(DtnRiskEventKind.Unknown, true, 0, 0, 0, 0, s.UnknownFlattenSeconds, false, inp.UnknownWhy);
            }
            else if (unknown && (now - rs.LastUnknownAlertUtc).TotalSeconds >= s.UnknownRemindSeconds)
            {
                rs.LastUnknownAlertUtc = now;
                res.Add(DtnRiskEventKind.Unknown, true, (now - rs.UnknownSince).TotalSeconds, 0, 0, 0, s.UnknownFlattenSeconds, false, inp.UnknownWhy);
            }
            else if (!unknown && rs.Unknown)
            {
                rs.Unknown = false;
                rs.UnknownSince = DateTime.MinValue;
                res.Add(DtnRiskEventKind.DataBack, false, 0, 0, 0, 0, 0, false, null);
            }

            // Orders do not need market data, so flattening still works when the data is gone.
            if (unknown && s.UnknownFlattenSeconds > 0 && !sessionLocked && (now - rs.UnknownSince).TotalSeconds >= s.UnknownFlattenSeconds)
            {
                res.FireReason = "risk data unavailable for " + Whole(s.UnknownFlattenSeconds) + "s while holding a position (" + inp.UnknownWhy + ")";
                return res;
            }

            if (double.IsNaN(netLiq))
                return res;

            // ---- session rollover: closing balance, best day, daily-loss and consistency baselines, session locks
            if (rs.Session != inp.Session)
            {
                bool firstSight = rs.Session.Length == 0;
                if (!firstSight)
                {
                    // The last reading is the closing value, unless nobody was watching across the boundary and the
                    // account has changed since: then it is unknown, and guessing could move a floor wrongly.
                    bool gap = rs.LastSeenUtc == DateTime.MinValue || (now - rs.LastSeenUtc).TotalMinutes > s.UnmonitoredGapMinutes;
                    bool balanceOk = !double.IsNaN(rs.LastBalance)
                        && !(gap && (double.IsNaN(balance) || Math.Abs(balance - rs.LastBalance) > s.ClosingTolerance));
                    bool netLiqOk = !double.IsNaN(rs.LastNetLiq) && !(gap && Math.Abs(netLiq - rs.LastNetLiq) > s.ClosingTolerance);
                    bool onBalance = basis == BasisBalance;
                    bool closeOk = onBalance ? balanceOk : netLiqOk;
                    double closeValue = onBalance ? rs.LastBalance : rs.LastNetLiq;

                    if (mode == ModeEod && closeOk)
                    {
                        double before = rs.TrailFloor;
                        rs.HighWater = NextHighWater(rs.HighWater, closeValue);
                        rs.TrailFloor = TrailingFloor(rs.HighWater, s.Drawdown, s.FloorLock, rs.TrailFloor);
                        if (!double.IsNaN(rs.TrailFloor) && !(rs.TrailFloor == before))
                            res.Add(DtnRiskEventKind.FloorTrailed, false, before, rs.TrailFloor, rs.HighWater, 0, 0, false, null);
                    }
                    if (balanceOk && !double.IsNaN(rs.SessionStartBalance))
                    {
                        double dayProfit = rs.LastBalance - rs.SessionStartBalance;
                        if (dayProfit > 0 && (double.IsNaN(rs.BestDay) || dayProfit > rs.BestDay))
                            rs.BestDay = dayProfit;
                    }
                    if ((mode == ModeEod && !closeOk) || (consistencyOn && !balanceOk))
                        res.Add(DtnRiskEventKind.ClosingUncertain, true, onBalance ? rs.LastBalance : rs.LastNetLiq, onBalance ? balance : netLiq, 0, 0, 0, false, null);
                }

                rs.Session = inp.Session;
                rs.SessionStart = netLiq;
                rs.SessionStartBalance = double.IsNaN(balance) ? netLiq : balance;
                rs.BaselineMidSession = firstSight;
                rs.DailyLevel = 0;
                rs.ConsistencyLevel = 0;
                rs.DailyBreachSince = DateTime.MinValue;
                rs.ConsistencyBreachSince = DateTime.MinValue;

                if (inp.LockedSession != null && inp.LockedSession != inp.Session)
                {
                    res.ReleaseSessionLock = true;
                    sessionLocked = false;
                    res.Add(DtnRiskEventKind.LockReleased, false, 0, 0, 0, 0, 0, false, null);
                }
                res.Dirty = true;
            }
            if (double.IsNaN(rs.SessionStartBalance))
                rs.SessionStartBalance = double.IsNaN(balance) ? netLiq : balance;      // state saved by an older version

            // ---- trailing modes: high-water mark and computed floor
            if (mode == ModeEod || mode == ModeIntraday)
            {
                if (double.IsNaN(rs.HighWater))
                {
                    double nowValue = (mode == ModeEod && basis == BasisBalance && !double.IsNaN(balance)) ? balance : netLiq;
                    bool fromStart = !double.IsNaN(s.StartingBalance);
                    rs.HighWater = fromStart ? s.StartingBalance : nowValue;
                    res.Dirty = true;
                    // Starting from StartingBalance is only right for an account that has not banked profit yet: say so.
                    if (!unknown && (!fromStart || nowValue - s.StartingBalance > Math.Max(50.0, s.StartingBalance * 0.001)))
                        res.Add(DtnRiskEventKind.HighWaterStarted, fromStart, rs.HighWater, nowValue, 0, 0, 0, fromStart, null);
                }
                if (mode == ModeIntraday && !unknown && netLiq > rs.HighWater)
                {
                    rs.HighWater = netLiq;
                    res.Dirty = true;
                }
                double trail = TrailingFloor(rs.HighWater, s.Drawdown, s.FloorLock, rs.TrailFloor);
                if (!(trail == rs.TrailFloor) && !(double.IsNaN(trail) && double.IsNaN(rs.TrailFloor)))
                {
                    rs.TrailFloor = trail;
                    res.Dirty = true;
                }
            }

            if (unknown)
            {
                // Frozen numbers: no level changes and no automatic action until the data is back.
                rs.FloorBreachSince = DateTime.MinValue;
                rs.DailyBreachSince = DateTime.MinValue;
                rs.ConsistencyBreachSince = DateTime.MinValue;
                return res;
            }
            rs.LastNetLiq = netLiq;
            if (!double.IsNaN(balance))
                rs.LastBalance = balance;
            rs.LastSeenUtc = now;

            // ---- floor
            string source;
            double floor = EffectiveFloor(rs, s, out source);
            if (!double.IsNaN(floor))
            {
                double buffer = netLiq - floor;
                double level = RiskLevel(buffer, s.RiskBuffers);
                double previous = rs.FloorLevel;
                rs.FloorLevel = level;
                if (level < previous)
                    res.Add(DtnRiskEventKind.RiskBuffer, true, netLiq, floor, buffer, level, 0, false, source);
                else if (double.IsPositiveInfinity(level) && !double.IsPositiveInfinity(previous))
                    res.Add(DtnRiskEventKind.RiskRecovered, false, buffer, 0, 0, 0, 0, false, null);

                if (s.RiskAutoFlatten && buffer <= s.FlattenBuffer && !sessionLocked)
                {
                    if (rs.FloorBreachSince == DateTime.MinValue)
                        rs.FloorBreachSince = now;
                    else if ((now - rs.FloorBreachSince).TotalSeconds >= s.ConfirmSeconds)
                        res.FireReason = "net liq " + N2(netLiq) + " is " + (buffer > 0 ? "only " + N2(buffer) + " above" : N2(-buffer) + " below")
                                       + " the floor " + N2(floor);
                }
                else
                {
                    rs.FloorBreachSince = DateTime.MinValue;
                }
            }
            else
            {
                rs.FloorLevel = double.PositiveInfinity;
                rs.FloorBreachSince = DateTime.MinValue;
            }

            // ---- daily loss
            double limit = ResolveAmountOrPercent(s.DailyLimitText, rs.SessionStart);
            if (!double.IsNaN(limit) && !double.IsNaN(rs.SessionStart))
            {
                double loss = rs.SessionStart - netLiq;
                int pct = DailyLossLevel(loss, limit, s.DailyLossAlerts);
                if (pct > rs.DailyLevel)
                {
                    rs.DailyLevel = pct;
                    res.Add(DtnRiskEventKind.DailyLoss, true, loss, limit, 0, 0, pct, false, null);
                }
                else if (pct == 0 && rs.DailyLevel > 0)
                {
                    rs.DailyLevel = 0;
                    res.Add(DtnRiskEventKind.DailyLossBack, false, loss, limit, 0, 0, 0, false, null);
                }

                if (s.DailyLossAutoFlatten && pct >= 100 && !sessionLocked && res.FireReason == null)
                {
                    if (rs.DailyBreachSince == DateTime.MinValue)
                        rs.DailyBreachSince = now;
                    else if ((now - rs.DailyBreachSince).TotalSeconds >= s.ConfirmSeconds)
                        res.FireReason = "daily loss limit: down " + N2(loss) + " today (limit " + N2(limit) + ")";
                }
                else if (pct < 100)
                {
                    rs.DailyBreachSince = DateTime.MinValue;
                }
            }

            // ---- consistency rule: how much more profit today can add before it is too large a share of the total
            if (consistencyOn && !double.IsNaN(rs.SessionStartBalance) && !double.IsNaN(s.StartingBalance))
            {
                double cap = ConsistencyCap(rs.SessionStartBalance - s.StartingBalance, s.ConsistencyPercent, s.ConsistencyTarget);
                double today = netLiq - rs.SessionStartBalance;
                int clevel = DailyLossLevel(today, cap, s.ConsistencyAlerts);
                if (clevel > rs.ConsistencyLevel)
                {
                    rs.ConsistencyLevel = clevel;
                    res.Add(DtnRiskEventKind.Consistency, true, today, cap, s.ConsistencyPercent, 0, clevel, false, null);
                }
                else if (clevel == 0 && rs.ConsistencyLevel > 0)
                {
                    rs.ConsistencyLevel = 0;
                    res.Add(DtnRiskEventKind.ConsistencyBack, false, today, cap, 0, 0, 0, false, null);
                }

                if (s.ConsistencyAutoFlatten && clevel >= 100 && !sessionLocked && res.FireReason == null)
                {
                    if (rs.ConsistencyBreachSince == DateTime.MinValue)
                        rs.ConsistencyBreachSince = now;
                    else if ((now - rs.ConsistencyBreachSince).TotalSeconds >= s.ConfirmSeconds)
                        res.FireReason = "consistency rule: today's profit " + N2(today) + " reached the cap " + N2(cap);
                }
                else if (clevel < 100)
                {
                    rs.ConsistencyBreachSince = DateTime.MinValue;
                }
            }
            return res;
        }

        #endregion

        #region Readiness, risk log and replay

        // "MFFUEVREOD729960001" -> "...001": enough to know which account, little to identify it to anyone else.
        public static string MaskAccount(string name)
        {
            string n = (name ?? string.Empty).Trim();
            return n.Length <= 3 ? n : "..." + n.Substring(n.Length - 3);
        }

        // What the second alert channel carries for a risk event: which kind of trouble and where to look, and no amounts.
        // The push service is a third party; the full detail stays in Telegram (BackupAlertDetail=full sends the whole alert instead).
        public static string BackupMinimalText(DtnRiskEvent e, string maskedAccount)
        {
            string account = "account " + maskedAccount;
            switch (e.Kind)
            {
                case DtnRiskEventKind.Unknown:
                    return "RISK UNKNOWN: " + account + " holds a position and its risk data is unavailable. Open NinjaTrader now.";
                case DtnRiskEventKind.RiskBuffer:
                    return (e.D <= 0 ? "BELOW FLOOR: " : "NEAR FLOOR: ") + account + ". Open NinjaTrader now.";
                case DtnRiskEventKind.DailyLoss:
                    return "DAILY LOSS LIMIT reached: " + account + ". Open NinjaTrader now.";
                default:
                    return "ALERT: " + account + ". Open NinjaTrader now.";
            }
        }

        // Events worth waking you through a second channel: the first Risk UNKNOWN, the tightest floor level (or below it), the daily limit.
        public static bool IsCriticalEvent(DtnRiskEvent e, DtnRiskSettings s)
        {
            switch (e.Kind)
            {
                case DtnRiskEventKind.Unknown:
                    return e.A <= 0;
                case DtnRiskEventKind.RiskBuffer:
                    double tightest = 0.0;
                    if (s.RiskBuffers != null && s.RiskBuffers.Count > 0)
                    {
                        tightest = double.MaxValue;
                        foreach (double level in s.RiskBuffers)
                            if (level < tightest)
                                tightest = level;
                    }
                    return e.D <= tightest;
                case DtnRiskEventKind.DailyLoss:
                    return e.N >= 100;
                default:
                    return false;
            }
        }

        // A send Telegram refused for good: wrong token, blocked bot, wrong or unreachable chat. These do not fix themselves, so the
        // heartbeat must not claim alerts are being delivered. Message-specific errors (too long, bad markup) are not in this group.
        public static bool IsFatalSendError(int code, string description)
        {
            string d = (description ?? string.Empty).ToLowerInvariant();
            if (code == 401 || code == 403 || code == 404)
                return true;
            if (code == 400)
                return d.Contains("chat not found") || d.Contains("peer_id_invalid") || d.Contains("user is deactivated") || d.Contains("bot was blocked")
                    || d.Contains("have no rights") || d.Contains("group chat was upgraded") || d.Contains("not a member");
            return false;
        }

        // Why an account's automatic protection would not act (or would act on guesses). Empty = nothing wrong found.
        public static List<string> ArmingProblems(DtnRiskSettings s, bool netLiqReadable, bool openPnlReadable, double floor)
        {
            List<string> problems = new List<string>();
            if (!netLiqReadable)
                problems.Add("net liquidation cannot be read");
            if (s.Mode == ModeEod && s.Basis == BasisBalance && !openPnlReadable)
                problems.Add("open P&L cannot be read, so the closing balance is unknown");
            bool needsDrawdown = s.Mode == ModeStatic || s.Mode == ModeEod || s.Mode == ModeIntraday;
            if (needsDrawdown && (double.IsNaN(s.Drawdown) || s.Drawdown <= 0))
                problems.Add("DrawdownMode=" + s.Mode + " but DrawdownAmount is not set");
            if (s.Mode == ModeStatic && double.IsNaN(s.StartingBalance))
                problems.Add("DrawdownMode=static but StartingBalance is not set");
            if (s.RiskAutoFlatten && double.IsNaN(floor))
                problems.Add("RiskAutoFlatten is on but there is no floor");
            if (s.DailyLossAutoFlatten && string.IsNullOrWhiteSpace(s.DailyLimitText))
                problems.Add("DailyLossAutoFlatten is on but there is no DailyLossLimit");
            bool consistencyOn = !double.IsNaN(s.ConsistencyPercent) && s.ConsistencyPercent > 0 && s.ConsistencyPercent < 100;
            if (consistencyOn && double.IsNaN(s.StartingBalance))
                problems.Add("ConsistencyPercent is set but StartingBalance is not");
            if (s.ConsistencyAutoFlatten && !consistencyOn)
                problems.Add("ConsistencyAutoFlatten is on but ConsistencyPercent is not set");
            return problems;
        }

        // ---- the risk log: every reading the risk check was given, so a real session can be replayed through EvaluateRisk.
        //   S,<utc>,<account>,<settings>                               the account's rules (written at the start and when they change)
        //   R,<utc>,<account>,<session>,<netliq>,<balance>,<unknown>,<lockedSession>      one reading
        // Numbers are written with the invariant culture; an empty number is "not available".

        private static string Cell(string text)
        {
            return (text ?? string.Empty).Replace(',', ' ').Replace(';', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string NumCell(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? string.Empty : v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string ListCell(IList<double> list)
        {
            List<string> parts = new List<string>();
            if (list != null)
                foreach (double d in list)
                    parts.Add(d.ToString("R", CultureInfo.InvariantCulture));
            return string.Join("|", parts.ToArray());
        }

        private static List<double> ParseListCell(string text)
        {
            List<double> list = new List<double>();
            foreach (string part in (text ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v = ParseOptionalDouble(part);
                if (!double.IsNaN(v))
                    list.Add(v);
            }
            return list;
        }

        public static string SettingsToText(DtnRiskSettings s)
        {
            string[] items = new string[]
            {
                "mode=" + Cell(s.Mode), "basis=" + Cell(s.Basis), "drawdown=" + NumCell(s.Drawdown), "lock=" + NumCell(s.FloorLock),
                "start=" + NumCell(s.StartingBalance), "override=" + NumCell(s.OverrideFloor), "manual=" + NumCell(s.ManualFloor),
                "cpct=" + NumCell(s.ConsistencyPercent), "ctarget=" + NumCell(s.ConsistencyTarget), "daily=" + Cell(s.DailyLimitText),
                "buffers=" + ListCell(s.RiskBuffers), "dalerts=" + ListCell(s.DailyLossAlerts), "calerts=" + ListCell(s.ConsistencyAlerts),
                "raf=" + (s.RiskAutoFlatten ? "1" : "0"), "fbuf=" + NumCell(s.FlattenBuffer), "daf=" + (s.DailyLossAutoFlatten ? "1" : "0"),
                "caf=" + (s.ConsistencyAutoFlatten ? "1" : "0"), "confirm=" + Whole(s.ConfirmSeconds), "unkflat=" + Whole(s.UnknownFlattenSeconds),
                "remind=" + Whole(s.UnknownRemindSeconds), "gap=" + Whole(s.UnmonitoredGapMinutes), "tol=" + NumCell(s.ClosingTolerance)
            };
            return string.Join(";", items);
        }

        public static DtnRiskSettings SettingsFromText(string text)
        {
            DtnRiskSettings s = new DtnRiskSettings();
            foreach (string item in (text ?? string.Empty).Split(';'))
            {
                int eq = item.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = item.Substring(0, eq).Trim();
                string v = item.Substring(eq + 1).Trim();
                double d = ParseOptionalDouble(v);
                int n;
                bool isInt = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                switch (key)
                {
                    case "mode": s.Mode = v; break;
                    case "basis": s.Basis = v; break;
                    case "drawdown": s.Drawdown = d; break;
                    case "lock": s.FloorLock = d; break;
                    case "start": s.StartingBalance = d; break;
                    case "override": s.OverrideFloor = d; break;
                    case "manual": s.ManualFloor = d; break;
                    case "cpct": s.ConsistencyPercent = d; break;
                    case "ctarget": s.ConsistencyTarget = d; break;
                    case "daily": s.DailyLimitText = v; break;
                    case "buffers": s.RiskBuffers = ParseListCell(v); break;
                    case "dalerts": s.DailyLossAlerts = ParseListCell(v); break;
                    case "calerts": s.ConsistencyAlerts = ParseListCell(v); break;
                    case "raf": s.RiskAutoFlatten = v == "1"; break;
                    case "fbuf": if (!double.IsNaN(d)) s.FlattenBuffer = d; break;
                    case "daf": s.DailyLossAutoFlatten = v == "1"; break;
                    case "caf": s.ConsistencyAutoFlatten = v == "1"; break;
                    case "confirm": if (isInt) s.ConfirmSeconds = n; break;
                    case "unkflat": if (isInt) s.UnknownFlattenSeconds = n; break;
                    case "remind": if (isInt) s.UnknownRemindSeconds = n; break;
                    case "gap": if (isInt) s.UnmonitoredGapMinutes = n; break;
                    case "tol": if (!double.IsNaN(d)) s.ClosingTolerance = d; break;
                }
            }
            return s;
        }

        private const string LogTimeFormat = "yyyy-MM-ddTHH:mm:ss.fff";

        public static string FormatSettingsRow(DateTime utc, string account, DtnRiskSettings s)
        {
            return "S," + utc.ToString(LogTimeFormat, CultureInfo.InvariantCulture) + "," + Cell(account) + "," + SettingsToText(s);
        }

        public static string FormatReadingRow(DateTime utc, string account, DtnRiskInput inp)
        {
            return "R," + utc.ToString(LogTimeFormat, CultureInfo.InvariantCulture) + "," + Cell(account) + "," + Cell(inp.Session) + ","
                + NumCell(inp.NetLiq) + "," + NumCell(inp.Balance) + "," + Cell(inp.UnknownWhy) + "," + Cell(inp.LockedSession);
        }

        // Null for comments, blank lines and anything that is not a valid row.
        public static DtnLogRow ParseLogRow(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                return null;
            string[] f = line.TrimEnd('\r', '\n').Split(new[] { ',' }, 4);
            DateTime utc;
            if (f.Length < 4 || (f[0] != "S" && f[0] != "R")
                || !DateTime.TryParseExact(f[1], LogTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
                return null;

            DtnLogRow row = new DtnLogRow { IsSettings = f[0] == "S", Utc = utc, Account = f[2] };
            if (row.IsSettings)
            {
                row.Settings = SettingsFromText(f[3]);
                return row;
            }
            string[] r = f[3].Split(new[] { ',' }, 5);
            if (r.Length < 5)
                return null;
            row.Input = new DtnRiskInput
            {
                Now = utc, Session = r[0], NetLiq = ParseOptionalDouble(r[1]), Balance = ParseOptionalDouble(r[2]),
                UnknownWhy = r[3].Length == 0 ? null : r[3], LockedSession = r[4].Length == 0 ? null : r[4]
            };
            return row;
        }

        private static string Stamp(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string EventLine(DateTime utc, string account, DtnRiskEvent e)
        {
            return Stamp(utc) + " " + account + " " + e.Kind + (e.Loud ? " loud" : " quiet")
                + " A=" + N2(e.A) + " B=" + N2(e.B) + " C=" + N2(e.C) + " D=" + N2(e.D) + " N=" + Whole(e.N) + (e.Flag ? " flag" : string.Empty)
                + (string.IsNullOrEmpty(e.Text) ? string.Empty : " " + e.Text);
        }

        // Feeds a recorded risk log back through EvaluateRisk, one tick a second while a reading holds (as the live check ticks),
        // and returns the timeline. A gap of more than two minutes means NinjaTrader was not running: no ticks are invented.
        public static List<string> ReplayRiskLog(IEnumerable<string> lines)
        {
            List<DtnLogRow> rows = new List<DtnLogRow>();
            foreach (string line in lines)
            {
                DtnLogRow row = ParseLogRow(line);
                if (row != null)
                    rows.Add(row);
            }

            List<string> output = new List<string>();
            Dictionary<string, DtnRiskState> states = new Dictionary<string, DtnRiskState>();
            Dictionary<string, DtnRiskSettings> settings = new Dictionary<string, DtnRiskSettings>();
            Dictionary<string, DtnRiskInput> held = new Dictionary<string, DtnRiskInput>();
            List<string> order = new List<string>();

            for (int i = 0; i < rows.Count; i++)
            {
                DtnLogRow row = rows[i];
                if (!states.ContainsKey(row.Account))
                {
                    states[row.Account] = new DtnRiskState();
                    settings[row.Account] = new DtnRiskSettings();
                    order.Add(row.Account);
                }
                if (row.IsSettings)
                {
                    settings[row.Account] = row.Settings;
                    continue;
                }

                row.Input.Settings = settings[row.Account];
                held[row.Account] = row.Input;
                ReplayTick(output, row.Account, states[row.Account], row.Input, row.Utc);

                int next = -1;
                for (int j = i + 1; j < rows.Count; j++)
                    if (!rows[j].IsSettings && rows[j].Account == row.Account) { next = j; break; }
                if (next < 0)
                    continue;
                double gap = (rows[next].Utc - row.Utc).TotalSeconds;
                if (gap > 120)
                    continue;
                for (int sec = 1; sec < (int)gap; sec++)
                {
                    DtnRiskInput hold = new DtnRiskInput
                    {
                        Now = row.Utc.AddSeconds(sec), Session = row.Input.Session, NetLiq = row.Input.NetLiq, Balance = row.Input.Balance,
                        UnknownWhy = row.Input.UnknownWhy, LockedSession = row.Input.LockedSession, Settings = settings[row.Account]
                    };
                    ReplayTick(output, row.Account, states[row.Account], hold, hold.Now);
                }
            }

            foreach (string account in order)
            {
                DtnRiskState rs = states[account];
                output.Add("END " + account + " session=" + rs.Session + " highWater=" + N2(rs.HighWater) + " trailFloor=" + N2(rs.TrailFloor)
                    + " bestDay=" + N2(rs.BestDay) + " unknown=" + (rs.Unknown ? "yes" : "no"));
            }
            return output;
        }

        private static void ReplayTick(List<string> output, string account, DtnRiskState rs, DtnRiskInput inp, DateTime utc)
        {
            DtnRiskResult result = EvaluateRisk(rs, inp);
            foreach (DtnRiskEvent e in result.Events)
                output.Add(EventLine(utc, account, e));
            if (result.ReleaseSessionLock)
                output.Add(Stamp(utc) + " " + account + " RELEASE-LOCK");
            if (result.FireReason != null)
                output.Add(Stamp(utc) + " " + account + " FIRE " + result.FireReason);
        }

        #endregion

        #region Quiet windows (market schedules)

        public sealed class QuietWindow
        {
            public readonly bool[] Days = new bool[7];        // indexed by DayOfWeek
            public TimeSpan Start;
            public TimeSpan End;
        }

        // CME equity-index style week, in US Eastern time.
        public const string CmeQuietWindows = "Mon-Thu 17:00-18:00; Fri 17:00-24:00; Sat 00:00-24:00; Sun 00:00-18:00";

        private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        // "Mon-Thu 17:00-18:00; Fri 17:00-24:00; Daily 09:00-09:05". Windows may not cross midnight: split them.
        public static List<QuietWindow> ParseQuietWindows(string spec, out string error)
        {
            error = null;
            List<QuietWindow> windows = new List<QuietWindow>();
            if (string.IsNullOrWhiteSpace(spec))
                return windows;

            foreach (string rawEntry in spec.Split(';'))
            {
                string entry = rawEntry.Trim();
                if (entry.Length == 0)
                    continue;

                string[] parts = entry.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    error = "'" + entry + "' should look like 'Mon-Thu 17:00-18:00'";
                    return new List<QuietWindow>();
                }

                QuietWindow w = new QuietWindow();
                if (!ParseDays(parts[0], w.Days))
                {
                    error = "unknown days '" + parts[0] + "' in '" + entry + "'";
                    return new List<QuietWindow>();
                }

                string[] times = parts[1].Split('-');
                TimeSpan start, end;
                if (times.Length != 2 || !ParseClock(times[0], out start) || !ParseClock(times[1], out end))
                {
                    error = "bad times '" + parts[1] + "' in '" + entry + "'";
                    return new List<QuietWindow>();
                }
                if (end <= start)
                {
                    error = "'" + entry + "' crosses midnight or is empty: split it into two windows";
                    return new List<QuietWindow>();
                }
                w.Start = start;
                w.End = end;
                windows.Add(w);
            }
            return windows;
        }

        public static bool InQuietWindow(DateTime local, IList<QuietWindow> windows)
        {
            if (windows == null)
                return false;
            int day = (int)local.DayOfWeek;
            TimeSpan t = local.TimeOfDay;
            foreach (QuietWindow w in windows)
            {
                if (w.Days[day] && t >= w.Start && t < w.End)
                    return true;
            }
            return false;
        }

        private static bool ParseDays(string text, bool[] days)
        {
            string t = text.Trim().ToLowerInvariant();
            if (t == "daily" || t == "everyday")
            {
                for (int i = 0; i < 7; i++)
                    days[i] = true;
                return true;
            }
            if (t == "weekdays")
                t = "mon-fri";
            if (t == "weekends")
                t = "sat,sun";

            foreach (string piece in t.Split(','))
            {
                string[] range = piece.Split('-');
                int from = Array.IndexOf(DayNames, range[0].Trim());
                int to = range.Length == 2 ? Array.IndexOf(DayNames, range[1].Trim()) : from;
                if (from < 0 || to < 0 || range.Length > 2)
                    return false;
                int d = from;
                while (true)
                {
                    days[d] = true;
                    if (d == to)
                        break;
                    d = (d + 1) % 7;
                }
            }
            return true;
        }

        private static bool ParseClock(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            string[] hm = text.Trim().Split(':');
            int h, m;
            if (hm.Length != 2 || !int.TryParse(hm[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
                || !int.TryParse(hm[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
                return false;
            if (h < 0 || m < 0 || m > 59 || h > 24 || (h == 24 && m != 0))
                return false;
            value = new TimeSpan(h, m, 0);
            return true;
        }

        #endregion

        #region Fills and commands

        // Positive = the follower did worse than the leader (paid more on a buy, received less on a sell).
        public static double TickDifference(bool isBuy, double leaderAvg, double followerAvg, double tick)
        {
            return (isBuy ? followerAvg - leaderAvg : leaderAvg - followerAvg) / tick;
        }

        public static string DescribeTicks(double ticks)
        {
            double magnitude = Math.Round(Math.Abs(ticks), 1);
            if (magnitude < 0.1)
                return "same price";
            return magnitude.ToString("0.#", CultureInfo.InvariantCulture) + (magnitude == 1.0 ? " tick " : " ticks ") + (ticks > 0 ? "worse" : "better");
        }

        // Hides internal copier order ids such as "73-007F6E72"; keeps readable names like "Entry" or "B27_Long".
        public static string CleanOrderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            string n = name.Trim();
            if (n.Length >= 8)
            {
                bool internalId = true;
                foreach (char c in n)
                {
                    if (!(Uri.IsHexDigit(c) || c == '-'))
                    {
                        internalId = false;
                        break;
                    }
                }
                if (internalId)
                    return string.Empty;
            }
            return n;
        }

        public static string ParseCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            string t = text.Trim();
            if (!t.StartsWith("/", StringComparison.Ordinal))
                return null;
            int space = t.IndexOfAny(new[] { ' ', '\n', '\t', '\r' });
            if (space > 0)
                t = t.Substring(0, space);
            int at = t.IndexOf('@');
            if (at > 0)
                t = t.Substring(0, at);                         // "/status@MyBot" -> "/status"
            return t.ToLowerInvariant();
        }

        public static string ParseArgument(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            string t = text.Trim();
            int space = t.IndexOfAny(new[] { ' ', '\n', '\t', '\r' });
            return space > 0 ? t.Substring(space + 1).Trim() : string.Empty;
        }

        #endregion

        #region JSON reader

        public static object ParseJson(string json)
        {
            return Json.Parse(json);
        }

        private static class Json
        {
            public static object Parse(string json)
            {
                int i = 0;
                object value = ParseValue(json, ref i);
                SkipWhitespace(json, ref i);
                return value;
            }

            private static void SkipWhitespace(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
            }

            private static object ParseValue(string s, ref int i)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                    throw new FormatException("Unexpected end of JSON.");

                switch (s[i])
                {
                    case '{': return ParseObject(s, ref i);
                    case '[': return ParseArray(s, ref i);
                    case '"': return ParseString(s, ref i);
                    case 't': ExpectLiteral(s, ref i, "true");  return true;
                    case 'f': ExpectLiteral(s, ref i, "false"); return false;
                    case 'n': ExpectLiteral(s, ref i, "null");  return null;
                    default:  return ParseNumber(s, ref i);
                }
            }

            private static void ExpectLiteral(string s, ref int i, string literal)
            {
                if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                    throw new FormatException("Invalid JSON literal at position " + i + ".");
                i += literal.Length;
            }

            private static Dictionary<string, object> ParseObject(string s, ref int i)
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return d;
                }

                while (true)
                {
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != '"')
                        throw new FormatException("Expected property name at position " + i + ".");
                    string key = ParseString(s, ref i);

                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != ':')
                        throw new FormatException("Expected ':' at position " + i + ".");
                    i++;

                    d[key] = ParseValue(s, ref i);

                    SkipWhitespace(s, ref i);
                    if (i >= s.Length)
                        throw new FormatException("Unterminated object.");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at position " + i + ".");
                }
            }

            private static List<object> ParseArray(string s, ref int i)
            {
                List<object> list = new List<object>();
                i++;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return list;
                }

                while (true)
                {
                    list.Add(ParseValue(s, ref i));
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length)
                        throw new FormatException("Unterminated array.");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException("Expected ',' or ']' at position " + i + ".");
                }
            }

            private static string ParseString(string s, ref int i)
            {
                StringBuilder sb = new StringBuilder();
                i++;                                             // opening quote
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"')
                        return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (i >= s.Length)
                        break;

                    char e = s[i++];
                    switch (e)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length)
                                throw new FormatException("Bad unicode escape.");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default:
                            sb.Append(e);
                            break;
                    }
                }
                throw new FormatException("Unterminated string.");
            }

            private static object ParseNumber(string s, ref int i)
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
                    i++;
                string token = s.Substring(start, i - start);
                if (token.Length == 0)
                    throw new FormatException("Unexpected character at position " + start + ".");

                long asLong;
                if (token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
                    && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out asLong))
                    return asLong;

                return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        #endregion
    }

    // ---- types for the risk state machine (prefixed so they cannot clash with other add-ons in this namespace)

    public enum DtnRiskEventKind
    {
        Unknown, DataBack, FloorTrailed, ClosingUncertain, HighWaterStarted, RiskBuffer, RiskRecovered,
        DailyLoss, DailyLossBack, Consistency, ConsistencyBack, LockReleased
    }

    // Per-account state that persists between ticks (and, through the add-on's state file, between restarts).
    public sealed class DtnRiskState
    {
        public double HighWater = double.NaN;        // trailing modes: highest closing balance (eod) or live net liquidation (intraday)
        public double TrailFloor = double.NaN;       // trailing modes: computed floor, never moves down
        public double SessionStart = double.NaN;     // net liquidation when the session began: the daily-loss baseline
        public string Session = string.Empty;        // which session the baselines belong to
        public bool   BaselineMidSession;            // tracking began after the session had started
        public double LastNetLiq = double.NaN;
        public int    DailyLevel;                    // highest daily-loss percentage already alerted
        public DateTime FloorBreachSince = DateTime.MinValue;
        public DateTime DailyBreachSince = DateTime.MinValue;
        public bool   Unknown;                       // holding a position while net liquidation can't be trusted
        public DateTime UnknownSince = DateTime.MinValue;
        public DateTime LastUnknownAlertUtc = DateTime.MinValue;
        public double LastBalance = double.NaN;      // net liquidation minus open P&L at the last trusted reading
        public DateTime LastSeenUtc = DateTime.MinValue;   // when LastNetLiq and LastBalance were read
        public double SessionStartBalance = double.NaN;    // realized balance when the session began (consistency rule)
        public double BestDay = double.NaN;          // best completed session profit (consistency rule)
        public int    ConsistencyLevel;              // highest consistency alert percentage already sent
        public DateTime ConsistencyBreachSince = DateTime.MinValue;
        public double FloorLevel = double.PositiveInfinity;    // tightest floor alert level already sent
    }

    // The rules for one account, already resolved from the config file and /floor.
    public sealed class DtnRiskSettings
    {
        public string Mode = DirectTelegramNotifierCore.ModeManual;
        public string Basis = DirectTelegramNotifierCore.BasisBalance;
        public double Drawdown = double.NaN;
        public double FloorLock = double.NaN;
        public double StartingBalance = double.NaN;
        public double OverrideFloor = double.NaN;    // a /floor setting
        public double ManualFloor = double.NaN;      // RiskFloor
        public double ConsistencyPercent = double.NaN;
        public double ConsistencyTarget = double.NaN;
        public string DailyLimitText = string.Empty;      // "1000" or "2%"
        public IList<double> RiskBuffers = new List<double>();
        public IList<double> DailyLossAlerts = new List<double>();
        public IList<double> ConsistencyAlerts = new List<double>();
        public bool RiskAutoFlatten;
        public double FlattenBuffer = 150.0;
        public bool DailyLossAutoFlatten;
        public bool ConsistencyAutoFlatten;
        public int ConfirmSeconds = 2;
        public int UnknownFlattenSeconds;
        public int UnknownRemindSeconds = 60;
        public int UnmonitoredGapMinutes = 10;
        public double ClosingTolerance = 1.0;
    }

    public sealed class DtnRiskInput
    {
        public DateTime Now;                         // UTC
        public string Session;
        public double NetLiq = double.NaN;
        public double Balance = double.NaN;          // net liquidation minus open P&L
        public string UnknownWhy;                    // null when the numbers can be trusted
        public string LockedSession;                 // the session of an existing session lock, or null when there is none
        public DtnRiskSettings Settings;
    }

    // What happened. A and B..D are numbers and Text a string, by kind:
    //   Unknown: A seconds it has lasted (0 = first alert), N seconds until it flattens, Text why
    //   FloorTrailed: A floor before, B after, C high-water        ClosingUncertain: A last seen, B now
    //   HighWaterStarted: A high-water, B current value, Flag started from StartingBalance
    //   RiskBuffer: A net liq, B floor, C buffer, D alert level, Text source      RiskRecovered: A buffer
    //   DailyLoss: A loss, B limit, N percent      DailyLossBack: A loss, B limit
    //   Consistency: A today, B cap, C percent, N level      ConsistencyBack: A today, B cap
    public sealed class DtnRiskEvent
    {
        public DtnRiskEventKind Kind;
        public bool Loud;
        public double A, B, C, D;
        public int N;
        public bool Flag;
        public string Text;
    }

    // Open P&L is net liquidation minus the balance, so the closing balance depends on it. A connection that reports it as 0
    // instead of failing would make the "balance" equal net liquidation without any error. This notices the one case that
    // cannot be legitimate: a position is held, net liquidation keeps moving, and open P&L stays exactly 0.00.
    // A single step (a commission) is not enough: it needs several changes and a real move over the window.
    public sealed class DtnOpenPnlWatch
    {
        public double WindowSeconds = 30.0;
        public double MinMove = 5.0;                 // dollars of range in net liquidation
        public int MinChanges = 3;                   // distinct changes: a market moving, not one fee

        private DateTime since = DateTime.MinValue;
        private double minNet = double.NaN, maxNet = double.NaN, lastNet = double.NaN;
        private int changes;
        private bool suspect;

        public bool Suspect { get { return suspect; } }
        public double Move { get { return double.IsNaN(minNet) ? 0.0 : maxNet - minNet; } }

        private void Reset()
        {
            since = DateTime.MinValue;
            minNet = double.NaN; maxNet = double.NaN; lastNet = double.NaN;
            changes = 0;
            suspect = false;
        }

        // True once per episode, when it becomes suspicious.
        public bool Observe(DateTime now, bool holdingPosition, double openPnl, double netLiq)
        {
            bool zero = holdingPosition && !double.IsNaN(openPnl) && openPnl == 0.0 && !double.IsNaN(netLiq) && !double.IsInfinity(netLiq);
            if (!zero)
            {
                Reset();
                return false;
            }
            if (since == DateTime.MinValue)
            {
                since = now;
                minNet = netLiq; maxNet = netLiq; lastNet = netLiq;
                changes = 0;
            }
            else
            {
                if (Math.Abs(netLiq - lastNet) >= 0.01)
                    changes++;
                lastNet = netLiq;
                if (netLiq < minNet) minNet = netLiq;
                if (netLiq > maxNet) maxNet = netLiq;
            }
            if (!suspect && (now - since).TotalSeconds >= WindowSeconds && (maxNet - minNet) >= MinMove && changes >= MinChanges)
            {
                suspect = true;
                return true;
            }
            return false;
        }
    }

    // One row of the risk log (see FormatReadingRow / FormatSettingsRow).
    public sealed class DtnLogRow
    {
        public bool IsSettings;
        public DateTime Utc;
        public string Account;
        public DtnRiskSettings Settings;             // when IsSettings
        public DtnRiskInput Input;                   // otherwise (Settings is filled in by the replay)
    }

    public sealed class DtnRiskResult
    {
        public readonly List<DtnRiskEvent> Events = new List<DtnRiskEvent>();
        public string FireReason;                    // flatten and lock the account, for this reason
        public bool ReleaseSessionLock;              // the session a lock belongs to has ended
        public bool Dirty;                           // state changed in a way worth saving

        public void Add(DtnRiskEventKind kind, bool loud, double a, double b, double c, double d, int n, bool flag, string text)
        {
            Events.Add(new DtnRiskEvent { Kind = kind, Loud = loud, A = a, B = b, C = c, D = d, N = n, Flag = flag, Text = text });
        }
    }
}
