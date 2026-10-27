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

// DirectTelegramNotifier: Executions, fill alerts, copy quality, fill CSV and reports.
// One part of a partial class: see DirectTelegramNotifier.cs for the overview.

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier
    {
        #region Accounts and executions

        private void AttachAccounts()
        {
            try
            {
                if (terminated || !started)
                    return;

                List<CbiAccount> all;
                lock (CbiAccount.All)
                    all = CbiAccount.All.ToList();

                List<CbiAccount> newlyAttached = new List<CbiAccount>();

                foreach (CbiAccount a in all)
                {
                    if (a == null || !AccountMatches(a.Name))
                        continue;

                    CbiAccount stale = null;
                    bool alreadyTracked = false;

                    lock (accountsLock)
                    {
                        foreach (CbiAccount t in trackedAccounts)
                        {
                            if (ReferenceEquals(t, a)) { alreadyTracked = true; break; }
                            if (string.Equals(t.Name, a.Name, StringComparison.OrdinalIgnoreCase)) stale = t;
                        }
                        if (!alreadyTracked)
                        {
                            if (stale != null)
                                trackedAccounts.Remove(stale);
                            trackedAccounts.Add(a);
                        }
                    }

                    if (alreadyTracked)
                        continue;

                    if (stale != null)
                    {
                        try { stale.ExecutionUpdate -= OnExecutionUpdate; } catch (Exception) { }
                    }

                    a.ExecutionUpdate += OnExecutionUpdate;
                    newlyAttached.Add(a);
                    WriteOutput("Tracking account '" + a.Name + "'.");
                }

                foreach (CbiAccount a in newlyAttached)
                    SeedExecutions(a);

                int count;
                lock (accountsLock)
                    count = trackedAccounts.Count;

                if (count == 0)
                    WriteOutput("No connected account matches AccountName='" + cfgAccountName + "' yet. Will re-check whenever a connection comes up.");
            }
            catch (Exception ex)
            {
                WriteError("AttachAccounts", ex);
            }
        }

        private bool AccountMatches(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            string filter = (cfgAccountName ?? string.Empty).Trim();
            if (filter.Length == 0 || filter == "*")
                return !name.Equals("Backtest", StringComparison.OrdinalIgnoreCase);

            foreach (string part in filter.Split(','))
            {
                if (part.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Loads this session's earlier fills (e.g. after a midday restart) into the stats without alerting.
        private void SeedExecutions(CbiAccount a)
        {
            try
            {
                List<CbiExecution> list;
                lock (a.Executions)
                    list = a.Executions.ToList();

                int seeded = 0;
                foreach (CbiExecution ex in list.Where(x => x != null).OrderBy(x => x.Time))
                {
                    if (ex.Time < sessionStartNt)
                        continue;
                    if (RecordExecution(ex))
                        seeded++;
                }

                if (seeded > 0)
                    WriteOutput("Loaded " + seeded + " earlier execution(s) from '" + a.Name + "' into session statistics.");
            }
            catch (Exception ex)
            {
                WriteError("SeedExecutions(" + a.Name + ")", ex);
            }
        }

        private List<CbiAccount> SnapshotTrackedAccounts()
        {
            lock (accountsLock)
                return trackedAccounts.ToList();
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (terminated || e == null || e.Execution == null)
                    return;

                CbiExecution ex = e.Execution;

                bool opened;
                if (!RecordExecution(ex, out opened))
                    return;                                   // duplicate event for an execution already seen

                if (opened)
                {
                    if (ex.Account != null && IsAccountLocked(ex.Account.Name))
                        EnforceLockSoon(ex.Account);
                    else
                        CheckReentryAfterFlatten(ex);
                }

                if (!cfgEnableExecutions || stopping)
                    return;

                // Executions replayed on reconnect are counted in stats but never re-alerted.
                if (Math.Abs((NtNow() - ex.Time).TotalSeconds) > FillAlertMaxAgeSec)
                    return;

                QueueFillAlert(ex);
            }
            catch (Exception exc)
            {
                WriteError("ExecutionUpdate", exc);
            }
        }

        private bool RecordExecution(CbiExecution ex)
        {
            bool opened;
            return RecordExecution(ex, out opened);
        }

        private bool RecordExecution(CbiExecution ex, out bool opened)
        {
            opened = false;
            if (ex == null || ex.Instrument == null || ex.Quantity <= 0)
                return false;

            string id = !string.IsNullOrEmpty(ex.ExecutionId)
                ? ex.ExecutionId
                : AccountNameOf(ex) + "|" + ex.Instrument.FullName + "|" + ex.Time.Ticks + "|"
                  + ex.Price.ToString("R", CultureInfo.InvariantCulture) + "|" + ex.Quantity;

            lock (statsLock)
            {
                if (seenExecutionIds.ContainsKey(id))
                    return false;
                seenExecutionIds[id] = DateTime.UtcNow;
                opened = ApplyExecutionLocked(ex);
            }
            return true;
        }

        // FIFO lot matching per account+instrument. A "trade" is a flat-to-flat round trip;
        // a reversal closes the current trade and opens a new one with the remainder.
        private bool ApplyExecutionLocked(CbiExecution ex)
        {
            int qty = ex.Quantity;
            int dir = ex.MarketPosition == MarketPosition.Long ? 1 : -1;
            double pointValue = 1.0;
            if (ex.Instrument.MasterInstrument != null && ex.Instrument.MasterInstrument.PointValue > 0)
                pointValue = ex.Instrument.MasterInstrument.PointValue;

            double commission = ex.Commission > 0 ? ex.Commission : cfgEstimatedCommissionPerSide * qty;

            string accountName = AccountNameOf(ex);
            string key = accountName + "|" + ex.Instrument.FullName;
            PositionBook book;
            if (!books.TryGetValue(key, out book))
            {
                book = new PositionBook { AccountName = accountName, InstrumentName = ex.Instrument.FullName };
                books[key] = book;
            }

            SessionStats st = StatsFor(accountName);
            st.Executions++;
            tradedInstruments[ex.Instrument.FullName] = ex.Instrument;
            st.Contracts  += qty;
            st.Commission += commission;
            book.TradeCommission += commission;

            int remaining = qty;

            if (book.Net != 0 && Math.Sign(book.Net) != dir)
            {
                int positionDir = Math.Sign(book.Net);
                while (remaining > 0 && book.Lots.Count > 0)
                {
                    Lot lot = book.Lots[0];
                    int matched = Math.Min(lot.Quantity, remaining);
                    double pnl = (ex.Price - lot.Price) * matched * pointValue * positionDir;

                    book.TradeGross += pnl;
                    st.Gross        += pnl;

                    lot.Quantity -= matched;
                    remaining    -= matched;
                    book.Net     -= positionDir * matched;

                    if (lot.Quantity == 0)
                        book.Lots.RemoveAt(0);
                }

                if (book.Net == 0)
                    FinalizeTradeLocked(book);
            }

            if (remaining > 0)
            {
                book.Lots.Add(new Lot { Price = ex.Price, Quantity = remaining });
                book.Net += dir * remaining;
            }

            return remaining > 0;                              // true when the fill opened or added to a position
        }

        private void FinalizeTradeLocked(PositionBook book)
        {
            SessionStats st = StatsFor(book.AccountName);
            double net = book.TradeGross - book.TradeCommission;
            st.Trades++;
            if (net > 0.005)
            {
                st.Wins++;
                st.LargestWin = Math.Max(st.LargestWin, net);
            }
            else if (net < -0.005)
            {
                st.Losses++;
                st.LargestLoss = Math.Min(st.LargestLoss, net);
            }
            else
            {
                st.Scratches++;
            }
            book.TradeGross = 0;
            book.TradeCommission = 0;
        }

        // Statistics for one account. Call with statsLock held.
        private SessionStats StatsFor(string account)
        {
            SessionStats st;
            if (!acctStats.TryGetValue(account, out st))
            {
                st = new SessionStats();
                acctStats[account] = st;
            }
            return st;
        }

        #endregion

        #region Fill alerts

        // Fills of the same instrument and side that arrive within FillGroupMs (config) are sent as one message,
        // so a leader and its followers show up together, with each follower's price difference.
        private void QueueFillAlert(CbiExecution ex)
        {
            bool isBuy = ex.MarketPosition == MarketPosition.Long;
            string key = ex.Instrument.FullName + "|" + (isBuy ? "B" : "S");
            string account = AccountNameOf(ex);

            bool schedule = false;
            lock (fillLock)
            {
                FillGroup g;
                if (!pendingFills.TryGetValue(key, out g))
                {
                    g = new FillGroup
                    {
                        Instrument     = ex.Instrument,
                        InstrumentName = ex.Instrument.FullName,
                        IsBuy          = isBuy,
                        FirstTime      = ex.Time
                    };
                    pendingFills[key] = g;
                    schedule = true;
                }
                if (ex.Time < g.FirstTime)
                    g.FirstTime = ex.Time;

                FillLeg leg;
                if (!g.Legs.TryGetValue(account, out leg))
                {
                    leg = new FillLeg
                    {
                        AccountName = account,
                        ActionText  = DescribeAction(ex),
                        OrderName   = ex.Order != null ? ex.Order.Name : string.Empty,
                        FirstTime   = ex.Time
                    };
                    g.Legs[account] = leg;
                }
                leg.Quantity  += ex.Quantity;
                leg.Notional  += ex.Price * ex.Quantity;
                leg.FillCount += 1;
                if (ex.Time < leg.FirstTime)
                    leg.FirstTime = ex.Time;
            }

            if (schedule)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(cfgFillGroupMs);
                    FlushFill(key);
                });
            }
        }

        private void FlushFill(string key)
        {
            try
            {
                FillGroup g;
                lock (fillLock)
                {
                    if (!pendingFills.TryGetValue(key, out g))
                        return;
                    pendingFills.Remove(key);
                }
                PublishFillGroup(g);
            }
            catch (Exception ex)
            {
                WriteError("Fill alert", ex);
            }
        }

        private void FlushPendingFills()
        {
            List<FillGroup> pending;
            lock (fillLock)
            {
                pending = pendingFills.Values.ToList();
                pendingFills.Clear();
            }
            foreach (FillGroup g in pending)
                PublishFillGroup(g);
        }

        private void PublishFillGroup(FillGroup g)
        {
            if (cfgEnableExecutions)
                EnqueueQuiet(BuildFillMessage(g));
            try { RecordCopyQuality(g); } catch (Exception ex) { WriteError("Copy quality", ex); }
            try { WriteFillCsv(g); } catch (Exception ex) { WriteError("Fill CSV", ex); }
        }

        private string BuildFillMessage(FillGroup g)
        {
            string leaderName = !string.IsNullOrEmpty(g.SimLeader) ? g.SimLeader : ResolveLeaderName();
            List<FillLeg> legs = g.Legs.Values
                .OrderBy(l => l.AccountName.Equals(leaderName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(l => l.AccountName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string side = g.IsBuy ? "BUY" : "SELL";
            string icon = g.IsBuy ? EmGreen : EmRed;
            StringBuilder sb = new StringBuilder();

            // One account: the compact single-line headline.
            if (legs.Count == 1)
            {
                FillLeg only = legs[0];
                sb.Append(icon).Append(" <b>").Append(side).Append(" ").Append(Num(only.Quantity)).Append(" \u00D7 ")
                  .Append(Html(g.InstrumentName)).Append("</b> @ ").Append(Html(LegPrice(g, only))).Append("\n");
                sb.Append(Html(only.AccountName)).Append(Sep).Append(TimeText(g.FirstTime)).Append("\n");

                List<string> details = new List<string>();
                string name = CleanOrderName(only.OrderName);
                if (name.Length > 0)
                    details.Add(Html(name));
                if (!only.ActionText.Equals("Buy", StringComparison.OrdinalIgnoreCase) && !only.ActionText.Equals("Sell", StringComparison.OrdinalIgnoreCase))
                    details.Add(Html(only.ActionText));
                if (only.FillCount > 1)
                    details.Add(Num(only.FillCount) + " fills");
                if (details.Count > 0)
                    sb.Append("Order: ").Append(string.Join(Sep, details.ToArray()));

                return sb.ToString().TrimEnd('\n');
            }

            // Several accounts (leader and followers): one headline, one line per account.
            sb.Append(icon).Append(" <b>").Append(side).Append(" ").Append(Html(g.InstrumentName)).Append("</b>")
              .Append(Sep).Append(TimeText(g.FirstTime)).Append("\n");

            FillLeg leader = legs.FirstOrDefault(l => l.AccountName.Equals(leaderName, StringComparison.OrdinalIgnoreCase));
            double tick = g.SimTick > 0 ? g.SimTick : TickSizeOf(g.Instrument);

            foreach (FillLeg leg in legs)
            {
                bool isLeader = leader != null && ReferenceEquals(leg, leader);
                sb.Append("<b>").Append(Html(leg.AccountName)).Append("</b>");
                if (isLeader)
                    sb.Append(" (leader)");
                sb.Append(Sep).Append(Num(leg.Quantity)).Append(" @ ").Append(Html(LegPrice(g, leg)));
                if (leader != null && !isLeader && tick > 0)
                    sb.Append(Sep).Append(DescribeDifference(g.IsBuy, LegAverage(leader), LegAverage(leg), tick));
                if (leg.FillCount > 1)
                    sb.Append(Sep).Append(Num(leg.FillCount)).Append(" fills");
                sb.Append("\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static double LegAverage(FillLeg leg)
        {
            return leg.Quantity > 0 ? leg.Notional / leg.Quantity : 0.0;
        }

        private static string LegPrice(FillGroup g, FillLeg leg)
        {
            double avg = LegAverage(leg);
            return leg.FillCount > 1
                ? avg.ToString("0.######", CultureInfo.InvariantCulture) + " avg"
                : FormatPriceSafe(g.Instrument, avg);
        }

        private static double TickSizeOf(CbiInstrument instrument)
        {
            try
            {
                if (instrument != null && instrument.MasterInstrument != null)
                    return instrument.MasterInstrument.TickSize;
            }
            catch (Exception) { }
            return 0.0;
        }

        private static string DescribeDifference(bool isBuy, double leaderAvg, double followerAvg, double tick)
        {
            return DescribeTicks(TickDifference(isBuy, leaderAvg, followerAvg, tick));
        }

        private static double TickDifference(bool isBuy, double leaderAvg, double followerAvg, double tick)
        {
            return Dtn.TickDifference(isBuy, leaderAvg, followerAvg, tick);
        }

        private static string DescribeTicks(double ticks)
        {
            return Dtn.DescribeTicks(ticks);
        }

        private static double PointValueOf(CbiInstrument instrument)
        {
            try
            {
                if (instrument != null && instrument.MasterInstrument != null && instrument.MasterInstrument.PointValue > 0)
                    return instrument.MasterInstrument.PointValue;
            }
            catch (Exception) { }
            return 1.0;
        }

        private static string CleanOrderName(string name)
        {
            return Dtn.CleanOrderName(name);
        }

        private static string DescribeAction(CbiExecution ex)
        {
            if (ex.Order != null)
            {
                switch (ex.Order.OrderAction)
                {
                    case OrderAction.Buy:        return "Buy";
                    case OrderAction.BuyToCover: return "Buy to cover";
                    case OrderAction.Sell:       return "Sell";
                    case OrderAction.SellShort:  return "Sell short";
                }
            }
            return ex.MarketPosition == MarketPosition.Long ? "Buy" : "Sell";
        }

        #endregion

        #region Copy quality and fill export

        // Compares each follower's fill with the leader's fill from the same group (same instrument, side and moment).
        private void RecordCopyQuality(FillGroup g)
        {
            string leaderName = ResolveLeaderName();
            FillLeg leader;
            if (leaderName.Length == 0 || !g.Legs.TryGetValue(leaderName, out leader))
                return;

            double tick = TickSizeOf(g.Instrument);
            if (tick <= 0)
                return;
            double pointValue = PointValueOf(g.Instrument);

            foreach (FillLeg leg in g.Legs.Values)
            {
                if (ReferenceEquals(leg, leader))
                    continue;

                double ticks = TickDifference(g.IsBuy, LegAverage(leader), LegAverage(leg), tick);
                double dollars = -ticks * tick * pointValue * leg.Quantity;
                double lagMs = (leg.FirstTime - leader.FirstTime).TotalMilliseconds;

                lock (statsLock)
                {
                    CopyStat cs;
                    if (!copyStats.TryGetValue(leg.AccountName, out cs))
                    {
                        cs = new CopyStat();
                        copyStats[leg.AccountName] = cs;
                    }
                    cs.Count++;
                    cs.SumTicks   += ticks;
                    cs.SumDollars += dollars;
                    cs.WorstTicks  = Math.Max(cs.WorstTicks, ticks);
                    cs.SumLagMs   += lagMs;
                    cs.MaxLagMs    = Math.Max(cs.MaxLagMs, lagMs);
                }
            }
        }

        private string BuildCopyQualityBlock()
        {
            List<KeyValuePair<string, CopyStat>> items;
            lock (statsLock)
                items = copyStats.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<b>Copy quality</b> (followers vs leader)\n");
            foreach (KeyValuePair<string, CopyStat> kv in items)
            {
                CopyStat cs = kv.Value;
                double avgTicks = cs.Count > 0 ? cs.SumTicks / cs.Count : 0.0;
                double avgLag = cs.Count > 0 ? cs.SumLagMs / cs.Count : 0.0;

                sb.Append(Html(kv.Key)).Append(": avg ").Append(DescribeTicks(avgTicks).Replace("same price", "no difference"))
                  .Append(" per fill").Append(Sep).Append(Money(cs.SumDollars)).Append(" vs leader")
                  .Append(Sep).Append("lag ").Append(Math.Round(avgLag).ToString("0", CultureInfo.InvariantCulture)).Append(" ms avg, ")
                  .Append(Math.Round(cs.MaxLagMs).ToString("0", CultureInfo.InvariantCulture)).Append(" max")
                  .Append(Sep).Append(Num(cs.Count)).Append(cs.Count == 1 ? " fill" : " fills").Append("\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static string FillCsvPath()
        {
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigPath()), "DirectTelegramNotifier-fills.csv");
        }

        private static string CsvField(string value)
        {
            value = value ?? string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        // One row per account per fill group; followers also carry the price difference and lag vs the leader.
        private void WriteFillCsv(FillGroup g)
        {
            if (!cfgEnableFillCsv)
                return;

            string leaderName = ResolveLeaderName();
            FillLeg leader = null;
            if (leaderName.Length > 0)
                g.Legs.TryGetValue(leaderName, out leader);
            double tick = TickSizeOf(g.Instrument);

            StringBuilder rows = new StringBuilder();
            foreach (FillLeg leg in g.Legs.Values.OrderBy(l => l.AccountName, StringComparer.OrdinalIgnoreCase))
            {
                bool isLeader = leader != null && ReferenceEquals(leg, leader);
                string role = leader == null ? string.Empty : (isLeader ? "leader" : "follower");
                string diff = string.Empty;
                string lag = string.Empty;
                if (leader != null && !isLeader)
                {
                    lag = Math.Round((leg.FirstTime - leader.FirstTime).TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture);
                    if (tick > 0)
                        diff = Math.Round(TickDifference(g.IsBuy, LegAverage(leader), LegAverage(leg), tick), 2).ToString("0.##", CultureInfo.InvariantCulture);
                }

                rows.Append(leg.FirstTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',')
                    .Append(CsvField(leg.AccountName)).Append(',').Append(role).Append(',')
                    .Append(CsvField(g.InstrumentName)).Append(',').Append(g.IsBuy ? "BUY" : "SELL").Append(',')
                    .Append(Num(leg.Quantity)).Append(',')
                    .Append(LegAverage(leg).ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Num(leg.FillCount)).Append(',').Append(diff).Append(',').Append(lag).Append("\r\n");
            }

            string path = FillCsvPath();
            lock (csvLock)
            {
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 10 * 1024 * 1024)
                {
                    string old = path + ".old";
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(path, old);
                    info = new FileInfo(path);
                }
                if (!info.Exists)
                    File.WriteAllText(path, "time_nt,account,role,instrument,side,qty,avg_price,fills,ticks_worse_than_leader,lag_ms_vs_leader\r\n", new UTF8Encoding(false));
                File.AppendAllText(path, rows.ToString(), new UTF8Encoding(false));
            }
        }

        #endregion

        #region Reports

        private string BuildStatusReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(EmChart).Append(" <b>STATUS</b>").Append(Sep).Append(TimeText(NtNow())).Append("\n\n");

            sb.Append("<b>Connections</b>\n").Append(BuildConnectionsBlock()).Append("\n");

            string leaderName = ResolveLeaderName();
            bool several = AccountFilterHasSeveral();
            List<CbiAccount> accounts = SnapshotTrackedAccounts();

            sb.Append("<b>Accounts</b>\n");
            if (accounts.Count == 0)
                sb.Append("  none tracked yet\n");

            bool firstAccount = true;
            foreach (CbiAccount a in accounts)
            {
                if (!firstAccount)
                    sb.Append("\n");
                firstAccount = false;

                bool flat;
                sb.Append("<b>").Append(Html(a.Name)).Append("</b>");
                if (several && leaderName.Length > 0 && a.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (leader)");
                sb.Append("\n").Append(BuildPositionLines(a, out flat));
                sb.Append("  P&amp;L: realized ").Append(Money(SafeAccountValue(a, AccountItem.RealizedProfitLoss)))
                  .Append(Sep).Append("open ").Append(Money(SafeAccountValue(a, AccountItem.UnrealizedProfitLoss))).Append("\n");

                sb.Append(BuildRiskLines(a, false));

                lock (statsLock)
                {
                    SessionStats st;
                    if (acctStats.TryGetValue(a.Name, out st))
                        sb.Append("  Today: ").Append(Num(st.Trades)).Append(st.Trades == 1 ? " trade" : " trades")
                          .Append(" (").Append(Num(st.Wins)).Append("W / ").Append(Num(st.Losses)).Append("L)\n");
                }
            }

            string copyLine = BuildCopyStatusLine();
            if (copyLine.Length > 0)
                sb.Append("\n").Append(copyLine);

            sb.Append("\n<b>Strategies</b>\n").Append(BuildStrategiesBlock());
            sb.Append(BuildFeedLines());

            sb.Append("\nNotifier up ").Append(FormatDuration(DateTime.UtcNow - startedUtc));
            return sb.ToString();
        }

        private string BuildConnectionsBlock()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                List<CbiConnection> connections;
                lock (CbiConnection.Connections)
                    connections = CbiConnection.Connections.ToList();

                int shown = 0;
                foreach (CbiConnection c in connections)
                {
                    if (c == null)
                        continue;
                    shown++;
                    sb.Append("\u2022 <code>").Append(Html(ConnectionName(c))).Append("</code> \u2014 orders ")
                      .Append(ChannelText(c.Status)).Append(" \u00B7 data ").Append(ChannelText(c.PriceStatus)).Append("\n");
                }
                if (shown == 0)
                    sb.Append("\u2022 none\n");
            }
            catch (Exception ex)
            {
                sb.Append("\u2022 unavailable: ").Append(Html(ex.Message)).Append("\n");
            }
            return sb.ToString();
        }

        private string BuildPositionLines(CbiAccount a, out bool flat)
        {
            flat = true;
            StringBuilder sb = new StringBuilder();
            try
            {
                int open = 0;
                lock (a.Positions)
                {
                    foreach (CbiPosition p in a.Positions)
                    {
                        if (p == null || p.Instrument == null || p.MarketPosition == MarketPosition.Flat)
                            continue;
                        open++;
                        sb.Append("  ").Append(p.MarketPosition == MarketPosition.Long ? "Long " : "Short ")
                          .Append(Num(p.Quantity))
                          .Append(" <code>").Append(Html(p.Instrument.FullName)).Append("</code> @ ")
                          .Append(Html(FormatPriceSafe(p.Instrument, p.AveragePrice))).Append("\n");
                    }
                }

                int working = 0;
                lock (a.Orders)
                {
                    foreach (CbiOrder o in a.Orders)
                    {
                        if (o != null && IsWorkingState(o.OrderState))
                            working++;
                    }
                }

                if (open == 0)
                    sb.Append("  Flat\n");
                if (working > 0)
                    sb.Append("  Working orders: ").Append(Num(working)).Append("\n");

                flat = open == 0 && working == 0;
            }
            catch (Exception ex)
            {
                flat = false;
                sb.Append("  Positions unavailable: ").Append(Html(ex.Message)).Append("\n");
            }
            return sb.ToString();
        }

        private void CheckEndOfDay()
        {
            DateTime nowTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eodTimeZone);
            if (nowTz.TimeOfDay < eodTimeOfDay || lastEodReportDate == nowTz.Date)
                return;

            lastEodReportDate = nowTz.Date;

            // CME futures don't settle a session on Sat/Sun; Sunday-evening activity rolls into Monday's report.
            if (nowTz.DayOfWeek == DayOfWeek.Saturday || nowTz.DayOfWeek == DayOfWeek.Sunday)
                return;

            string report;
            lock (statsLock)
            {
                report = BuildEodReportLocked(nowTz);
                acctStats.Clear();                               // open lots are kept so carried positions still match correctly
                copyStats.Clear();
                sessionStartNt = NtNow();
                tradedInstruments.Clear();
            }

            EnqueueQuiet(report + BuildBrokerBlock());
            WriteOutput("EOD summary queued.");
        }

        private string BuildBrokerBlock()
        {
            List<CbiAccount> accounts = SnapshotTrackedAccounts();
            if (accounts.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n<b>Broker-reported realized P&amp;L</b>\n");
            foreach (CbiAccount a in accounts)
                sb.Append(Html(a.Name)).Append(": ").Append(Money(SafeAccountValue(a, AccountItem.RealizedProfitLoss))).Append("\n");
            return sb.ToString().TrimEnd('\n');
        }

        private string BuildEodReportLocked(DateTime nowTz)
        {
            string leaderName = ResolveLeaderName();

            StringBuilder sb = new StringBuilder();
            sb.Append(EmChart).Append(" <b>END OF DAY</b>").Append(Sep).Append(nowTz.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("\n");
            sb.Append("<i>Since ").Append(sessionStartNt.ToString("ddd HH:mm", CultureInfo.InvariantCulture)).Append(" (NinjaTrader time)</i>\n");

            List<KeyValuePair<string, SessionStats>> accounts = acctStats
                .OrderBy(kv => kv.Key.Equals(leaderName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (accounts.Count == 0)
                sb.Append("\nNo trades.");

            foreach (KeyValuePair<string, SessionStats> kv in accounts)
            {
                SessionStats s = kv.Value;
                sb.Append("\n<b>").Append(Html(kv.Key)).Append("</b>");
                if (kv.Key.Equals(leaderName, StringComparison.OrdinalIgnoreCase) && AccountFilterHasSeveral())
                    sb.Append(" (leader)");
                sb.Append("\n");

                sb.Append("Trades ").Append(Num(s.Trades)).Append(Sep)
                  .Append("W ").Append(Num(s.Wins)).Append(" / L ").Append(Num(s.Losses)).Append(" / BE ").Append(Num(s.Scratches));
                if (s.Trades > 0)
                    sb.Append(Sep).Append((100.0 * s.Wins / s.Trades).ToString("0", CultureInfo.InvariantCulture)).Append("% win");
                sb.Append("\n");

                if (s.Commission > 0)
                    sb.Append("Gross ").Append(Money(s.Gross)).Append(Sep).Append("Comm ").Append(Money(-s.Commission))
                      .Append(Sep).Append("<b>Net ").Append(Money(s.Gross - s.Commission)).Append("</b>\n");
                else
                    sb.Append("<b>Gross ").Append(Money(s.Gross)).Append("</b>").Append(Sep).Append("<i>fees not in fills</i>\n");

                if (s.Wins > 0 || s.Losses > 0)
                {
                    List<string> extremes = new List<string>();
                    if (s.Wins > 0)
                        extremes.Add("Best " + Money(s.LargestWin));
                    if (s.Losses > 0)
                        extremes.Add("Worst " + Money(s.LargestLoss));
                    sb.Append(string.Join(Sep, extremes.ToArray())).Append("\n");
                }

                sb.Append("Executions ").Append(Num(s.Executions)).Append(" (").Append(Num(s.Contracts)).Append(" contracts)\n");
            }

            string quality = BuildCopyQualityBlock();
            if (quality.Length > 0)
                sb.Append("\n").Append(quality);

            List<PositionBook> open = books.Values.Where(b => b.Net != 0).ToList();
            if (open.Count > 0)
            {
                sb.Append("\n<b>Still open</b> (not counted as trades)\n");
                foreach (PositionBook b in open)
                {
                    sb.Append(Html(b.AccountName)).Append(Sep).Append(b.Net > 0 ? "Long " : "Short ").Append(Num(Math.Abs(b.Net)))
                      .Append(" <code>").Append(Html(b.InstrumentName)).Append("</code>\n");
                }
            }
            return sb.ToString().TrimEnd('\n');
        }

        #endregion
    }
}
