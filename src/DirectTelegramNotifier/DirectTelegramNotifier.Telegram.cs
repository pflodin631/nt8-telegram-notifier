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

// DirectTelegramNotifier: Telegram: command polling and dispatch, the send queue, buttons and diagnostics helpers.
// One part of a partial class: see DirectTelegramNotifier.cs for the overview.

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier
    {
        #region Remote commands (getUpdates polling)

        private void PollTimerCallback(object state)
        {
            Task.Run(async () =>
            {
                try
                {
                    await PollOnceAsync();
                }
                catch (OperationCanceledException)
                {
                    // Shutdown or HTTP timeout; the next tick retries.
                }
                catch (Exception ex)
                {
                    if (!terminated)
                        WriteThrottled("Command poll failed: " + Describe(ex));
                }
                finally
                {
                    ReschedulePoll();
                }
            });
        }

        private void ReschedulePoll()
        {
            System.Threading.Timer t = pollTimer;
            if (t == null || terminated)
                return;
            try { t.Change(pollFailed ? PollIntervalMs : PollIdleMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private async Task PollOnceAsync()
        {
            if (terminated || !started || lifetimeCts == null)
                return;

            CancellationToken ct = lifetimeCts.Token;
            string url = !backlogDrained
                ? ApiUrl("getUpdates") + "?offset=-1&timeout=0"
                : ApiUrl("getUpdates") + "?offset=" + updateOffset.ToString(CultureInfo.InvariantCulture)
                  + "&timeout=" + Num(PollTimeoutSec)
                  + "&allowed_updates=" + Uri.EscapeDataString("[\"message\",\"callback_query\"]");

            pollFailed = true;                       // cleared below once a good response is parsed; errors back off for a few seconds
            int code;
            string body;
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(PollHttpTimeoutMs);
                using (HttpResponseMessage resp = await Http.GetAsync(url, timeout.Token))
                {
                    code = (int)resp.StatusCode;
                    body = await resp.Content.ReadAsStringAsync();
                }
            }

            Dictionary<string, object> root = TryParseObject(body);
            if (code != 200 || root == null || !JsonBool(root, "ok"))
            {
                string desc = JsonString(root, "description") ?? ("HTTP " + code.ToString(CultureInfo.InvariantCulture));
                string hint = string.Empty;
                if (code == 409) hint = " Another client or a webhook is consuming this bot token; only one getUpdates consumer is allowed per token.";
                else if (code == 401) hint = " Check BotToken.";
                WriteThrottled("getUpdates failed: " + desc + hint);
                return;
            }

            pollFailed = false;
            lastPollOkUtc = DateTime.UtcNow;

            List<object> updates = JsonList(root, "result") ?? new List<object>();

            if (!backlogDrained)
            {
                // offset=-1 returns only the newest update and makes Telegram forget everything before it.
                long maxId = -1;
                foreach (object u in updates)
                    maxId = Math.Max(maxId, JsonLong(u as Dictionary<string, object>, "update_id"));
                if (maxId >= 0)
                {
                    updateOffset = maxId + 1;
                    WriteOutput("Discarded Telegram messages queued before startup. Stale commands are never executed.");
                }
                backlogDrained = true;
                return;
            }

            foreach (object u in updates)
            {
                Dictionary<string, object> update = u as Dictionary<string, object>;
                if (update == null)
                    continue;

                long updateId = JsonLong(update, "update_id");
                if (updateId >= updateOffset)
                    updateOffset = updateId + 1;               // advanced before handling: a failing command is never re-run

                await HandleUpdateAsync(update);
            }
        }

        private async Task HandleUpdateAsync(Dictionary<string, object> update)
        {
            if (terminated)
                return;

            Dictionary<string, object> callback = JsonObject(update, "callback_query");
            if (callback != null)
            {
                await HandleCallbackAsync(callback);
                return;
            }

            Dictionary<string, object> message = JsonObject(update, "message");
            if (message == null)
                return;

            Dictionary<string, object> from = JsonObject(message, "from");
            long fromId = JsonLong(from, "id");

            if (from == null || fromId != authorizedUserId || JsonBool(from, "is_bot"))
            {
                bool firstTime;
                lock (warnedUnauthorizedIds)
                    firstTime = warnedUnauthorizedIds.Add(fromId);
                LogAlert("AUDIT ignored a message from another Telegram user (id " + fromId.ToString(CultureInfo.InvariantCulture) + ")");
                if (firstTime)
                    WriteOutput("Ignored message from unauthorized Telegram user id " + fromId.ToString(CultureInfo.InvariantCulture)
                        + ". (If that is you, put this number in AuthorizedChatID.)");
                return;
            }

            long sentUnix = JsonLong(message, "date");
            long nowUnix = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            if (sentUnix > 0 && nowUnix - sentUnix > CommandMaxAgeSec)
            {
                WriteOutput("Ignored stale command (" + (nowUnix - sentUnix).ToString(CultureInfo.InvariantCulture) + "s old).");
                return;
            }

            string messageText = JsonString(message, "text");
            string command = ParseCommand(messageText);
            if (command == null)
                return;
            string argument = ParseArgument(messageText);

            WriteOutput("Remote command received: " + command);
            LogAlert("AUDIT command " + command + (argument.Length > 0 ? " " + argument : string.Empty));

            switch (command)
            {
                case "/status":
                    EnqueueReply(BuildStatusReport());
                    break;
                case "/flatten":
                    if (cfgFlattenConfirm)
                        RequestActionConfirmation("flatten", argument);
                    else
                        RunInBackground(() => ExecuteFlattenAsync(argument), "Flatten");    // the 2.5s verification must not hold up other commands
                    break;
                case "/cancel":
                    RunInBackground(() => ExecuteCancelAsync(argument), "Cancel");
                    break;
                case "/lock":
                    if (cfgFlattenConfirm)
                        RequestActionConfirmation("lock", argument);
                    else
                        EnqueueReply(ExecuteLockCommand(argument));
                    break;
                case "/unlock":
                    if (cfgFlattenConfirm)
                        RequestActionConfirmation("unlock", argument);
                    else
                        EnqueueReply(ExecuteUnlockCommand(argument));
                    break;
                case "/selftest":
                    EnqueueReply(BuildSelfTestText(AssessReadiness()));
                    break;
                case "/testbackup":
                    RunInBackground(async () => EnqueueReply(await TestBackupAsync()), "Backup test");
                    break;
                case "/floor":
                    {
                        string floorReply = HandleFloorCommand(argument);
                        if (floorReply != null)
                            EnqueueReply(floorReply);
                    }
                    break;
                case "/risk":
                    {
                        string riskReply = HandleRiskCommand(argument);
                        if (riskReply != null)
                            EnqueueReply(riskReply);
                    }
                    break;
                case "/activity":
                    EnqueueReply(HandleActivityCommand(argument));
                    break;
                case "/leader":
                    EnqueueReply(HandleLeaderCommand(argument));
                    break;
                case "/version":
                    EnqueueReply(EmInfo + " DirectTelegramNotifier v" + AddOnVersion);
                    break;
                case "/diag":
                    EnqueueReply(BuildDiagReport());
                    break;
                case "/copy":
                    EnqueueReply(BuildCopyReport());
                    break;
                case "/eod":
                    EnqueueReply(BuildEodPreview());
                    break;
                case "/simulate":
                    RunSimulation();
                    break;
                case "/help":
                case "/start":
                    EnqueueReply(BuildHelpText());
                    break;
                default:
                    EnqueueReply(EmInfo + " Unknown command <code>" + Html(command) + "</code>. Send /help.");
                    break;
            }
        }

        private static string ParseCommand(string text)
        {
            return Dtn.ParseCommand(text);
        }

        private Task ExecuteFlattenAsync()
        {
            return ExecuteFlattenAsync(null);
        }

        // filter = blank for every tracked account, or part of an account name for just that account.
        private async Task ExecuteFlattenAsync(string filter)
        {
            string problem;
            List<CbiAccount> targets = ResolveAccounts(filter, out problem);
            if (targets == null)
            {
                EnqueueQuiet(EmInfo + " " + Html(problem));
                return;
            }
            bool targeted = !string.IsNullOrWhiteSpace(filter);

            lastFlattenUtc = DateTime.UtcNow;
            reentryAlerted = false;

            // Copiers and strategies can put a position straight back (or copy the close onto an already-flat
            // follower and flip it). For a short hold, anything that opens on these accounts is closed again.
            List<CbiAccount> held = cfgUseGlobalFlattenEverything && !targeted ? SnapshotTrackedAccounts() : targets;
            if (cfgFlattenHoldSec > 0)
            {
                foreach (CbiAccount a in held)
                    SetLock(a.Name, "hold after /flatten", false, cfgFlattenHoldSec);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(EmStop).Append(" <b>FLATTEN SENT</b>\n");

            try
            {
                if (cfgUseGlobalFlattenEverything && !targeted)
                {
                    CbiAccount.FlattenEverything();
                    sb.Append("Scope: <b>ALL accounts</b> (FlattenEverything)\n");
                }
                else
                {
                    sb.Append("Scope: ").Append(targeted ? Html(targets[0].Name) : "tracked accounts").Append("\n");
                    if (targets.Count == 0)
                        sb.Append("No tracked accounts, nothing was sent.\n");

                    foreach (CbiAccount a in targets)
                    {
                        List<CbiInstrument> instruments = CollectExposedInstruments(a);
                        if (instruments.Count == 0)
                        {
                            sb.Append(Html(a.Name)).Append(": already flat\n");
                            continue;
                        }
                        a.Flatten(instruments);
                        sb.Append(Html(a.Name)).Append(": ")
                          .Append(Html(string.Join(", ", instruments.Select(i => i.FullName).ToArray()))).Append("\n");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("Flatten", ex);
                sb.Append(EmWarn).Append(" Error: ").Append(Html(ex.Message)).Append("\n");
            }

            // Look again until the positions are really gone, instead of judging after a fixed wait.
            DateTime verifyStart = DateTime.UtcNow;
            await Task.Delay(Math.Min(FlattenFirstCheckMs, flattenCheckMaxMs));
            bool allFlat;
            string check = BuildPostFlattenCheck(targets, out allFlat);
            while (!allFlat && (DateTime.UtcNow - verifyStart).TotalMilliseconds < flattenCheckMaxMs)
            {
                await Task.Delay(FlattenPollMs);
                check = BuildPostFlattenCheck(targets, out allFlat);
            }
            double tookSec = (DateTime.UtcNow - verifyStart).TotalSeconds;
            sb.Append("\n").Append(allFlat ? EmOk + " <b>Flat</b>" : EmSiren + " <b>STILL EXPOSED</b>")
              .Append(" after ").Append(tookSec.ToString("0.0", CultureInfo.InvariantCulture)).Append("s\n")
              .Append(check);
            if (!allFlat)
                sb.Append("<i>Check your broker's platform and close it by hand if needed. A flatten that is still working is not sent again, to avoid reversing the position.</i>\n");
            if (cfgFlattenHoldSec > 0)
                sb.Append("Hold: anything new on ").Append(held.Count == 1 ? "this account" : "these accounts").Append(" is closed again for ")
                  .Append(Num(cfgFlattenHoldSec)).Append("s.\n");
            sb.Append("<i>Strategies and copiers are not disabled. To keep an account flat, use /lock.</i>");

            if (allFlat)
            {
                EnqueueText(sb.ToString());
            }
            else
            {
                EnqueueCritical(sb.ToString(), StillExposedMinimal(targets, tookSec));
                RepeatExposureAlerts(targets, "/flatten", verifyStart, false, ExposureRepeatFlattenMinutes);
            }
        }

        // Cancels working orders (no flatten). Uses Account.Cancel through reflection so a NinjaTrader API difference cannot break the compile.
        private async Task ExecuteCancelAsync(string filter)
        {
            string problem;
            List<CbiAccount> targets = ResolveAccounts(filter, out problem);
            if (targets == null)
            {
                EnqueueQuiet(EmInfo + " " + Html(problem));
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(EmStop).Append(" <b>CANCEL SENT</b>\n");
            sb.Append("Scope: ").Append(string.IsNullOrWhiteSpace(filter) ? "tracked accounts" : Html(targets[0].Name)).Append("\n");

            foreach (CbiAccount a in targets)
            {
                List<CbiOrder> working = WorkingOrders(a);
                if (working.Count == 0)
                {
                    sb.Append(Html(a.Name)).Append(": no working orders\n");
                    continue;
                }

                string how;
                bool ok = TryCancelOrders(a, working, out how);
                sb.Append(Html(a.Name)).Append(": ")
                  .Append(ok ? Num(working.Count) + (working.Count == 1 ? " order" : " orders") : EmWarn + " failed (" + Html(how) + ")").Append("\n");
            }

            await Task.Delay(2000);

            int remaining = 0;
            foreach (CbiAccount a in targets)
                remaining += WorkingOrders(a).Count;
            sb.Append("\n").Append(remaining == 0 ? EmOk + " <b>No working orders left</b>" : EmWarn + " <b>" + Num(remaining) + " working order(s) remain</b>")
              .Append("\n<i>Positions are untouched; use /flatten to close them.</i>");

            EnqueueText(sb.ToString());
        }

        private static List<CbiOrder> WorkingOrders(CbiAccount a)
        {
            lock (a.Orders)
                return a.Orders.Where(o => o != null && IsWorkingState(o.OrderState)).ToList();
        }

        private static bool TryCancelOrders(CbiAccount a, List<CbiOrder> orders, out string how)
        {
            how = null;
            try
            {
                System.Reflection.MethodInfo m = typeof(CbiAccount).GetMethod("Cancel", new Type[] { typeof(IEnumerable<CbiOrder>) });
                if (m == null)
                {
                    how = "Account.Cancel not found";
                    return false;
                }
                m.Invoke(a, new object[] { orders });
                return true;
            }
            catch (Exception ex)
            {
                how = Truncate(Describe(ex), 120);
                return false;
            }
        }

        // Finds tracked accounts by exact name or by a unique part of the name ("002"). Blank filter = all tracked accounts.
        private List<CbiAccount> ResolveAccounts(string filter, out string problem)
        {
            problem = null;
            List<CbiAccount> all = SnapshotTrackedAccounts();
            if (string.IsNullOrWhiteSpace(filter))
                return all;

            string f = filter.Trim();
            List<CbiAccount> exact = all.Where(a => a.Name.Equals(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact;

            List<CbiAccount> part = all.Where(a => a.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (part.Count == 1)
                return part;

            problem = part.Count == 0
                ? "No tracked account matches '" + f + "'."
                : "'" + f + "' matches several accounts (" + string.Join(", ", part.Select(a => a.Name).ToArray()) + "). Be more specific.";
            return null;
        }

        private static string ParseArgument(string text)
        {
            return Dtn.ParseArgument(text);
        }

        private List<CbiInstrument> CollectExposedInstruments(CbiAccount a)
        {
            Dictionary<string, CbiInstrument> map = new Dictionary<string, CbiInstrument>(StringComparer.OrdinalIgnoreCase);

            lock (a.Positions)
            {
                foreach (CbiPosition p in a.Positions)
                {
                    if (p != null && p.Instrument != null && p.MarketPosition != MarketPosition.Flat)
                        map[p.Instrument.FullName] = p.Instrument;
                }
            }

            lock (a.Orders)
            {
                foreach (CbiOrder o in a.Orders)
                {
                    if (o != null && o.Instrument != null && IsWorkingState(o.OrderState))
                        map[o.Instrument.FullName] = o.Instrument;
                }
            }

            return map.Values.ToList();
        }

        private string BuildPostFlattenCheck(List<CbiAccount> accounts, out bool allFlat)
        {
            allFlat = true;
            StringBuilder sb = new StringBuilder();
            foreach (CbiAccount a in accounts)
            {
                bool accountFlat;
                string lines = BuildPositionLines(a, out accountFlat);
                if (!accountFlat)
                    allFlat = false;
                sb.Append("<b>").Append(Html(a.Name)).Append("</b>\n").Append(lines);
            }
            return sb.ToString();
        }

        private string BuildHelpText()
        {
            return EmInfo + " <b>Commands</b>\n"
                + "/status \u2014 connections, positions, strategies, risk\n"
                + "/flatten [account] \u2014 close positions and cancel orders ("
                + (cfgUseGlobalFlattenEverything ? "ALL accounts" : "tracked accounts") + (cfgFlattenConfirm ? ", asks to confirm" : string.Empty) + "). Add part of an account name for just that account\n"
                + "/cancel [account] \u2014 cancel working orders, keep positions\n"
                + "/lock [account] \u2014 flatten and keep the account flat until /unlock or the session ends\n"
                + "/unlock [account] \u2014 end a lock\n"
                + "/risk \u2014 floors, buffers, daily loss, consistency and locks. /risk seed ACCOUNT 51000 sets a trailing high-water mark; /risk reset [account] restarts it\n"
                + "/floor [account] [amount] \u2014 set a floor by hand (/floor 48250, /floor 002 48500, /floor clear)\n"
                + "/selftest \u2014 can it read everything it needs, and is auto-flatten armed for each account\n"
                + "/testbackup \u2014 send a test through the backup alert channel\n"
                + "/activity [account days] \u2014 last trade and days left on the inactivity rule (/activity 002 3 = last traded 3 days ago)\n"
                + "/leader [account] \u2014 show or change the copier leader (/leader clear)\n"
                + "/copy \u2014 follower fills vs the leader today\n"
                + "/eod \u2014 preview the end-of-day report\n"
                + "/diag \u2014 health and diagnostics\n"
                + "/simulate \u2014 one sample of every alert (nothing real)\n"
                + "/version \u2014 add-on version\n"
                + "/help \u2014 this list";
        }

        #endregion

        #region Telegram transport (ordered queue, retry, backoff)

        private static HttpClient CreateHttpClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
            try
            {
                ServicePoint sp = ServicePointManager.FindServicePoint(new Uri(TelegramApiBase));
                sp.ConnectionLimit = 8;                     // the long poll holds one connection; keep room for sends and button answers
                // TCP keepalive notices a silently dropped connection (router, VPN, NAT timeout) in seconds instead of minutes.
                // Called by name so a framework without it cannot break the compile.
                System.Reflection.MethodInfo keepAlive = typeof(ServicePoint).GetMethod("SetTcpKeepAlive", new Type[] { typeof(bool), typeof(int), typeof(int) });
                if (keepAlive != null)
                    keepAlive.Invoke(sp, new object[] { true, 20000, 5000 });
            }
            catch (Exception) { }
            HttpClient client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;          // every request sets its own timeout
            return client;
        }

        private string ApiUrl(string method)
        {
            return TelegramApiBase + resolvedToken + "/" + method;
        }

        // Problem alert: always notifies and goes ahead of routine messages.
        private void EnqueueText(string text)
        {
            Enqueue(text, false, null, true, false);
        }

        // Routine message: silent when SilentRoutineAlerts is on; merged with others when several are waiting.
        private void EnqueueQuiet(string text)
        {
            Enqueue(text, cfgSilentRoutine, null, false, false);
        }

        // Reply to a command: you are waiting for it, so it goes in the priority lane (silent when SilentRoutineAlerts is on).
        private void EnqueueReply(string text)
        {
            Enqueue(text, cfgSilentRoutine, null, true, false);
        }

        // /simulate: kept in order and never merged, so each sample looks exactly like the real alert.
        private void EnqueueSample(string text, bool loud)
        {
            Enqueue(text, !loud && cfgSilentRoutine, null, false, true);
        }

        private void EnqueueWithButtons(string text, string replyMarkup)
        {
            Enqueue(text, false, replyMarkup, true, true);
        }

        private void Enqueue(string text, bool silent, string replyMarkup, bool priority, bool noMerge)
        {
            LogAlert(text);
            OutboundMessage m = new OutboundMessage
            {
                Text = text ?? string.Empty, CreatedUtc = DateTime.UtcNow, Silent = silent,
                ReplyMarkup = replyMarkup, Priority = priority, NoMerge = noMerge
            };
            lock (queueLock)
            {
                if (priority)
                    priorityLane.Add(m);
                else
                    routineLane.Add(m);
            }
            try { sendSignal.Release(); } catch (Exception) { }
        }

        private OutboundMessage PeekNextMessage()
        {
            lock (queueLock)
            {
                if (priorityLane.Count > 0)
                    return priorityLane[0];
                if (routineLane.Count == 0)
                    return null;
                if (routineLane.Count >= RoutineMergeAt)
                    MergeRoutineLocked();
                return routineLane[0];
            }
        }

        // Several routine messages waiting: send them as one message instead of one per second.
        private void MergeRoutineLocked()
        {
            const string separator = "\n\n\u2500\u2500\u2500\u2500\u2500\n\n";
            int take = 0;
            int length = 0;
            while (take < routineLane.Count)
            {
                OutboundMessage m = routineLane[take];
                if (m.NoMerge || m.Plain || m.Attempts > 0 || !string.IsNullOrEmpty(m.ReplyMarkup))
                    break;
                int add = m.Text.Length + separator.Length;
                if (take > 0 && length + add > MergeMaxChars)
                    break;
                length += add;
                take++;
            }
            if (take < 2)
                return;

            StringBuilder sb = new StringBuilder();
            bool silent = true;
            DateTime created = DateTime.MaxValue;
            for (int i = 0; i < take; i++)
            {
                OutboundMessage m = routineLane[i];
                if (i > 0)
                    sb.Append(separator);
                sb.Append(m.Text);
                silent &= m.Silent;
                if (m.CreatedUtc < created)
                    created = m.CreatedUtc;
            }
            routineLane.RemoveRange(0, take);
            routineLane.Insert(0, new OutboundMessage { Text = sb.ToString(), CreatedUtc = created, Silent = silent });
        }

        private void RemoveMessage(OutboundMessage m)
        {
            lock (queueLock)
            {
                if (!priorityLane.Remove(m))
                    routineLane.Remove(m);
            }
        }

        private int QueuedCount(out int priority)
        {
            lock (queueLock)
            {
                priority = priorityLane.Count;
                return priorityLane.Count + routineLane.Count;
            }
        }

        private double OldestQueuedSeconds()
        {
            DateTime oldest = DateTime.MaxValue;
            lock (queueLock)
            {
                foreach (OutboundMessage m in priorityLane)
                    if (m.CreatedUtc < oldest) oldest = m.CreatedUtc;
                foreach (OutboundMessage m in routineLane)
                    if (m.CreatedUtc < oldest) oldest = m.CreatedUtc;
            }
            return oldest == DateTime.MaxValue ? 0.0 : (DateTime.UtcNow - oldest).TotalSeconds;
        }

        // A priority message other than the one that is failing: worth trying before the backoff ends.
        private bool OtherPriorityWaiting(OutboundMessage failing)
        {
            lock (queueLock)
                return priorityLane.Count > 0 && !ReferenceEquals(priorityLane[0], failing);
        }

        private async Task SenderLoopAsync()
        {
            while (true)
            {
                int delayMs = 0;
                OutboundMessage failing = null;
                try
                {
                    OutboundMessage msg = PeekNextMessage();
                    if (msg == null)
                    {
                        if (stopping)
                            break;
                        await sendSignal.WaitAsync(1000);
                        continue;
                    }

                    if (stopping && DateTime.UtcNow > stopDeadlineUtc)
                        break;

                    double gateMs = (rateLimitedUntilUtc - DateTime.UtcNow).TotalMilliseconds;
                    if (gateMs > 0 && !stopping)
                    {
                        delayMs = (int)Math.Min(gateMs, 5000) + 1;     // Telegram asked us to wait: nothing can be sent until then
                    }
                    else
                    {
                        CancellationTokenSource lifeCts = lifetimeCts;
                        SendOutcome outcome = await SendAsync(msg, lifeCts != null ? lifeCts.Token : CancellationToken.None);

                        if (outcome.Result == SendResult.Retry)
                        {
                            msg.Attempts++;
                            if ((DateTime.UtcNow - msg.CreatedUtc).TotalMinutes > SendRetryWindowMin)
                            {
                                RemoveMessage(msg);
                                WriteOutput("Dropped a Telegram message after " + SendRetryWindowMin + " minutes of failed delivery.");
                            }
                            else
                            {
                                delayMs = outcome.RetryAfterSec > 0
                                    ? outcome.RetryAfterSec * 1000
                                    : Math.Min(60000, 1000 << Math.Min(msg.Attempts, 6));
                                failing = msg;
                            }
                            if (stopping)
                                delayMs = Math.Min(delayMs, 500);
                        }
                        else
                        {
                            RemoveMessage(msg);
                            int priorityWaiting;
                            if (outcome.Result == SendResult.Sent && QueuedCount(out priorityWaiting) > 0)
                                delayMs = stopping ? 200 : 1000;     // Telegram: about one message per second per chat
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!stopping)
                        WriteError("Sender loop", ex);
                    delayMs = stopping ? 200 : 2000;
                }

                if (delayMs > 0)
                {
                    try { await WaitBeforeNextSendAsync(delayMs, failing); }
                    catch (Exception) { }
                }
            }
        }

        // Backing off after a failure: a different priority alert arriving in the meantime is tried at once.
        private async Task WaitBeforeNextSendAsync(int delayMs, OutboundMessage failing)
        {
            if (failing == null)
            {
                await Task.Delay(delayMs);
                return;
            }
            DateTime until = DateTime.UtcNow.AddMilliseconds(delayMs);
            while (!stopping)
            {
                int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0)
                    return;
                await sendSignal.WaitAsync(Math.Min(left, 1000));
                if (OtherPriorityWaiting(failing) && DateTime.UtcNow >= rateLimitedUntilUtc)
                    return;
            }
        }

        private async Task<SendOutcome> SendAsync(OutboundMessage msg, CancellationToken ct)
        {
            string text = msg.Text;
            if ((DateTime.UtcNow - msg.CreatedUtc).TotalSeconds > 60)
            {
                text = EmClock + " <i>Delayed alert, raised at "
                    + TimeText(NtNow() - (DateTime.UtcNow - msg.CreatedUtc)) + "</i>\n" + text;
            }
            if (msg.Plain)
                text = ToPlainText(text);

            List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>();
            fields.Add(new KeyValuePair<string, string>("chat_id", chatIdText));
            fields.Add(new KeyValuePair<string, string>("text", Truncate(text, MaxTextLength)));
            if (!msg.Plain)
                fields.Add(new KeyValuePair<string, string>("parse_mode", "HTML"));
            fields.Add(new KeyValuePair<string, string>("disable_web_page_preview", "true"));
            if (msg.Silent)
                fields.Add(new KeyValuePair<string, string>("disable_notification", "true"));
            if (!string.IsNullOrEmpty(msg.ReplyMarkup))
                fields.Add(new KeyValuePair<string, string>("reply_markup", msg.ReplyMarkup));

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(SendTimeoutMs);
                    using (FormUrlEncodedContent content = new FormUrlEncodedContent(fields))
                    using (HttpResponseMessage resp = await Http.PostAsync(ApiUrl("sendMessage"), content, timeout.Token))
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        SendOutcome outcome = InterpretResponse(resp, body);

                        if (outcome.Result == SendResult.Sent)
                        {
                            lastSendOkUtc = DateTime.UtcNow;
                            lastSendMs = (int)watch.ElapsedMilliseconds;
                        }
                        else if (outcome.Result == SendResult.Permanent && !msg.Plain
                                 && body != null && body.IndexOf("parse entities", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Telegram could not parse the formatting: resend as plain text instead of losing the alert.
                            msg.Plain = true;
                            return new SendOutcome(SendResult.Retry, 1);
                        }
                        return outcome;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return new SendOutcome(SendResult.Retry, 0);
            }
            catch (HttpRequestException ex)
            {
                if (!stopping)
                    WriteThrottled("Network error talking to Telegram (alerts are queued and retried): " + Describe(ex));
                return new SendOutcome(SendResult.Retry, 0);
            }
        }

        private SendOutcome InterpretResponse(HttpResponseMessage resp, string body)
        {
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                NoteServerTime(resp);
                return new SendOutcome(SendResult.Sent, 0);
            }

            Dictionary<string, object> root = TryParseObject(body);
            string description = JsonString(root, "description") ?? (body ?? string.Empty);

            if (code == 429)
            {
                long retryAfter = JsonLong(JsonObject(root, "parameters"), "retry_after");
                int wait = (int)Math.Max(1, Math.Min(retryAfter, 300));
                rateLimitedUntilUtc = DateTime.UtcNow.AddSeconds(wait);      // applies to every message, not just this one
                return new SendOutcome(SendResult.Retry, wait);
            }
            if (code >= 500 || code == 408)
                return new SendOutcome(SendResult.Retry, 0);

            string hint = string.Empty;
            if (code == 401)
                hint = " Check BotToken.";
            else if (code == 400 && description.IndexOf("chat not found", StringComparison.OrdinalIgnoreCase) >= 0)
                hint = " Open your bot in Telegram and press Start, and verify AuthorizedChatID.";
            else if (code == 403)
                hint = " The bot was blocked or is not allowed to message this chat.";

            // Blocked bot, wrong chat or revoked token: every alert is being thrown away, even though polling may still work.
            if (Dtn.IsFatalSendError(code, description))
            {
                lastFatalSendUtc = DateTime.UtcNow;
                lastFatalSendReason = "HTTP " + code.ToString(CultureInfo.InvariantCulture) + (hint.Length > 0 ? ":" + hint : string.Empty);
            }
            WriteOutput("sendMessage rejected (" + code.ToString(CultureInfo.InvariantCulture) + "): " + Truncate(description, 300) + hint);
            return new SendOutcome(SendResult.Permanent, 0);
        }

        #endregion

        #region Minimal JSON reader (no external dependencies)

        private static Dictionary<string, object> TryParseObject(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try { return Dtn.ParseJson(json) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        private static Dictionary<string, object> JsonObject(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        private static List<object> JsonList(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        private static string JsonString(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as string : null;
        }

        private static long JsonLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
                return 0;
            if (v is long)
                return (long)v;
            if (v is double)
                return (long)(double)v;
            long parsed;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static bool JsonBool(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        #endregion

        #region Buttons, clock check and diagnostics helpers

        private void RequestActionConfirmation(string kind, string filter)
        {
            string problem;
            List<CbiAccount> targets = ResolveAccounts(filter, out problem);
            if (targets == null)
            {
                EnqueueReply(EmInfo + " " + Html(problem));
                return;
            }
            bool targeted = !string.IsNullOrWhiteSpace(filter);
            string scope = targeted ? Html(targets[0].Name)
                : (kind == "flatten" && cfgUseGlobalFlattenEverything ? "<b>ALL accounts</b>" : "tracked accounts");

            if (kind == "unlock")
                RequestConfirmation("unlock", targeted ? filter.Trim() : string.Empty, double.NaN,
                    EmWarn + " <b>Confirm unlock?</b>\nScope: " + scope + "\nThe account can trade again. A floor or limit that is still breached can lock it again.",
                    EmUnlock + " Unlock now");
            else if (kind == "lock")
                RequestConfirmation("lock", targeted ? filter.Trim() : string.Empty, double.NaN,
                    EmWarn + " <b>Confirm lock?</b>\nScope: " + scope + "\nFlattens, cancels orders, and closes anything new until /unlock or the session ends.",
                    EmLock + " Lock now");
            else
                RequestConfirmation("flatten", targeted ? filter.Trim() : string.Empty, double.NaN,
                    EmWarn + " <b>Confirm flatten?</b>\nScope: " + scope, EmStop + " Flatten now");
        }

        private void RequestConfirmation(string kind, string filter, double value, string prompt, string okLabel)
        {
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            lock (confirmLock)
            {
                DateTime now = DateTime.UtcNow;
                foreach (string old in pendingConfirms.Where(kv => kv.Value.ExpiresUtc < now).Select(kv => kv.Key).ToList())
                    pendingConfirms.Remove(old);
                pendingConfirms[token] = new PendingConfirm { Kind = kind, Filter = filter ?? string.Empty, Value = value, ExpiresUtc = now.AddSeconds(FlattenConfirmSec) };
            }

            string markup = "{\"inline_keyboard\":[[{\"text\":\"" + okLabel + "\",\"callback_data\":\"ok:" + token
                + "\"},{\"text\":\"Cancel\",\"callback_data\":\"cancel:" + token + "\"}]]}";
            EnqueueWithButtons(prompt + "\nButtons expire in " + Num(FlattenConfirmSec) + "s.", markup);
        }

        private async Task HandleCallbackAsync(Dictionary<string, object> callback)
        {
            Dictionary<string, object> from = JsonObject(callback, "from");
            if (from == null || JsonLong(from, "id") != authorizedUserId)
                return;                                              // not you: ignored silently

            string id = JsonString(callback, "id");
            string data = JsonString(callback, "data") ?? string.Empty;

            bool isOk = data.StartsWith("ok:", StringComparison.Ordinal) || data.StartsWith("flat:", StringComparison.Ordinal);
            bool isCancel = data.StartsWith("cancel:", StringComparison.Ordinal);
            string token = isOk || isCancel ? data.Substring(data.IndexOf(':') + 1) : string.Empty;

            bool valid = false;
            string kind = string.Empty;
            string filter = string.Empty;
            double value = double.NaN;
            lock (confirmLock)
            {
                PendingConfirm pending;
                if (token.Length > 0 && pendingConfirms.TryGetValue(token, out pending))
                {
                    pendingConfirms.Remove(token);                   // single use
                    valid = DateTime.UtcNow <= pending.ExpiresUtc;
                    kind = pending.Kind;
                    filter = pending.Filter;
                    value = pending.Value;
                }
            }

            if (isOk || isCancel)
                LogAlert("AUDIT button " + (isOk ? "confirm" : "cancel") + " " + kind + (filter.Length > 0 ? " " + filter : string.Empty) + (valid ? string.Empty : " (expired or already used)"));
            if (isOk && valid)
            {
                switch (kind)
                {
                    case "unlock":
                        await AnswerCallbackAsync(id, "Unlocking...");
                        EnqueueReply(ExecuteUnlockCommand(filter));
                        break;
                    case "lock":
                        await AnswerCallbackAsync(id, "Locking...");
                        EnqueueReply(ExecuteLockCommand(filter));
                        break;
                    case "floor":
                        await AnswerCallbackAsync(id, "Floor set");
                        EnqueueReply(ApplyFloor(filter, value));
                        break;
                    case "riskreset":
                        await AnswerCallbackAsync(id, "Reset");
                        EnqueueReply(ApplyRiskReset(filter));
                        break;
                    case "riskseed":
                        await AnswerCallbackAsync(id, "Seeded");
                        EnqueueReply(ApplySeed(filter, value, false));
                        break;
                    case "riskbest":
                        await AnswerCallbackAsync(id, "Seeded");
                        EnqueueReply(ApplySeed(filter, value, true));
                        break;
                    default:
                        await AnswerCallbackAsync(id, "Flattening...");
                        RunInBackground(() => ExecuteFlattenAsync(filter), "Flatten");
                        break;
                }
            }
            else if (isCancel && valid)
            {
                await AnswerCallbackAsync(id, "Cancelled");
            }
            else
            {
                await AnswerCallbackAsync(id, "Expired. Send the command again.");
            }
        }

        private void RunInBackground(Func<Task> work, string what)
        {
            Task.Run(async () =>
            {
                try { await work(); }
                catch (Exception ex) { WriteError(what, ex); }
            });
        }

        private async Task AnswerCallbackAsync(string callbackId, string text)
        {
            if (string.IsNullOrEmpty(callbackId))
                return;
            try
            {
                CancellationToken life = lifetimeCts != null ? lifetimeCts.Token : CancellationToken.None;
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(life))
                {
                    timeout.CancelAfter(10000);
                    using (FormUrlEncodedContent content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("callback_query_id", callbackId),
                        new KeyValuePair<string, string>("text", text)
                    }))
                    using (HttpResponseMessage resp = await Http.PostAsync(ApiUrl("answerCallbackQuery"), content, timeout.Token))
                    {
                        await resp.Content.ReadAsStringAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                if (!terminated)
                    WriteThrottled("answerCallbackQuery failed: " + Describe(ex));
            }
        }

        // Telegram's own clock (HTTP Date header) vs this PC: a wrong PC clock breaks end-of-day timing and the restart report.
        private void NoteServerTime(HttpResponseMessage resp)
        {
            try
            {
                DateTimeOffset? date = resp.Headers.Date;
                if (!date.HasValue)
                    return;

                double drift = (date.Value.UtcDateTime - DateTime.UtcNow).TotalSeconds;
                lastClockDriftSec = drift;

                if (Math.Abs(drift) > ClockDriftAlertSec && (DateTime.UtcNow - clockAlertUtc).TotalHours > 6)
                {
                    clockAlertUtc = DateTime.UtcNow;
                    EnqueueText(MsgClockDrift(drift));
                }
            }
            catch (Exception) { }
        }

        private static string MsgClockDrift(double driftSec)
        {
            return EmWarn + " <b>PC CLOCK OFF</b>\n"
                + "Telegram's clock differs from this PC by about " + Num((int)Math.Abs(driftSec)) + "s ("
                + (driftSec > 0 ? "PC is behind" : "PC is ahead") + ").\n"
                + "End-of-day timing and the restart report depend on it. Sync Windows time (Settings > Time & language > Date & time > Sync now).";
        }

        private void RememberLine(string line)
        {
            lock (linesLock)
            {
                recentLines.Enqueue(TimeText(NtNow()) + " " + Truncate(line, 160));
                while (recentLines.Count > 12)
                    recentLines.Dequeue();
            }
        }

        private static string ToPlainText(string html)
        {
            return System.Text.RegularExpressions.Regex.Replace(html ?? string.Empty, "<[^>]+>", string.Empty)
                .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
        }

        #endregion
    }
}
