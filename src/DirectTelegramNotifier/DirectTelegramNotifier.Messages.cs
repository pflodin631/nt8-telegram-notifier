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

// DirectTelegramNotifier: Message templates (shared by real alerts and /simulate), diagnostics, simulation, small helpers.
// One part of a partial class: see DirectTelegramNotifier.cs for the overview.

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier
    {
        #region Message templates (shared by real alerts and /simulate)

        private static string MsgUiFrozen(int seconds)
        {
            return EmWarn + " <b>NINJATRADER UI FROZEN</b>\n"
                + "No response for " + Num(seconds) + "s. Orders and strategies may be affected. Check the machine.";
        }

        private static string MsgUiRecovered(TimeSpan blocked)
        {
            return EmOk + " <b>UI RECOVERED</b>\nNinjaTrader's UI was blocked for about " + FormatDuration(blocked) + ".";
        }

        private static string MsgFlapping(string connection, string channel, int drops)
        {
            return EmWarn + " <b>CONNECTION FLAPPING</b>\n"
                + Html(connection) + Sep + Html(channel) + "\n"
                + Num(drops) + " drops in " + Num(FlapWindowSec / 60) + " min. Further alerts for this channel are muted for "
                + Num(FlapMuteSec / 60) + " min; /status shows the live state.";
        }

        private static string MsgStrategyEnabled(string name, string account, string state)
        {
            return EmPlay + " <b>STRATEGY ENABLED</b>\n"
                + Html(name) + Sep + Html(account) + "\n"
                + "State: " + Html(state);
        }

        private static string MsgStrategyDisabled(string name, string account, string previous)
        {
            return EmStop + " <b>STRATEGY DISABLED</b>\n"
                + Html(name) + Sep + Html(account) + "\n"
                + Html(previous) + " \u2192 Terminated\n"
                + "<i>Check the NinjaTrader Log tab for the reason. A disabled strategy can leave a position open and unmanaged: check /status.</i>";
        }

        private static string MsgStrategyRemoved(string name, string account, string state)
        {
            return EmStop + " <b>STRATEGY REMOVED</b>\n"
                + Html(name) + Sep + Html(account) + "\n"
                + "It disappeared from the account while " + Html(state) + ". Check /status.";
        }

        private static string MsgStale(string instrument, TimeSpan silent, bool exposed)
        {
            return EmWarn + " <b>MARKET DATA STALE</b>\n"
                + Html(instrument) + Sep + "no ticks for " + FormatDuration(silent) + "\n"
                + (exposed ? "<b>You have a position or working orders here.</b> " : string.Empty)
                + "The data connection still reports Connected, so strategies on this instrument may be running blind.\n"
                + "<i>On a holiday or early close this can be normal.</i>";
        }

        private static string MsgDataResumed(string instrument, TimeSpan after)
        {
            return EmOk + " <b>MARKET DATA RESUMED</b>\n" + Html(instrument) + Sep + "after " + FormatDuration(after);
        }

        // diffs are already HTML-escaped lines such as "Account2: Flat, expected Long 3".
        private static string MsgMismatch(string instrument, string leaderName, int leaderNet, List<string> diffs, TimeSpan outOfSync)
        {
            return EmWarn + " <b>COPY MISMATCH</b>\n"
                + Html(instrument) + Sep + "leader " + Html(leaderName) + " " + FormatNet(leaderNet) + "\n"
                + "\u2022 " + string.Join("\n\u2022 ", diffs.ToArray()) + "\n"
                + "Out of sync for " + FormatDuration(outOfSync) + ". Check the copier and the follower connections.";
        }

        private static string MsgInSync(string instrument, TimeSpan after)
        {
            return EmOk + " <b>COPY IN SYNC</b>\n" + Html(instrument) + Sep + "followers match the leader again after " + FormatDuration(after);
        }

        private static string MsgOrderRate(int count, int windowSec, int limit, string breakdown)
        {
            return EmSiren + " <b>ORDER RATE HIGH</b>\n"
                + Num(count) + " new orders in " + Num(windowSec) + "s (limit " + Num(limit) + "): " + Html(breakdown) + "\n"
                + "Possible runaway strategy. Check NinjaTrader now; /flatten closes positions.";
        }

        private static string MsgReject(string account, string instrument, string action, string type, string qty, string name, string reason)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(EmSiren).Append(" <b>ORDER REJECTED</b>\n");
            sb.Append(Html(action)).Append(" ").Append(Html(type)).Append(" ").Append(Html(qty)).Append(" \u00D7 ")
              .Append(Html(instrument)).Append(Sep).Append(Html(account)).Append("\n");

            string cleanName = CleanOrderName(name);
            if (cleanName.Length > 0)
                sb.Append("Name: ").Append(Html(cleanName)).Append("\n");
            if (!string.IsNullOrEmpty(reason))
                sb.Append("Reason: ").Append(Html(Truncate(reason, 300))).Append("\n");

            sb.Append("<i>If this was a stop or exit, the position may be unprotected. Check /status.</i>");
            return sb.ToString();
        }

        private static string MsgRestart(bool clean, TimeSpan gap, DateTime lastAliveNt, DateTime nowNt)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(clean ? EmInfo + " <b>NINJATRADER RESTARTED</b>" : EmWarn + " <b>NINJATRADER WENT DOWN</b>")
              .Append(Sep).Append("down ").Append(FormatDuration(gap)).Append("\n");
            sb.Append("Last alive ").Append(lastAliveNt.ToString("ddd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(Sep).Append("back ").Append(nowNt.ToString("ddd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(" (NinjaTrader time)\n");
            sb.Append(clean
                ? "Exit: normal (closed or restarted)\n"
                : "Exit: <b>abnormal</b> (crash, forced kill, power loss, or no shutdown handler)\n");
            sb.Append("Connections and positions are not restored by the notifier. Check /status.");
            return sb.ToString();
        }

        private static string MsgReentry(string account, string instrument, string side, int qty, string name, int secondsAfter)
        {
            return EmSiren + " <b>RE-ENTERED AFTER FLATTEN</b>\n"
                + Html(account) + Sep + side + " " + Num(qty) + " \u00D7 " + Html(instrument)
                + (string.IsNullOrEmpty(name) ? string.Empty : Sep + Html(name)) + "\n"
                + "A position opened " + Num(secondsAfter) + "s after /flatten, so a strategy (or the copier) is trading again.\n"
                + "<i>/flatten does not disable strategies. Send /lock to keep the account flat, or disable the strategy in the Strategies tab.</i>";
        }

        #endregion

        #region Diagnostics, simulation and flatten safety

        private void CheckReentryAfterFlatten(CbiExecution ex)
        {
            if (reentryAlerted)
                return;
            double since = (DateTime.UtcNow - lastFlattenUtc).TotalSeconds;
            if (since > ReentryWindowSec)
                return;

            string orderName = ex.Order != null ? ex.Order.Name : string.Empty;
            if (string.Equals(orderName, "Close", StringComparison.OrdinalIgnoreCase))
                return;                                               // the flatten's own close orders

            reentryAlerted = true;
            EnqueueText(MsgReentry(AccountNameOf(ex), ex.Instrument.FullName,
                ex.MarketPosition == MarketPosition.Long ? "BUY" : "SELL", ex.Quantity, CleanOrderName(orderName), (int)since));
        }

        private void PruneTrackingSets()
        {
            if ((DateTime.UtcNow - lastPruneUtc).TotalMinutes < 10)
                return;
            lastPruneUtc = DateTime.UtcNow;

            DateTime cutoff = DateTime.UtcNow.AddHours(-72);
            lock (statsLock)
            {
                List<string> old = seenExecutionIds.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
                foreach (string key in old)
                    seenExecutionIds.Remove(key);
            }
        }

        private void PruneOrderIds(string accountName, HashSet<string> present)
        {
            string prefix = accountName + "|";
            lock (orderLock)
            {
                List<string> goneSeen = seenOrderIds.Where(id1 => id1.StartsWith(prefix, StringComparison.Ordinal) && !present.Contains(id1)).ToList();
                foreach (string key in goneSeen)
                    seenOrderIds.Remove(key);

                List<string> goneRejected = rejectedAlerted.Where(id2 => id2.StartsWith(prefix, StringComparison.Ordinal) && !present.Contains(id2)).ToList();
                foreach (string key in goneRejected)
                    rejectedAlerted.Remove(key);

                List<string> goneStamps = orderStateSince.Keys.Where(id3 => id3.StartsWith(prefix, StringComparison.Ordinal) && !present.Contains(id3)).ToList();
                foreach (string key in goneStamps)
                {
                    orderStateSince.Remove(key);
                    stuckAlerted.Remove(key);
                }
            }
        }

        private static string Ago(DateTime utc)
        {
            return FormatDuration(DateTime.UtcNow - utc) + " ago";
        }

        private string BuildDiagReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(EmInfo).Append(" <b>DIAGNOSTICS</b>").Append(Sep).Append(TimeText(NtNow())).Append("\n");
            sb.Append("v").Append(AddOnVersion).Append(Sep).Append("up ").Append(FormatDuration(DateTime.UtcNow - startedUtc)).Append("\n\n");

            sb.Append("<b>Telegram</b>\n");
            sb.Append("  Last send: ").Append(lastSendOkUtc == DateTime.MinValue ? "none yet" : Ago(lastSendOkUtc) + " (" + Num(lastSendMs) + " ms)").Append("\n");
            sb.Append("  Last poll: ").Append(lastPollOkUtc == DateTime.MinValue ? "none yet" : Ago(lastPollOkUtc)).Append("\n");
            int priorityWaiting;
            int waitingTotal = QueuedCount(out priorityWaiting);
            sb.Append("  Waiting to send: ").Append(Num(waitingTotal));
            if (waitingTotal > 0)
                sb.Append(" (").Append(Num(priorityWaiting)).Append(" priority, oldest ").Append(FormatDuration(TimeSpan.FromSeconds(OldestQueuedSeconds()))).Append(")");
            sb.Append("\n");
            if (DateTime.UtcNow < rateLimitedUntilUtc)
                sb.Append("  ").Append(EmWarn).Append(" Telegram rate limit for ").Append(FormatDuration(rateLimitedUntilUtc - DateTime.UtcNow)).Append("\n");
            if (!string.IsNullOrEmpty(cfgHeartbeatUrl))
                sb.Append("  External heartbeat: ").Append(heartbeatHoldReason.Length > 0 ? EmWarn + " paused (" + Html(heartbeatHoldReason) + ")" : "pinging").Append("\n");
            sb.Append("  PC clock vs Telegram: ").Append(lastClockDriftSec.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)).Append("s\n\n");

            int running;
            lock (strategyLock)
                running = strategies.Values.Count(x => !IsTerminatedState(x.State));
            int feedCount;
            lock (feedLock)
                feedCount = feeds.Values.Count(x => x.Subscribed);

            sb.Append("<b>Monitoring</b>\n");
            sb.Append("  Accounts ").Append(Num(SnapshotTrackedAccounts().Count)).Append(Sep)
              .Append("strategies running ").Append(Num(running)).Append(Sep)
              .Append("data feeds ").Append(Num(feedCount)).Append("\n");
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    sb.Append("  NinjaTrader memory ").Append(Num((int)(p.WorkingSet64 / (1024 * 1024)))).Append(" MB").Append(Sep)
                      .Append(Num(p.Threads.Count)).Append(" threads\n");
                }
            }
            catch (Exception) { }

            sb.Append("  NinjaTrader CPU ").Append(lastCpuSampleUtc == DateTime.MinValue ? "n/a" : Math.Round(lastCpuPercent).ToString("0", CultureInfo.InvariantCulture) + "%")
              .Append(Sep).Append("disk free ").Append(lastDiskFreeMb < 0 ? "n/a" : N2(lastDiskFreeMb / 1024.0) + " GB").Append("\n");

            sb.Append("\n<b>Settings</b>\n");
            sb.Append("  EOD ").Append(eodTimeOfDay.ToString(@"hh\:mm")).Append(Sep)
              .Append("stale-data ").Append(cfgStaleDataSeconds > 0 ? Num(cfgStaleDataSeconds) + "s" : "off").Append(Sep)
              .Append("order-rate ").Append(cfgRunawayMaxOrders > 0 ? Num(cfgRunawayMaxOrders) + "/" + Num(cfgRunawayWindowSec) + "s" : "off").Append(Sep)
              .Append("copy check ").Append(cfgMismatchSeconds > 0 ? Num(cfgMismatchSeconds) + "s" : "off").Append("\n");
            sb.Append("  Heartbeat ").Append(string.IsNullOrEmpty(cfgHeartbeatUrl) ? "off" : "on").Append(Sep)
              .Append("flatten confirm ").Append(cfgFlattenConfirm ? "on" : "off").Append(Sep)
              .Append("silent routine ").Append(cfgSilentRoutine ? "on" : "off").Append(Sep)
              .Append("fill CSV ").Append(cfgEnableFillCsv ? "on" : "off").Append("\n");

            List<string> lines;
            lock (linesLock)
                lines = recentLines.ToList();
            if (lines.Count > 0)
            {
                sb.Append("\n<b>Recent log</b>\n");
                foreach (string line in lines.Skip(Math.Max(0, lines.Count - 6)))
                    sb.Append("<code>").Append(Html(Truncate(line, 120))).Append("</code>\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        private string BuildCopyReport()
        {
            string quality = BuildCopyQualityBlock();
            if (quality.Length == 0)
                return EmInfo + " No leader vs follower fills compared since the last end-of-day report.";
            return EmChart + " " + quality;
        }

        private string BuildEodPreview()
        {
            DateTime nowTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eodTimeZone);
            string report;
            lock (statsLock)
                report = BuildEodReportLocked(nowTz);
            return EmInfo + " <b>PREVIEW</b> (nothing was reset)\n\n" + report + BuildBrokerBlock();
        }

        // Sends one sample of every alert, built by the same code as the real ones. Nothing is changed or flattened.
        private void RunSimulation()
        {
            const string tag = "\n<i>(simulated)</i>";
            string leader = ResolveLeaderName();
            if (leader.Length == 0)
                leader = "LeaderAccount";
            string follower1 = "FollowerAccount1";
            string follower2 = "FollowerAccount2";
            DateTime now = NtNow();

            EnqueueSample(EmInfo + " <b>SIMULATION</b>\nSample alerts follow. Nothing is wrong and nothing was changed.", false);

            FillGroup copied = new FillGroup { InstrumentName = "MNQ DEC26", IsBuy = true, FirstTime = now, SimLeader = leader, SimTick = 0.25 };
            copied.Legs[leader]    = new FillLeg { AccountName = leader,    ActionText = "Buy", Quantity = 3, Notional = 3 * 29411.00, FillCount = 1, FirstTime = now };
            copied.Legs[follower1] = new FillLeg { AccountName = follower1, ActionText = "Buy", Quantity = 3, Notional = 3 * 29410.00, FillCount = 1, FirstTime = now.AddMilliseconds(180) };
            copied.Legs[follower2] = new FillLeg { AccountName = follower2, ActionText = "Buy", Quantity = 3, Notional = 3 * 29411.25, FillCount = 2, FirstTime = now.AddMilliseconds(420) };
            EnqueueSample(BuildFillMessage(copied) + tag, false);

            FillGroup single = new FillGroup { InstrumentName = "MNQ DEC26", IsBuy = false, FirstTime = now };
            single.Legs[leader] = new FillLeg { AccountName = leader, ActionText = "Sell", OrderName = "ProfitTarget", Quantity = 3, Notional = 3 * 29435.75, FillCount = 1, FirstTime = now };
            EnqueueSample(BuildFillMessage(single) + tag, false);

            ConnNotice down = new ConnNotice { Connection = "MFFU", Down = true, Lost = true, Time = now, Detail = "Server closed the connection" };
            down.Channels.Add("orders");
            down.Channels.Add("data");
            EnqueueSample(BuildConnectionMessage(down) + tag, true);

            ConnNotice up = new ConnNotice { Connection = "MFFU", Down = false, Time = now, Downtime = TimeSpan.FromSeconds(12) };
            up.Channels.Add("orders");
            up.Channels.Add("data");
            EnqueueSample(BuildConnectionMessage(up) + tag, false);

            EnqueueSample(MsgFlapping("MFFU", "orders", 4) + tag, true);
            EnqueueSample(MsgUiFrozen(52) + tag, true);
            EnqueueSample(MsgUiRecovered(TimeSpan.FromSeconds(71)) + tag, false);
            EnqueueSample(MsgStrategyDisabled("MyStrategy", leader, "Realtime") + tag, true);
            EnqueueSample(MsgStrategyEnabled("MyStrategy", leader, "Realtime") + tag, false);
            EnqueueSample(MsgStale("MNQ DEC26", TimeSpan.FromSeconds(140), true) + tag, true);
            EnqueueSample(MsgDataResumed("MNQ DEC26", TimeSpan.FromSeconds(185)) + tag, false);
            EnqueueSample(MsgMismatch("MNQ DEC26", leader, 3, new List<string> { Html(follower1) + ": Flat, expected Long 3" }, TimeSpan.FromSeconds(25)) + tag, true);
            EnqueueSample(MsgInSync("MNQ DEC26", TimeSpan.FromSeconds(48)) + tag, false);
            EnqueueSample(MsgOrderRate(34, cfgRunawayWindowSec, cfgRunawayMaxOrders, "MNQ DEC26 \u00D730, ES DEC26 \u00D74") + tag, true);
            EnqueueSample(MsgReject(leader, "MNQ DEC26", "Sell", "StopMarket", "3", "Stop1", "Order price outside limits") + tag, true);
            EnqueueSample(MsgReentry(leader, "MNQ DEC26", "BUY", 3, "B27_Long", 14) + tag, true);
            EnqueueSample(MsgRestart(false, TimeSpan.FromSeconds(372), now.AddSeconds(-372), now) + tag, true);
            EnqueueSample(MsgClockDrift(21) + tag, true);

            EnqueueSample(MsgUnprotected(leader, "MNQ DEC26", 3, 1, TimeSpan.FromSeconds(24)) + tag, true);
            EnqueueSample(MsgOrphan(leader, "MNQ DEC26", 2, "ProfitTarget, Stop1", TimeSpan.FromSeconds(31)) + tag, true);
            EnqueueSample(MsgStuck(leader, "MNQ DEC26", "Sell", "StopMarket", "3", "Submitted", TimeSpan.FromSeconds(45)) + tag, true);
            EnqueueSample(MsgPositionLimit(leader, 12, 10) + tag, true);
            EnqueueSample(MsgRiskBuffer(leader, 48730.10, 48000.0, 730.10, 750.0, "eod") + tag, true);
            EnqueueSample(MsgRiskRecovered(leader, 2296.10) + tag, false);
            EnqueueSample(MsgLocked(leader, "net liq 48,120.50 is only 120.50 above the floor 48,000.00", "flattened MNQ DEC26") + tag, true);
            EnqueueSample(MsgLockEnforced(follower1, "flattened MNQ DEC26", 2, false) + tag, true);
            EnqueueSample(MsgLockEnforced(follower1, "flattened MNQ DEC26", 1, true) + tag, true);
            EnqueueSample(MsgUnlocked(leader, "/unlock") + tag, false);
            EnqueueSample(MsgDailyLoss(leader, 760.00, 1000.00, 75) + tag, true);
            EnqueueSample(MsgDailyLoss(leader, 1012.50, 1000.00, 100) + tag, true);
            EnqueueSample(MsgDailyLossRecovered(leader, 310.00, 1000.00) + tag, false);
            EnqueueSample(MsgRiskUnknown(leader, "market data for MNQ DEC26 is stale") + tag, true);
            EnqueueSample(MsgRiskKnown(leader) + tag, false);
            EnqueueSample(MsgFloorTrailed(leader, 48000.00, 48296.10, 50296.10) + tag, false);
            EnqueueSample(MsgConfigWarning(new List<string> { "Unrecognized setting RiskAutoFlaten (did you mean RiskAutoFlatten?)" }) + tag, true);
            EnqueueSample(MsgCpuHigh(94.0, TimeSpan.FromSeconds(195)) + tag, true);
            EnqueueSample(MsgDiskLow("C:", 1480) + tag, true);

            EnqueueSample(EmOk + " <b>Simulation finished</b>", false);
        }

        #endregion

        #region Message templates for the safety checks

        private static string MsgUnprotected(string account, string instrument, int net, int covered, TimeSpan elapsed)
        {
            return EmSiren + " <b>UNPROTECTED POSITION</b>\n"
                + Html(account) + Sep + FormatNet(net) + " <code>" + Html(instrument) + "</code>\n"
                + (covered <= 0
                    ? "No working stop order for " + FormatDuration(elapsed) + "."
                    : "Working stops cover only " + Num(covered) + " of " + Num(Math.Abs(net)) + " contracts for " + FormatDuration(elapsed) + ".") + "\n"
                + "<i>If your strategy handles stops internally, set ProtectiveCheckSeconds=0. Otherwise add a stop or /flatten.</i>";
        }

        private static string MsgOrphan(string account, string instrument, int count, string names, TimeSpan elapsed)
        {
            return EmSiren + " <b>ORPHAN EXIT ORDERS</b>\n"
                + Html(account) + Sep + "<code>" + Html(instrument) + "</code> is flat\n"
                + Num(count) + (count == 1 ? " exit-type order is" : " exit-type orders are") + " still working (" + Html(names) + ") after " + FormatDuration(elapsed) + ".\n"
                + "<i>A leftover stop or target can open an unwanted position. /cancel removes working orders.</i>";
        }

        private static string MsgStuck(string account, string instrument, string action, string type, string qty, string state, TimeSpan elapsed)
        {
            return EmWarn + " <b>ORDER STUCK</b>\n"
                + Html(action) + " " + Html(type) + " " + Html(qty) + " \u00D7 " + Html(instrument) + Sep + Html(account) + "\n"
                + "State " + Html(state) + " for " + FormatDuration(elapsed) + ". It may not be reaching the broker.\n"
                + "<i>Check the connection and the Orders tab.</i>";
        }

        private static string MsgPositionLimit(string account, int total, int limit)
        {
            return EmSiren + " <b>POSITION LIMIT EXCEEDED</b>\n"
                + Html(account) + Sep + Num(total) + " contracts open (limit " + Num(limit) + ")\n"
                + "<i>Check the strategies and the copier. /flatten closes positions.</i>";
        }

        private static string MsgRiskBuffer(string account, double netLiq, double floor, double buffer, double level, string source)
        {
            return EmSiren + " <b>RISK BUFFER</b>\n"
                + Html(account) + "\n"
                + "Net liq " + N2(netLiq) + Sep + "floor " + N2(floor) + "\n"
                + (buffer > 0 ? "<b>" + N2(buffer) + " above</b> the floor" : "<b>" + N2(-buffer) + " BELOW</b> the floor")
                + (level > 0 ? " (alert level " + N2(level) + ")" : string.Empty)
                + (source == "/floor" || source == "config" ? "\n<i>A hand-set floor: keep it current with /floor if your drawdown trails.</i>" : string.Empty);
        }

        private static string MsgRiskRecovered(string account, double buffer)
        {
            return EmOk + " <b>RISK BUFFER RECOVERED</b>\n" + Html(account) + Sep + N2(buffer) + " above the floor";
        }

        private static string MsgLocked(string account, string reason, string action)
        {
            return EmLock + " <b>ACCOUNT LOCKED</b>\n"
                + Html(account) + "\n"
                + Html(reason) + "\n"
                + "Sent: " + Html(action) + "\n"
                + "New positions on this account are closed automatically until /unlock or the session ends.\n"
                + "<i>Strategies and copiers are not disabled: turn them off for this account, or each new entry is closed at a loss.</i>";
        }

        private static string MsgLockEnforced(string account, string action, int count, bool hold)
        {
            return EmLock + " <b>" + (hold ? "RE-ENTRY CLOSED" : "LOCK ENFORCED") + "</b>\n"
                + Html(account) + Sep + "new position closed (" + Num(count) + (count == 1 ? " time" : " times") + " since " + (hold ? "/flatten" : "the lock") + ")\n"
                + "Sent: " + Html(action) + "\n"
                + (hold ? "<i>A strategy or the copier traded right after /flatten. Use /lock to keep the account flat.</i>"
                        : "<i>Something keeps trading this locked account: disable the strategy or the copier follower.</i>");
        }

        private static string MsgUnlocked(string accounts, string why)
        {
            return EmUnlock + " <b>UNLOCKED</b>\n" + Html(accounts) + Sep + Html(why);
        }

        private static string MsgRiskUnknown(string account, string why)
        {
            return EmWarn + " <b>RISK UNKNOWN</b>\n"
                + Html(account) + " holds a position, but " + Html(why) + ".\n"
                + "Net liquidation may be frozen, so floor and daily-loss checks (and auto-flatten) are paused for this account.\n"
                + "<i>Check the position on your broker's platform.</i>";
        }

        private static string MsgRiskKnown(string account)
        {
            return EmOk + " <b>RISK DATA BACK</b>\n" + Html(account) + ": floor and daily-loss checks resumed";
        }

        private static string MsgDailyLoss(string account, double loss, double limit, int pct)
        {
            return (pct >= 100 ? EmSiren + " <b>DAILY LOSS LIMIT REACHED</b>" : (pct >= 90 ? EmSiren : EmWarn) + " <b>DAILY LOSS " + Num(pct) + "%</b>") + "\n"
                + Html(account) + Sep + "down " + N2(loss) + " today" + Sep + "limit " + N2(limit);
        }

        private static string MsgDailyLossRecovered(string account, double loss, double limit)
        {
            return EmOk + " <b>DAILY LOSS BACK UNDER ALERT LEVELS</b>\n"
                + Html(account) + Sep + (loss > 0 ? "down " + N2(loss) : "up " + N2(-loss)) + " today" + Sep + "limit " + N2(limit);
        }

        private static string MsgFloorTrailed(string account, double before, double after, double highWater)
        {
            return EmUp + " <b>FLOOR TRAILED</b>\n"
                + Html(account) + Sep + "floor " + (double.IsNaN(before) ? string.Empty : N2(before) + " \u2192 ") + N2(after) + "\n"
                + "End-of-day high-water " + N2(highWater);
        }

        private static string MsgConfigWarning(List<string> warnings)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(EmWarn).Append(" <b>CONFIG WARNING</b>\n");
            foreach (string w in warnings.Take(12))
                sb.Append("\u2022 ").Append(Html(w)).Append("\n");
            if (warnings.Count > 12)
                sb.Append("\u2022 and ").Append(Num(warnings.Count - 12)).Append(" more (see the NinjaScript Output window)\n");
            sb.Append("<i>These settings are ignored or replaced by defaults. Fix the config file, then press F5 in the NinjaScript Editor.</i>");
            return sb.ToString();
        }

        private static string MsgCpuHigh(double percent, TimeSpan duration)
        {
            return EmWarn + " <b>NINJATRADER CPU HIGH</b>\n"
                + Num((int)Math.Round(percent)) + "% for " + FormatDuration(duration) + ". Orders and copying may lag.\n"
                + "<i>Close unused charts, Market Analyzer windows and indicators.</i>";
        }

        private static string MsgMemoryHigh(int memoryMb, int limitMb)
        {
            return EmWarn + " <b>NINJATRADER MEMORY HIGH</b>\n"
                + "Using " + N0(memoryMb) + " MB (alert at " + N0(limitMb) + " MB).\n"
                + "<i>A restart while flat clears it.</i>";
        }

        private static string MsgDiskLow(string drive, long freeMb)
        {
            return EmWarn + " <b>DISK SPACE LOW</b>\n"
                + Html(drive) + " has " + N0(freeMb) + " MB free.\n"
                + "<i>NinjaTrader logs and data may stop being written.</i>";
        }

        #endregion

        #region Small helpers

        private static bool IsWorkingState(OrderState s)
        {
            return s == OrderState.Working
                || s == OrderState.Accepted
                || s == OrderState.Submitted
                || s == OrderState.TriggerPending
                || s == OrderState.ChangePending
                || s == OrderState.ChangeSubmitted
                || s == OrderState.PartFilled;
        }

        private static string AccountNameOf(CbiExecution ex)
        {
            return ex.Account != null ? ex.Account.Name : "?";
        }

        private static string ConnectionName(CbiConnection c)
        {
            return c != null && c.Options != null && !string.IsNullOrEmpty(c.Options.Name) ? c.Options.Name : "Connection";
        }

        private static double SafeAccountValue(CbiAccount a, AccountItem item)
        {
            try { return a.Get(item, Currency.UsDollar); }
            catch (Exception) { return double.NaN; }
        }

        private static string FormatPriceSafe(CbiInstrument instrument, double price)
        {
            try
            {
                if (instrument != null && instrument.MasterInstrument != null)
                    return instrument.MasterInstrument.FormatPrice(price);
            }
            catch (Exception) { }
            return price.ToString("0.00####", CultureInfo.InvariantCulture);
        }

        private static string Money(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "n/a";
            string sign = value > 0.004 ? "+" : (value < -0.004 ? "-" : string.Empty);
            return sign + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;
            if (span.TotalDays >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}d {1}h {2}m", (int)span.TotalDays, span.Hours, span.Minutes);
            if (span.TotalHours >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", (int)span.TotalHours, span.Minutes);
            if (span.TotalMinutes >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}m {1}s", (int)span.TotalMinutes, span.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}s", (int)span.TotalSeconds);
        }


        private static string Num(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string TimeText(DateTime t)
        {
            return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string ChannelText(ConnectionStatus status)
        {
            if (status == ConnectionStatus.Connected)
                return EmOk;
            if (status == ConnectionStatus.Connecting || status == ConnectionStatus.Disconnecting)
                return EmWait + " " + status;
            return EmWarn + " " + status;
        }

        private static string Html(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s ?? string.Empty;
            return s.Substring(0, max - 1) + "\u2026";
        }

        private static void DisposeTimer(ref System.Threading.Timer timer)
        {
            System.Threading.Timer t = Interlocked.Exchange(ref timer, null);
            if (t != null)
            {
                try { t.Dispose(); } catch (Exception) { }
            }
        }

        private static string Describe(Exception ex)
        {
            Exception root = ex;
            while (root.InnerException != null)
                root = root.InnerException;
            string text = ex.GetType().Name + ": " + ex.Message;
            if (!ReferenceEquals(root, ex))
                text += " -> " + root.GetType().Name + ": " + root.Message;
            return text;
        }

        private string Redact(string s)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(resolvedToken))
                return s;
            string redacted = s.Replace(resolvedToken, "***TOKEN***");
            if (!string.IsNullOrEmpty(cfgHeartbeatUrl))
                redacted = redacted.Replace(cfgHeartbeatUrl, "***HEARTBEAT-URL***");
            return redacted;
        }

        private void WriteOutput(string message)
        {
            try { string clean = Redact(message); Print("[DirectTelegramNotifier] " + clean); RememberLine(clean); }
            catch (Exception) { }
        }

        private void WriteError(string context, Exception ex)
        {
            WriteOutput("ERROR in " + context + ": " + Describe(ex));
        }

        private void WriteThrottled(string message)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastThrottledPrintUtc).TotalSeconds < 60)
                return;
            lastThrottledPrintUtc = now;
            WriteOutput(message + " (similar messages suppressed for 60s)");
        }

        #endregion
    }
}
