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

// DirectTelegramNotifier: Watchdogs: connections, UI thread, restarts, external heartbeat, orders, strategies, data feeds, trade copier.
// One part of a partial class: see DirectTelegramNotifier.cs for the overview.

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier
    {
        #region Watchdog: connections and UI thread

        private void OnConnectionStatusUpdate(object sender, ConnectionStatusEventArgs e)
        {
            try
            {
                if (terminated || e == null || e.Connection == null)
                    return;

                if (e.Status == ConnectionStatus.Connected && e.PreviousStatus != ConnectionStatus.Connected)
                    Task.Run(new Action(AttachAccounts));

                if (!cfgEnableWatchdog)
                    return;

                string name = ConnectionName(e.Connection);
                string detail = string.Empty;
                if (e.Error != ErrorCode.NoError)
                    detail = e.Error.ToString();
                if (!string.IsNullOrEmpty(e.NativeError))
                    detail += (detail.Length > 0 ? " - " : string.Empty) + e.NativeError;

                EvaluateChannel(name, "orders", e.PreviousStatus, e.Status, detail);
                EvaluateChannel(name, "data", e.PreviousPriceStatus, e.PriceStatus, detail);
            }
            catch (Exception ex)
            {
                WriteError("ConnectionStatusUpdate", ex);
            }
        }

        private void EvaluateChannel(string connection, string channel, ConnectionStatus previous, ConnectionStatus current, string detail)
        {
            if (previous == current)
                return;

            string key = connection + "|" + channel;
            bool isDown = current == ConnectionStatus.Disconnected || current == ConnectionStatus.ConnectionLost;

            lock (connLock)
            {
                if (isDown)
                {
                    if (connDownSinceUtc.ContainsKey(key))
                        return;
                    connDownSinceUtc[key] = DateTime.UtcNow;
                    if (SuppressAsFlapping(key, connection, channel))
                        return;

                    AddConnectionNotice(connection, channel, true, previous == ConnectionStatus.Disconnecting,
                        current == ConnectionStatus.ConnectionLost, detail, TimeSpan.Zero);
                }
                else if (current == ConnectionStatus.Connected)
                {
                    DateTime since;
                    if (!connDownSinceUtc.TryGetValue(key, out since))
                        return;
                    connDownSinceUtc.Remove(key);

                    DateTime mutedUntil;
                    if (connMutedUntilUtc.TryGetValue(key, out mutedUntil) && mutedUntil > DateTime.UtcNow)
                        return;

                    AddConnectionNotice(connection, channel, false, false, false, null, DateTime.UtcNow - since);
                }
            }
        }

        // Collects the events of one connection for a moment so "orders" and "data" go out as a single message.
        private void AddConnectionNotice(string connection, string channel, bool down, bool userInitiated, bool lost, string detail, TimeSpan downtime)
        {
            string key = connection + "|" + (down ? "down" : "up");
            bool schedule = false;

            lock (noticeLock)
            {
                ConnNotice n;
                if (!connNotices.TryGetValue(key, out n))
                {
                    n = new ConnNotice { Connection = connection, Down = down, UserInitiated = userInitiated, Time = NtNow() };
                    connNotices[key] = n;
                    schedule = true;
                }
                if (!n.Channels.Contains(channel))
                    n.Channels.Add(channel);
                n.UserInitiated = n.UserInitiated && userInitiated;
                n.Lost = n.Lost || lost;
                if (downtime > n.Downtime)
                    n.Downtime = downtime;
                if (string.IsNullOrEmpty(n.Detail) && !string.IsNullOrEmpty(detail))
                    n.Detail = detail;
            }

            if (schedule)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(ConnectionMergeMs);
                    ConnNotice ready;
                    lock (noticeLock)
                    {
                        connNotices.TryGetValue(key, out ready);
                        connNotices.Remove(key);
                    }
                    if (ready != null)
                    {
                        if (ready.Down)
                            EnqueueText(BuildConnectionMessage(ready));
                        else
                            EnqueueQuiet(BuildConnectionMessage(ready));
                    }
                });
            }
        }

        private string BuildConnectionMessage(ConnNotice n)
        {
            StringBuilder sb = new StringBuilder();
            string channels = string.Join(" + ", n.Channels.ToArray());

            if (n.Down)
            {
                string title = n.UserInitiated ? "DISCONNECTED (by you)" : (n.Lost ? "CONNECTION LOST" : "DISCONNECTED");
                sb.Append(n.UserInitiated ? EmInfo : EmWarn).Append(" <b>").Append(title).Append("</b>\n");
                sb.Append(Html(n.Connection)).Append(Sep).Append(Html(channels)).Append("\n");
                if (!string.IsNullOrEmpty(n.Detail))
                    sb.Append(Html(Truncate(n.Detail, 200))).Append("\n");
                sb.Append(TimeText(n.Time));
            }
            else
            {
                sb.Append(EmOk).Append(" <b>RECONNECTED</b>\n");
                sb.Append(Html(n.Connection)).Append(Sep).Append(Html(channels)).Append("\n");
                sb.Append("Down for ").Append(FormatDuration(n.Downtime));
            }
            return sb.ToString();
        }

        // Posts a no-op to NinjaTrader's main UI dispatcher; if it isn't executed within UiFreezeAlertSec, the UI is hung.
        private void CheckUiResponsiveness()
        {
            Dispatcher d = uiDispatcher;
            if (d == null || d.HasShutdownStarted)
                return;

            long now  = DateTime.UtcNow.Ticks;
            long sent = Interlocked.Read(ref uiProbeSentTicks);
            long pong = Interlocked.Read(ref uiPongTicks);

            if (sent == 0 || pong >= sent)
            {
                if (uiFreezeAlerted)
                {
                    uiFreezeAlerted = false;
                    EnqueueQuiet(MsgUiRecovered(TimeSpan.FromTicks(pong - sent)));
                }

                Interlocked.Exchange(ref uiProbeSentTicks, now);
                try
                {
                    d.BeginInvoke(DispatcherPriority.Normal, new Action(() => Interlocked.Exchange(ref uiPongTicks, DateTime.UtcNow.Ticks)));
                }
                catch (Exception ex)
                {
                    WriteError("UI probe", ex);
                }
            }
            else
            {
                double waited = TimeSpan.FromTicks(now - sent).TotalSeconds;
                if (waited > UiFreezeAlertSec && !uiFreezeAlerted)
                {
                    uiFreezeAlerted = true;
                    EnqueueText(MsgUiFrozen((int)waited));
                }
            }
        }

        #endregion

        #region Restart detection, heartbeat and order monitoring

        // ---- Connection flap suppression (called with connLock held) ----

        private bool SuppressAsFlapping(string key, string connection, string channel)
        {
            DateTime now = DateTime.UtcNow;
            List<DateTime> hist;
            if (!connDownHistory.TryGetValue(key, out hist))
            {
                hist = new List<DateTime>();
                connDownHistory[key] = hist;
            }
            hist.RemoveAll(t => (now - t).TotalSeconds > FlapWindowSec);
            hist.Add(now);

            DateTime mutedUntil;
            if (connMutedUntilUtc.TryGetValue(key, out mutedUntil) && mutedUntil > now)
                return true;

            if (hist.Count >= FlapThreshold)
            {
                connMutedUntilUtc[key] = now.AddSeconds(FlapMuteSec);
                EnqueueText(MsgFlapping(connection, channel, hist.Count));
                return true;
            }
            return false;
        }

        // ---- Direct, synchronous send used for the shutdown message ----

        private void SendBlocking(string text, int timeoutMs)
        {
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(timeoutMs))
                {
                    LogAlert(text);
                    OutboundMessage msg = new OutboundMessage { Text = text, CreatedUtc = DateTime.UtcNow };
                    Task<SendOutcome> task = Task.Run(() => SendAsync(msg, cts.Token));
                    task.Wait(timeoutMs + 500);
                }
            }
            catch (Exception ex)
            {
                WriteOutput("Stop message not delivered: " + Describe(ex));
            }
        }

        // ---- Restart / gap detection (local heartbeat file) ----

        private static string HeartbeatPath()
        {
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigPath()), HeartbeatFileName);
        }

        private string ReadPreviousHeartbeat()
        {
            try
            {
                if (string.IsNullOrEmpty(heartbeatPath) || !File.Exists(heartbeatPath))
                    return string.Empty;

                string[] lines = File.ReadAllLines(heartbeatPath);
                long ticks;
                if (lines.Length < 1
                    || !long.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)
                    || ticks <= 0)
                    return string.Empty;

                bool clean = lines.Length > 1 && lines[1].Trim().Equals("CLEAN", StringComparison.OrdinalIgnoreCase);
                TimeSpan gap = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                if (gap.TotalSeconds < cfgRestartGapThresholdSec)
                    return string.Empty;

                DateTime nowNt = NtNow();
                return MsgRestart(clean, gap, nowNt - gap, nowNt);
            }
            catch (Exception ex)
            {
                WriteError("Reading previous heartbeat", ex);
                return string.Empty;
            }
        }

        private void WriteHeartbeatFile(bool clean)
        {
            if (string.IsNullOrEmpty(heartbeatPath))
                return;
            if (terminated && !clean)
                return;
            try
            {
                File.WriteAllText(heartbeatPath,
                    DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\r\n" + (clean ? "CLEAN" : "RUNNING") + "\r\n");
            }
            catch (Exception ex)
            {
                WriteThrottled("Heartbeat file write failed: " + Describe(ex));
            }
        }

        // ---- External dead-man's switch (optional) ----

        // Null when alerts are getting through; otherwise why not.
        private string DeliveryProblem(DateTime now)
        {
            DateTime lastOk = lastSendOkUtc;
            if (lastPollOkUtc > lastOk) lastOk = lastPollOkUtc;
            if (lastProbeOkUtc > lastOk) lastOk = lastProbeOkUtc;

            double quietSec = (now - lastOk).TotalSeconds;
            if (quietSec > DeliveryStaleSec / 2)
                ProbeTelegram();                                // no recent traffic to prove it: ask Telegram directly
            if (quietSec > DeliveryStaleSec)
                return lastOk == DateTime.MinValue ? "no successful Telegram call yet" : "no successful Telegram call for " + FormatDuration(now - lastOk);

            double waiting = OldestQueuedSeconds();
            if (waiting > DeliveryStaleSec)
                return "alerts have been waiting " + FormatDuration(TimeSpan.FromSeconds(waiting)) + " to send";
            return null;
        }

        // getMe: cheap proof that the token works and Telegram is reachable.
        private void ProbeTelegram()
        {
            if (string.IsNullOrEmpty(resolvedToken) || Interlocked.CompareExchange(ref probeBusy, 1, 0) != 0)
                return;
            Task.Run(async () =>
            {
                try
                {
                    using (CancellationTokenSource cts = new CancellationTokenSource(10000))
                    using (HttpResponseMessage resp = await Http.GetAsync(ApiUrl("getMe"), cts.Token))
                    {
                        if (resp.IsSuccessStatusCode)
                            lastProbeOkUtc = DateTime.UtcNow;
                        else
                            WriteThrottled("Telegram check (getMe) returned HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                    }
                }
                catch (Exception ex)
                {
                    if (!terminated)
                        WriteThrottled("Telegram check (getMe) failed: " + Describe(ex));
                }
                finally
                {
                    Interlocked.Exchange(ref probeBusy, 0);
                }
            });
        }

        private void PingExternalHeartbeat()
        {
            string url = cfgHeartbeatUrl;
            if (string.IsNullOrEmpty(url) || uiFreezeAlerted)          // a frozen UI deliberately stops the pings
                return;

            DateTime now = DateTime.UtcNow;
            if ((now - lastHeartbeatPingUtc).TotalSeconds < cfgHeartbeatIntervalSec)
                return;

            // The ping vouches for alert delivery, not just for this timer: while Telegram is unreachable, the token
            // is revoked or alerts are stuck in the queue, it is held back so the external monitor raises the alarm.
            string problem = DeliveryProblem(now);
            if (problem != null)
            {
                if (problem != heartbeatHoldReason)
                    WriteOutput("External heartbeat paused: " + problem + ".");
                heartbeatHoldReason = problem;
                return;
            }
            if (heartbeatHoldReason.Length > 0)
            {
                WriteOutput("External heartbeat resumed.");
                heartbeatHoldReason = string.Empty;
            }

            if (Interlocked.CompareExchange(ref heartbeatPingBusy, 1, 0) != 0)
                return;
            lastHeartbeatPingUtc = now;

            Task.Run(async () =>
            {
                try
                {
                    using (CancellationTokenSource cts = new CancellationTokenSource(10000))
                    using (HttpResponseMessage resp = await Http.GetAsync(url, cts.Token))
                    {
                        if (!resp.IsSuccessStatusCode)
                            WriteThrottled("External heartbeat ping returned HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                    }
                }
                catch (Exception ex)
                {
                    if (!terminated)
                        WriteThrottled("External heartbeat ping failed: " + Describe(ex));
                }
                finally
                {
                    Interlocked.Exchange(ref heartbeatPingBusy, 0);
                }
            });
        }

        // ---- Rejected orders and order-rate (runaway) detection ----

        private void ScanOrders()
        {
            try
            {
                List<string> newOrders = new List<string>();

                foreach (CbiAccount a in SnapshotTrackedAccounts())
                {
                    List<CbiOrder> orders;
                    lock (a.Orders)
                        orders = a.Orders.ToList();

                    bool baseline;
                    lock (orderLock)
                        baseline = baselinedAccounts.Add(a.Name);       // first scan of an account only records what already exists

                    HashSet<string> present = new HashSet<string>();
                    foreach (CbiOrder o in orders)
                    {
                        if (o == null)
                            continue;

                        string id = a.Name + "|" + (string.IsNullOrEmpty(o.OrderId)
                            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString(CultureInfo.InvariantCulture)
                            : o.OrderId);
                        present.Add(id);

                        if (cfgStuckOrderSec > 0)
                            CheckStuckOrder(a, o, id);

                        bool firstSeen;
                        bool rejectIsNew = false;
                        lock (orderLock)
                        {
                            firstSeen = seenOrderIds.Add(id);
                            if (o.OrderState == OrderState.Rejected)
                                rejectIsNew = rejectedAlerted.Add(id);
                        }

                        if (baseline)
                            continue;

                        if (firstSeen)
                            newOrders.Add(o.Instrument != null ? o.Instrument.FullName : "?");

                        if (cfgEnableOrderRejectAlerts && rejectIsNew)
                            EnqueueText(BuildRejectMessage(a, o));
                    }

                    PruneOrderIds(a.Name, present);
                }

                if (cfgRunawayMaxOrders > 0)
                    CheckRunaway(newOrders);
            }
            catch (Exception ex)
            {
                WriteError("ScanOrders", ex);
            }
        }

        private string BuildRejectMessage(CbiAccount a, CbiOrder o)
        {
            string reason = FirstUsefulText(GetMember(o, "Comment"), GetMember(o, "Error"), GetMember(o, "NativeError"));
            return MsgReject(a.Name, o.Instrument != null ? o.Instrument.FullName : "?", o.OrderAction.ToString(), o.OrderType.ToString(),
                Convert.ToString(o.Quantity, CultureInfo.InvariantCulture), o.Name, reason);
        }

        private void CheckRunaway(List<string> newOrders)
        {
            DateTime now = DateTime.UtcNow;
            bool alert = false;
            int count = 0;
            string breakdown = string.Empty;

            lock (orderLock)
            {
                foreach (string instrument in newOrders)
                    recentOrders.Add(new KeyValuePair<DateTime, string>(now, instrument));
                recentOrders.RemoveAll(k => (now - k.Key).TotalSeconds > cfgRunawayWindowSec);

                count = recentOrders.Count;
                if (count >= cfgRunawayMaxOrders && (now - lastRunawayAlertUtc).TotalSeconds >= RunawayCooldownSec)
                {
                    alert = true;
                    lastRunawayAlertUtc = now;
                    breakdown = string.Join(", ", recentOrders
                        .GroupBy(k => k.Value)
                        .Select(g => g.Key + " \u00D7" + g.Count().ToString(CultureInfo.InvariantCulture))
                        .ToArray());
                }
            }

            if (alert)
            {
                EnqueueText(MsgOrderRate(count, cfgRunawayWindowSec, cfgRunawayMaxOrders, breakdown));
            }
        }

        private static object GetMember(object target, string name)
        {
            if (target == null)
                return null;
            try
            {
                System.Reflection.PropertyInfo p = target.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                return p != null ? p.GetValue(target, null) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FirstUsefulText(params object[] values)
        {
            foreach (object v in values)
            {
                if (v == null)
                    continue;
                string text = Convert.ToString(v, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)
                    && !text.Equals("NoError", StringComparison.OrdinalIgnoreCase)
                    && !text.Equals("None", StringComparison.OrdinalIgnoreCase))
                    return text.Trim();
            }
            return string.Empty;
        }

        #endregion

        #region Strategy, data-feed and trade-copier monitoring

        // ---- Strategy watch (documented Account.Strategies collection) ----

        private List<KeyValuePair<string, StrategyBase>> ReadStrategies(out string problem)
        {
            problem = null;
            List<CbiAccount> accounts = SnapshotTrackedAccounts();
            if (accounts.Count == 0)
                return null;

            List<KeyValuePair<string, StrategyBase>> result = new List<KeyValuePair<string, StrategyBase>>();
            foreach (CbiAccount a in accounts)
            {
                try
                {
                    if (a.Strategies == null)
                        continue;
                    lock (a.Strategies)
                    {
                        foreach (StrategyBase st in a.Strategies)
                        {
                            if (st != null)
                                result.Add(new KeyValuePair<string, StrategyBase>(a.Name, st));
                        }
                    }
                }
                catch (Exception ex)
                {
                    problem = Describe(ex);
                    return null;
                }
            }
            return result;
        }

        private static string SafeStateText(StrategyBase st)
        {
            try { return st.State.ToString(); }
            catch (Exception) { return "?"; }
        }

        private static string SafeStrategyName(StrategyBase st)
        {
            try { return string.IsNullOrEmpty(st.Name) ? st.GetType().Name : st.Name; }
            catch (Exception) { return "?"; }
        }

        private static bool IsTerminatedState(string state)
        {
            return string.Equals(state, "Terminated", StringComparison.OrdinalIgnoreCase);
        }

        // Alerts on strategies that appear, get disabled, or disappear. The first scan is a silent baseline.
        private void CheckStrategies()
        {
            try
            {
                string problem;
                List<KeyValuePair<string, StrategyBase>> now = ReadStrategies(out problem);
                if (now == null)
                {
                    if (problem != null && !strategyProblemReported)
                    {
                        strategyProblemReported = true;
                        WriteOutput("Strategy watch could not read Account.Strategies: " + problem);
                        EnqueueText(EmInfo + " <b>Strategy watch unavailable</b>\n" + Html(Truncate(problem, 200)));
                    }
                    return;
                }

                List<string> messages = new List<string>();
                List<string> quietMessages = new List<string>();
                lock (strategyLock)
                {
                    bool firstScan = !strategyBaselined;
                    HashSet<object> present = new HashSet<object>(new RefComparer());

                    foreach (KeyValuePair<string, StrategyBase> kv in now)
                    {
                        object key = kv.Value;
                        present.Add(key);
                        string state = SafeStateText(kv.Value);
                        string name = SafeStrategyName(kv.Value);

                        StrategyInfo info;
                        if (!strategies.TryGetValue(key, out info))
                        {
                            strategies[key] = new StrategyInfo { Ref = key, Name = name, AccountName = kv.Key, State = state };
                            if (!firstScan && !IsTerminatedState(state))
                            {
                                quietMessages.Add(MsgStrategyEnabled(name, kv.Key, state));
                            }
                            continue;
                        }

                        if (!string.Equals(info.State, state, StringComparison.Ordinal))
                        {
                            string previous = info.State;
                            info.State = state;
                            info.Name = name;
                            if (IsTerminatedState(state) && !IsTerminatedState(previous))
                            {
                                messages.Add(MsgStrategyDisabled(name, kv.Key, previous));
                            }
                        }
                    }

                    foreach (StrategyInfo gone in strategies.Values.Where(x => !present.Contains(x.Ref)).ToList())
                    {
                        strategies.Remove(gone.Ref);
                        if (!IsTerminatedState(gone.State))
                        {
                            messages.Add(MsgStrategyRemoved(gone.Name, gone.AccountName, gone.State));
                        }
                    }

                    strategyBaselined = true;
                }

                foreach (string text in messages)
                    EnqueueText(text);
                foreach (string text in quietMessages)
                    EnqueueQuiet(text);
            }
            catch (Exception ex)
            {
                WriteError("CheckStrategies", ex);
            }
        }

        private string BuildStrategiesBlock()
        {
            if (!cfgEnableStrategyWatch)
                return "  watch is off\n";

            StringBuilder sb = new StringBuilder();
            lock (strategyLock)
            {
                if (!strategyBaselined)
                    return "  not scanned yet\n";

                int running = 0;
                int disabled = 0;
                foreach (StrategyInfo si in strategies.Values.OrderBy(x => x.Name))
                {
                    if (IsTerminatedState(si.State))
                    {
                        disabled++;
                        continue;
                    }
                    running++;
                    sb.Append("  \u2022 <code>").Append(Html(si.Name)).Append("</code>").Append(Sep)
                      .Append(Html(si.AccountName)).Append(Sep).Append(Html(si.State)).Append("\n");
                }
                if (running == 0)
                    sb.Append("  none running\n");
                if (disabled > 0)
                    sb.Append("  ").Append(Num(disabled)).Append(" disabled\n");
            }
            return sb.ToString();
        }

        // ---- Stale market data ("Connected" but no ticks) ----

        private void CheckStaleData()
        {
            if (cfgStaleDataSeconds <= 0)
                return;
            try
            {
                DateTime nowUtc = DateTime.UtcNow;

                // While the market is closed or a data connection is down, other alerts apply. Moving the
                // grace epoch forward restarts the no-tick clock when things come back.
                if (!IsMarketOpen(nowUtc) || !AllDataConnectionsUp())
                {
                    staleGraceEpochUtc = nowUtc;
                    return;
                }

                Dictionary<string, CbiInstrument> wanted = WantedInstruments();
                foreach (KeyValuePair<string, CbiInstrument> kv in wanted)
                {
                    FeedWatch created = null;
                    lock (feedLock)
                    {
                        if (!feeds.ContainsKey(kv.Key))
                        {
                            created = new FeedWatch { Instrument = kv.Value, Name = kv.Key, LastTickTicks = nowUtc.Ticks };
                            feeds[kv.Key] = created;
                        }
                    }
                    if (created != null)
                        SubscribeFeed(created);
                }

                HashSet<string> exposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CbiAccount acc in SnapshotTrackedAccounts())
                {
                    foreach (CbiInstrument inst in CollectExposedInstruments(acc))
                        exposed.Add(inst.FullName);
                }

                List<FeedWatch> watches;
                lock (feedLock)
                    watches = feeds.Values.ToList();

                foreach (FeedWatch w in watches)
                {
                    if (!w.Subscribed)
                        continue;

                    long lastTicks = Math.Max(Interlocked.Read(ref w.LastTickTicks), staleGraceEpochUtc.Ticks);
                    DateTime last = new DateTime(lastTicks, DateTimeKind.Utc);
                    double silent = (nowUtc - last).TotalSeconds;

                    if (w.Stale)
                    {
                        if (silent < cfgStaleDataSeconds)
                        {
                            w.Stale = false;
                            EnqueueQuiet(MsgDataResumed(w.Name, nowUtc - w.StaleSinceUtc));
                        }
                    }
                    else if (wanted.ContainsKey(w.Name) && silent >= cfgStaleDataSeconds)
                    {
                        w.Stale = true;
                        w.StaleSinceUtc = last;
                        EnqueueText(MsgStale(w.Name, nowUtc - last, exposed.Contains(w.Name)));
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("CheckStaleData", ex);
            }
        }

        // Instruments worth watching: the configured extras, anything held or working, anything traded since the last EOD report.
        private Dictionary<string, CbiInstrument> WantedInstruments()
        {
            Dictionary<string, CbiInstrument> map = new Dictionary<string, CbiInstrument>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in (cfgStaleDataInstruments ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (name.Length == 0)
                    continue;
                CbiInstrument configured = null;
                try { configured = CbiInstrument.GetInstrument(name); }
                catch (Exception) { }
                if (configured != null)
                    map[configured.FullName] = configured;
                else
                    WriteThrottled("StaleDataInstruments: unknown instrument '" + name + "'.");
            }

            foreach (CbiAccount acc in SnapshotTrackedAccounts())
            {
                foreach (CbiInstrument inst in CollectExposedInstruments(acc))
                    map[inst.FullName] = inst;
            }

            lock (statsLock)
            {
                foreach (KeyValuePair<string, CbiInstrument> kv in tradedInstruments)
                    map[kv.Key] = kv.Value;
            }
            return map;
        }

        private void SubscribeFeed(FeedWatch w)
        {
            try
            {
                CbiInstrument inst = w.Instrument;
                if (inst == null || inst.Dispatcher == null || inst.Dispatcher.HasShutdownStarted)
                    return;

                inst.Dispatcher.InvokeAsync(new Action(delegate
                {
                    try
                    {
                        if (terminated)
                            return;
                        w.LastTickTicks = DateTime.UtcNow.Ticks;
                        inst.MarketData.Update += w.OnTick;
                        w.Subscribed = true;
                    }
                    catch (Exception ex)
                    {
                        WriteError("Subscribe market data " + w.Name, ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                WriteError("SubscribeFeed", ex);
            }
        }

        private void StopFeedWatches()
        {
            List<FeedWatch> list;
            lock (feedLock)
            {
                list = feeds.Values.ToList();
                feeds.Clear();
            }

            foreach (FeedWatch w in list)
            {
                try
                {
                    CbiInstrument inst = w.Instrument;
                    if (!w.Subscribed || inst == null || inst.Dispatcher == null || inst.Dispatcher.HasShutdownStarted)
                        continue;

                    FeedWatch captured = w;
                    inst.Dispatcher.InvokeAsync(new Action(delegate
                    {
                        try
                        {
                            inst.MarketData.Update -= captured.OnTick;
                            captured.Subscribed = false;
                        }
                        catch (Exception) { }
                    }));
                }
                catch (Exception ex)
                {
                    WriteError("StopFeedWatches", ex);
                }
            }
        }

        private static bool AllDataConnectionsUp()
        {
            try
            {
                List<CbiConnection> list;
                lock (CbiConnection.Connections)
                    list = CbiConnection.Connections.ToList();

                bool any = false;
                foreach (CbiConnection c in list)
                {
                    if (c == null)
                        continue;
                    any = true;
                    if (c.PriceStatus != ConnectionStatus.Connected)
                        return false;
                }
                return any;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Outside the schedule's quiet windows (CME hours by default) the feed is expected to tick.
        private bool IsMarketOpen(DateTime utc)
        {
            if (staleScheduleAlways)
                return true;

            if (marketZone == null)
                marketZone = ResolveTimeZone(staleZoneId);

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, marketZone);
            return !Dtn.InQuietWindow(local, staleQuietWindows);
        }

        private void ApplyStaleSchedule(List<string> warnings)
        {
            string schedule = (cfgStaleDataSchedule ?? string.Empty).Trim().ToLowerInvariant();
            string spec;
            staleScheduleAlways = false;
            staleZoneId = "Eastern Standard Time";              // the cme windows are US Eastern

            if (schedule == "always")
            {
                staleScheduleAlways = true;
                spec = string.Empty;
            }
            else if (schedule == "custom")
            {
                spec = cfgStaleDataQuietWindows;
                staleZoneId = string.IsNullOrWhiteSpace(cfgStaleDataTimeZone) ? "Eastern Standard Time" : cfgStaleDataTimeZone.Trim();
                if (string.IsNullOrWhiteSpace(spec))
                    warnings.Add("StaleDataSchedule=custom but StaleDataQuietWindows is empty: stale data is checked around the clock");
            }
            else
            {
                if (schedule != "cme")
                    warnings.Add("StaleDataSchedule '" + cfgStaleDataSchedule + "' is not cme, custom or always; using cme");
                spec = Dtn.CmeQuietWindows;
            }

            string error;
            List<Dtn.QuietWindow> windows = Dtn.ParseQuietWindows(spec, out error);
            if (error != null)
            {
                warnings.Add("StaleDataQuietWindows: " + error + "; using the cme schedule");
                windows = Dtn.ParseQuietWindows(Dtn.CmeQuietWindows, out error);
                staleZoneId = "Eastern Standard Time";
            }
            staleQuietWindows = windows;
            marketZone = null;                                  // resolved again on first use
        }

        private string BuildFeedLines()
        {
            List<FeedWatch> list;
            lock (feedLock)
                list = feeds.Values.Where(x => x.Subscribed).ToList();
            if (list.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n<b>Data feeds</b>\n");
            foreach (FeedWatch w in list.OrderBy(x => x.Name))
            {
                TimeSpan age = DateTime.UtcNow - new DateTime(Interlocked.Read(ref w.LastTickTicks), DateTimeKind.Utc);
                sb.Append("  <code>").Append(Html(w.Name)).Append("</code> last tick ").Append(FormatDuration(age)).Append(" ago");
                if (w.Stale)
                    sb.Append(" ").Append(EmWarn);
                sb.Append("\n");
            }
            return sb.ToString();
        }

        // ---- Trade-copier check: followers should mirror the leader ----

        private string ResolveLeaderName()
        {
            string chosen;
            lock (riskLock)
                chosen = leaderOverride;
            if (!string.IsNullOrEmpty(chosen))
                return chosen;

            string leader = (cfgLeaderAccount ?? string.Empty).Trim();
            if (leader.Length > 0)
                return leader;

            string filter = (cfgAccountName ?? string.Empty).Trim();
            if (filter.Length == 0 || filter == "*")
                return string.Empty;
            return filter.Split(',')[0].Trim();
        }

        private string AccountListText()
        {
            string filter = (cfgAccountName ?? string.Empty).Trim();
            if (filter.Length == 0 || filter == "*")
                return "all";
            return string.Join(", ", filter.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToArray());
        }

        private bool AccountFilterHasSeveral()
        {
            string filter = (cfgAccountName ?? string.Empty).Trim();
            return filter == "*" || filter.IndexOf(',') >= 0;
        }

        private static Dictionary<string, int> NetPositions(CbiAccount a)
        {
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                lock (a.Positions)
                {
                    foreach (CbiPosition p in a.Positions)
                    {
                        if (p == null || p.Instrument == null || p.MarketPosition == MarketPosition.Flat)
                            continue;
                        int signed = p.MarketPosition == MarketPosition.Long ? p.Quantity : -p.Quantity;
                        int existing;
                        map.TryGetValue(p.Instrument.FullName, out existing);
                        map[p.Instrument.FullName] = existing + signed;
                    }
                }
            }
            catch (Exception) { }
            return map;
        }

        private static string FormatNet(int net)
        {
            if (net == 0)
                return "Flat";
            return net > 0 ? "Long " + Num(net) : "Short " + Num(-net);
        }

        private void CheckCopyMismatch()
        {
            if (cfgMismatchSeconds <= 0)
                return;
            try
            {
                List<CbiAccount> accounts = SnapshotTrackedAccounts();
                if (accounts.Count < 2)
                    return;

                string leaderName = ResolveLeaderName();
                if (leaderName.Length == 0)
                {
                    if (!leaderProblemReported)
                    {
                        leaderProblemReported = true;
                        WriteOutput("Copy check is off: set LeaderAccount in the config (AccountName is *).");
                    }
                    return;
                }

                CbiAccount leader = accounts.FirstOrDefault(acc => acc.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase));
                if (leader == null)
                {
                    if (!leaderProblemReported)
                    {
                        leaderProblemReported = true;
                        WriteOutput("Copy check is waiting: leader account '" + leaderName + "' is not tracked or not connected.");
                    }
                    return;
                }
                leaderProblemReported = false;

                List<CbiAccount> followers = accounts.Where(acc => !ReferenceEquals(acc, leader)).ToList();
                Dictionary<string, int> leaderNet = NetPositions(leader);
                Dictionary<string, Dictionary<string, int>> followerNet = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                foreach (CbiAccount fa in followers)
                    followerNet[fa.Name] = NetPositions(fa);

                HashSet<string> instruments = new HashSet<string>(leaderNet.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, int> nets in followerNet.Values)
                {
                    foreach (string name in nets.Keys)
                        instruments.Add(name);
                }
                lock (mismatchLock)
                {
                    foreach (string open in mismatches.Keys)
                        instruments.Add(open);
                }

                DateTime now = DateTime.UtcNow;
                List<string> messages = new List<string>();
                List<string> quietMessages = new List<string>();

                foreach (string inst in instruments)
                {
                    int lead;
                    leaderNet.TryGetValue(inst, out lead);
                    int expected = (int)Math.Round(lead * cfgFollowerRatio, MidpointRounding.AwayFromZero);

                    List<string> diffs = new List<string>();
                    foreach (CbiAccount fa in followers)
                    {
                        int have;
                        followerNet[fa.Name].TryGetValue(inst, out have);
                        if (have != expected)
                            diffs.Add(Html(fa.Name) + ": " + FormatNet(have) + ", expected " + FormatNet(expected));
                    }

                    string signature = Num(lead) + "|" + string.Join(";", diffs.ToArray());

                    lock (mismatchLock)
                    {
                        MismatchState st;
                        if (diffs.Count == 0)
                        {
                            if (mismatches.TryGetValue(inst, out st))
                            {
                                mismatches.Remove(inst);
                                if (st.Alerted)
                                    quietMessages.Add(MsgInSync(inst, now - st.FirstUtc));
                            }
                            continue;
                        }

                        if (!mismatches.TryGetValue(inst, out st))
                        {
                            st = new MismatchState { Signature = signature, SinceUtc = now, FirstUtc = now };
                            mismatches[inst] = st;
                        }
                        else if (st.Signature != signature)
                        {
                            st.Signature = signature;
                            st.SinceUtc = now;
                            st.Alerted = false;
                        }

                        if (!st.Alerted && (now - st.SinceUtc).TotalSeconds >= cfgMismatchSeconds)
                        {
                            st.Alerted = true;
                            messages.Add(MsgMismatch(inst, leader.Name, lead, diffs, now - st.FirstUtc));
                        }
                    }
                }

                foreach (string text in messages)
                    EnqueueText(text);
                foreach (string text in quietMessages)
                    EnqueueQuiet(text);
            }
            catch (Exception ex)
            {
                WriteError("CheckCopyMismatch", ex);
            }
        }

        private string BuildCopyStatusLine()
        {
            if (cfgMismatchSeconds <= 0)
                return string.Empty;

            string leaderName = ResolveLeaderName();
            if (leaderName.Length == 0)
                return string.Empty;

            List<CbiAccount> accounts = SnapshotTrackedAccounts();
            if (accounts.Count < 2)
                return string.Empty;
            if (!accounts.Any(acc => acc.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase)))
                return "Copy check: leader " + Html(leaderName) + " is not connected\n";

            int followers = accounts.Count - 1;
            List<string> open;
            lock (mismatchLock)
                open = mismatches.Keys.ToList();

            return "Copy check: leader " + Html(leaderName) + Sep + Num(followers) + (followers == 1 ? " follower" : " followers") + Sep
                + (open.Count == 0 ? "in sync" : "<b>out of sync</b>: " + Html(string.Join(", ", open.ToArray()))) + "\n";
        }

        // ---- Local alert log ----

        private static string AlertLogPath()
        {
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigPath()), "DirectTelegramNotifier.log");
        }

        private void LogAlert(string text)
        {
            if (!cfgEnableAlertLog || string.IsNullOrEmpty(alertLogPath) || string.IsNullOrEmpty(text))
                return;
            try
            {
                string plain = ToPlainText(text).Replace("\r", string.Empty).Replace("\n", " | ");
                string line = NtNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + plain + Environment.NewLine;

                lock (logLock)
                {
                    FileInfo info = new FileInfo(alertLogPath);
                    if (info.Exists && info.Length > 2 * 1024 * 1024)
                    {
                        string old = alertLogPath + ".old";
                        if (File.Exists(old))
                            File.Delete(old);
                        File.Move(alertLogPath, old);
                    }
                    File.AppendAllText(alertLogPath, line, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // Logging must never interfere with alerting.
            }
        }

        #endregion
    }
}
