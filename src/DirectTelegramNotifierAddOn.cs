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
#endregion

// ============================================================================================
//  DirectTelegramNotifier  -  NinjaTrader 8 AddOn
//
//  Telegram alerts and remote commands for NinjaTrader 8, talking straight to the Telegram Bot
//  API over HttpClient (no Share Services, no third-party servers, no external DLLs).
//  Starts and stops with NinjaTrader itself, independent of any chart or workspace.
//
//  Alerts:    fills, connection loss/restore (orders and data), UI freeze, rejected orders,
//             order-rate spikes, strategy disabled, stale market data, trade-copier mismatch
//             (leader vs followers), restart/crash detection, end-of-day summary.
//             Every alert is also appended to Documents\NinjaTrader 8\DirectTelegramNotifier.log.
//  Commands:  /status  /flatten  /copy  /eod  /diag  /simulate  /version  /help   (only from AuthorizedChatID)
//  Optional:  external heartbeat ping (dead-man's switch, e.g. Healthchecks.io).
//
//  Configuration: Documents\NinjaTrader 8\DirectTelegramNotifier.config (template created on
//  first run). Press F5 in the NinjaScript Editor or restart NinjaTrader after editing.
//  BotToken may be left blank and supplied via the NT_TELEGRAM_BOT_TOKEN environment variable.
//
//  Safety:
//   * Only messages whose from.id equals AuthorizedChatID are executed.
//   * Telegram messages queued before startup are discarded and commands older than 120 s are
//     ignored, so a stale /flatten never runs after a restart or reboot.
//   * /flatten covers the tracked account(s) only, unless UseGlobalFlattenEverything=true.
//
//  Version 0.1.2, MIT License. Not affiliated with or endorsed by NinjaTrader or Telegram.
//  Provided as is, without warranty. Automated trading and remote order control carry risk.
//
//  Written in C# 5 syntax so it compiles on any NinjaScript compiler version.
// ============================================================================================

namespace NinjaTrader.NinjaScript.AddOns
{
    public class DirectTelegramNotifier : AddOnBase
    {
        #region Constants

        private const string TelegramApiBase      = "https://api.telegram.org/bot";
        private const string TokenEnvironmentVar  = "NT_TELEGRAM_BOT_TOKEN";
        private const string ConfigFileName       = "DirectTelegramNotifier.config";
        private const int    PollIntervalMs       = 3000;
        private const int    HousekeepingMs       = 5000;
        private const int    FillAggregationMs    = 2000;
        private const int    FillAlertMaxAgeSec   = 300;
        private const int    CommandMaxAgeSec     = 120;
        private const int    UiFreezeAlertSec     = 45;
        private const int    SendRetryWindowMin   = 30;
        private const int    ShutdownDrainSec     = 4;
        private const int    MaxTextLength        = 4000;
        private const int    FlattenVerifyDelayMs = 2500;
        private const int    FlapThreshold        = 4;
        private const int    FlapWindowSec        = 300;
        private const int    FlapMuteSec          = 300;
        private const int    RunawayCooldownSec   = 300;
        private const int    StopMessageTimeoutMs = 3500;
        private const int    ConnectionMergeMs    = 1500;
        private const int    SendTimeoutMs        = 30000;
        private const int    PollTimeoutSec       = 25;
        private const int    PollHttpTimeoutMs    = 45000;
        private const int    PollIdleMs           = 250;
        private const int    FlattenConfirmSec    = 30;
        private const int    ClockDriftAlertSec   = 15;
        private const int    ReentryWindowSec     = 90;
        private const string AddOnVersion        = "0.1.2";
        private const string HeartbeatFileName    = "DirectTelegramNotifier.heartbeat";
        private const string Sep                  = " \u00B7 ";

        // Emoji written as escapes so the file survives any editor encoding.
        private const string EmGreen  = "\U0001F7E2";
        private const string EmRed    = "\U0001F534";
        private const string EmWarn   = "\u26A0\uFE0F";
        private const string EmOk     = "\u2705";
        private const string EmChart  = "\U0001F4CA";
        private const string EmStop   = "\U0001F6D1";
        private const string EmInfo   = "\u2139\uFE0F";
        private const string EmClock  = "\u23F1\uFE0F";
        private const string EmRocket = "\U0001F680";
        private const string EmSiren  = "\U0001F6A8";
        private const string EmPlay   = "\u25B6\uFE0F";
        private const string EmWait   = "\u23F3";

        #endregion

        #region Nested types

        private enum SendResult { Sent, Retry, Permanent }

        private sealed class SendOutcome
        {
            public readonly SendResult Result;
            public readonly int RetryAfterSec;
            public SendOutcome(SendResult result, int retryAfterSec) { Result = result; RetryAfterSec = retryAfterSec; }
        }

        private sealed class OutboundMessage
        {
            public string Text;
            public DateTime CreatedUtc;
            public int Attempts;
            public bool Silent;
            public bool Plain;
            public string ReplyMarkup;
        }

        private sealed class Lot
        {
            public double Price;
            public int Quantity;
        }

        private sealed class PositionBook
        {
            public readonly List<Lot> Lots = new List<Lot>();
            public string AccountName;
            public string InstrumentName;
            public int Net;
            public double TradeGross;
            public double TradeCommission;
        }

        private sealed class SessionStats
        {
            public int Trades;
            public int Wins;
            public int Losses;
            public int Scratches;
            public int Executions;
            public int Contracts;
            public double Gross;
            public double Commission;
            public double LargestWin;
            public double LargestLoss;
        }

        // Leader-vs-follower price and timing differences, accumulated per follower account.
        private sealed class CopyStat
        {
            public int Count;
            public double SumTicks;        // positive = follower did worse than the leader
            public double SumDollars;      // positive = follower did better than the leader
            public double WorstTicks;
            public double SumLagMs;
            public double MaxLagMs;
        }

        // Fills of one instrument and side that arrive close together, one leg per account.
        private sealed class FillLeg
        {
            public string AccountName;
            public string ActionText;
            public string OrderName;
            public int Quantity;
            public double Notional;
            public int FillCount;
            public DateTime FirstTime;
        }

        private sealed class FillGroup
        {
            public CbiInstrument Instrument;
            public string InstrumentName;
            public bool IsBuy;
            public DateTime FirstTime;
            public string SimLeader;               // used only by /simulate
            public double SimTick;                 // used only by /simulate
            public readonly Dictionary<string, FillLeg> Legs = new Dictionary<string, FillLeg>(StringComparer.OrdinalIgnoreCase);
        }

        // Connection events that arrive together (orders + data of one connection) are sent as one message.
        private sealed class ConnNotice
        {
            public string Connection;
            public bool Down;
            public bool UserInitiated;
            public bool Lost;
            public string Detail;
            public DateTime Time;
            public TimeSpan Downtime;
            public readonly List<string> Channels = new List<string>();
        }

        private sealed class StrategyInfo
        {
            public object Ref;
            public string Name;
            public string AccountName;
            public string State;
        }

        private sealed class RefComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }

        // One market-data subscription per watched instrument. The handler only records "a tick arrived".
        private sealed class FeedWatch
        {
            public CbiInstrument Instrument;
            public string Name;
            public long LastTickTicks;
            public volatile bool Subscribed;
            public bool Stale;
            public DateTime StaleSinceUtc;

            public void OnTick(object sender, object e)           // object, so it binds to the event whatever its args type derives from
            {
                Interlocked.Exchange(ref LastTickTicks, DateTime.UtcNow.Ticks);
            }
        }

        private sealed class MismatchState
        {
            public string Signature;
            public DateTime SinceUtc;
            public DateTime FirstUtc;
            public bool Alerted;
        }

        #endregion

        #region Static (process-wide) state

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly object InstanceLock = new object();
        private static DirectTelegramNotifier activeInstance;

        #endregion

        #region Configuration (loaded from file at start)

        private string cfgBotToken = string.Empty;
        private string cfgAuthorizedChatId = string.Empty;
        private string cfgAccountName = "Sim101";
        private bool   cfgEnableWatchdog = true;
        private bool   cfgEnableExecutions = true;
        private bool   cfgEnableRemoteCommands = true;
        private bool   cfgUseGlobalFlattenEverything = false;
        private int    cfgEodReportTime = 170000;
        private string cfgEodTimeZoneId = "Eastern Standard Time";
        private double cfgEstimatedCommissionPerSide = 0.0;
        private string cfgHeartbeatUrl = string.Empty;
        private int    cfgHeartbeatIntervalSec = 60;
        private int    cfgRestartGapThresholdSec = 45;
        private bool   cfgEnableStrategyWatch = true;
        private bool   cfgEnableAlertLog = true;
        private int    cfgStaleDataSeconds = 120;
        private string cfgStaleDataSchedule = "cme";
        private string cfgStaleDataInstruments = string.Empty;
        private string cfgLeaderAccount = string.Empty;
        private double cfgFollowerRatio = 1.0;
        private int    cfgMismatchSeconds = 20;
        private bool   cfgFlattenConfirm = false;
        private bool   cfgSilentRoutine = false;
        private bool   cfgEnableFillCsv = true;
        private bool   cfgEnableOrderRejectAlerts = true;
        private int    cfgRunawayMaxOrders = 30;
        private int    cfgRunawayWindowSec = 60;

        #endregion

        #region Instance state

        private string resolvedToken = string.Empty;
        private string chatIdText = string.Empty;
        private long authorizedUserId;

        private volatile bool started;
        private volatile bool terminated;
        private volatile bool stopping;
        private DateTime stopDeadlineUtc;
        private DateTime startedUtc;

        private System.Threading.Timer housekeepingTimer;
        private System.Threading.Timer pollTimer;
        private int housekeepingBusy;
        private CancellationTokenSource lifetimeCts;
        private Task senderTask;
        private Dispatcher uiDispatcher;

        private readonly ConcurrentQueue<OutboundMessage> sendQueue = new ConcurrentQueue<OutboundMessage>();
        private readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);

        private long updateOffset;
        private bool backlogDrained;
        private DateTime lastThrottledPrintUtc = DateTime.MinValue;
        private readonly HashSet<long> warnedUnauthorizedIds = new HashSet<long>();

        private readonly object accountsLock = new object();
        private readonly List<CbiAccount> trackedAccounts = new List<CbiAccount>();

        private readonly object statsLock = new object();
        private readonly Dictionary<string, DateTime> seenExecutionIds = new Dictionary<string, DateTime>();    // pruned by age, guarded by statsLock
        private readonly Dictionary<string, PositionBook> books = new Dictionary<string, PositionBook>();
        private readonly Dictionary<string, SessionStats> acctStats = new Dictionary<string, SessionStats>(StringComparer.OrdinalIgnoreCase);    // guarded by statsLock
        private readonly Dictionary<string, CopyStat> copyStats = new Dictionary<string, CopyStat>(StringComparer.OrdinalIgnoreCase);         // guarded by statsLock
        private readonly object csvLock = new object();
        private DateTime sessionStartNt;

        private TimeZoneInfo eodTimeZone = TimeZoneInfo.Local;
        private TimeSpan eodTimeOfDay = new TimeSpan(17, 0, 0);
        private DateTime lastEodReportDate = DateTime.MinValue;

        private readonly object fillLock = new object();
        private readonly Dictionary<string, FillGroup> pendingFills = new Dictionary<string, FillGroup>();

        private readonly object connLock = new object();
        private readonly Dictionary<string, DateTime> connDownSinceUtc = new Dictionary<string, DateTime>();

        private long uiProbeSentTicks;
        private long uiPongTicks;
        private volatile bool uiFreezeAlerted;

        private string heartbeatPath = string.Empty;
        private DateTime lastHeartbeatPingUtc = DateTime.MinValue;
        private int heartbeatPingBusy;
        private string startupGapNote = string.Empty;

        private readonly object noticeLock = new object();
        private readonly Dictionary<string, ConnNotice> connNotices = new Dictionary<string, ConnNotice>();

        private readonly object strategyLock = new object();
        private readonly Dictionary<object, StrategyInfo> strategies = new Dictionary<object, StrategyInfo>(new RefComparer());
        private bool strategyBaselined;
        private bool strategyProblemReported;

        private readonly object feedLock = new object();
        private readonly Dictionary<string, FeedWatch> feeds = new Dictionary<string, FeedWatch>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CbiInstrument> tradedInstruments = new Dictionary<string, CbiInstrument>(StringComparer.OrdinalIgnoreCase);   // guarded by statsLock
        private DateTime staleGraceEpochUtc = DateTime.MinValue;
        private TimeZoneInfo marketZone;

        private readonly object mismatchLock = new object();
        private readonly Dictionary<string, MismatchState> mismatches = new Dictionary<string, MismatchState>(StringComparer.OrdinalIgnoreCase);
        private bool leaderProblemReported;

        private readonly object logLock = new object();
        private string alertLogPath = string.Empty;

        private volatile bool pollFailed;
        private DateTime lastSendOkUtc = DateTime.MinValue;
        private int lastSendMs;
        private DateTime lastPollOkUtc = DateTime.MinValue;
        private double lastClockDriftSec;
        private DateTime clockAlertUtc = DateTime.MinValue;
        private DateTime lastFlattenUtc = DateTime.MinValue;
        private bool reentryAlerted;
        private DateTime lastPruneUtc = DateTime.UtcNow;

        private readonly object confirmLock = new object();
        private string confirmToken = string.Empty;
        private DateTime confirmExpiresUtc = DateTime.MinValue;

        private readonly object linesLock = new object();
        private readonly Queue<string> recentLines = new Queue<string>();

        private readonly object orderLock = new object();
        private readonly HashSet<string> seenOrderIds = new HashSet<string>();
        private readonly HashSet<string> rejectedAlerted = new HashSet<string>();
        private readonly HashSet<string> baselinedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<DateTime, string>> recentOrders = new List<KeyValuePair<DateTime, string>>();
        private DateTime lastRunawayAlertUtc = DateTime.MinValue;


        private readonly Dictionary<string, List<DateTime>> connDownHistory = new Dictionary<string, List<DateTime>>();
        private readonly Dictionary<string, DateTime> connMutedUntilUtc = new Dictionary<string, DateTime>();

        #endregion

        #region Lifecycle

        protected override void OnStateChange()
        {
            try
            {
                if (State == State.SetDefaults)
                {
                    Name        = "DirectTelegramNotifier";
                    Description = "Direct Telegram Bot API notifier: fills, connection/UI watchdog, EOD P&L summary and authenticated remote /status and /flatten.";
                }
                else if (State == State.Configure || State == State.Active)
                {
                    // Idempotent: whichever of these states NinjaTrader delivers first starts the notifier once.
                    TryStart();
                }
                else if (State == State.Terminated)
                {
                    ShutdownNotifier();
                }
            }
            catch (Exception ex)
            {
                WriteError("OnStateChange(" + State + ")", ex);
            }
        }

        private void TryStart()
        {
            if (started || terminated)
                return;

            lock (InstanceLock)
            {
                if (terminated || started)
                    return;
                if (activeInstance != null && !ReferenceEquals(activeInstance, this))
                {
                    WriteOutput("Another DirectTelegramNotifier instance is already running; this one stays idle.");
                    return;
                }
                if (!LoadConfiguration())
                    return;

                activeInstance = this;
                started = true;
            }

            StartNotifier();
        }

        private void StartNotifier()
        {
            startedUtc  = DateTime.UtcNow;
            lifetimeCts = new CancellationTokenSource();

            heartbeatPath  = HeartbeatPath();
            alertLogPath   = AlertLogPath();
            startupGapNote = ReadPreviousHeartbeat();
            WriteHeartbeatFile(false);

            try
            {
                System.Windows.Application app = System.Windows.Application.Current;
                uiDispatcher = app != null ? app.Dispatcher : null;
            }
            catch (Exception)
            {
                uiDispatcher = null;
            }

            InitializeSchedule();

            senderTask = Task.Run(() => SenderLoopAsync());

            // Always subscribed: it re-attaches accounts after reconnects even with the watchdog off.
            CbiConnection.ConnectionStatusUpdate += OnConnectionStatusUpdate;

            Task.Run(new Action(AttachAccounts));

            housekeepingTimer = new System.Threading.Timer(HousekeepingCallback, null, 5000, HousekeepingMs);

            if (cfgEnableRemoteCommands)
                pollTimer = new System.Threading.Timer(PollTimerCallback, null, 2000, Timeout.Infinite);

            WriteOutput("v" + AddOnVersion + " active. EOD report at " + eodTimeOfDay.ToString(@"hh\:mm\:ss") + " " + eodTimeZone.Id + ". Config: " + ConfigPath());

            if (cfgEnableWatchdog)
            {
                EnqueueQuiet(BuildOnlineMessage());
                if (!string.IsNullOrEmpty(startupGapNote))
                    EnqueueText(startupGapNote);
            }
        }

        private string BuildOnlineMessage()
        {
            List<string> active = new List<string>();
            if (cfgEnableWatchdog)          active.Add("watchdog");
            if (cfgEnableExecutions)        active.Add("fills");
            if (cfgEnableRemoteCommands)    active.Add("commands");
            if (cfgEnableOrderRejectAlerts) active.Add("reject alerts");
            if (cfgEnableStrategyWatch)     active.Add("strategy watch");
            if (cfgStaleDataSeconds > 0)    active.Add("stale-data " + Num(cfgStaleDataSeconds) + "s");
            if (cfgMismatchSeconds > 0 && ResolveLeaderName().Length > 0 && AccountFilterHasSeveral()) active.Add("copy check");
            if (cfgRunawayMaxOrders > 0)    active.Add("order-rate " + Num(cfgRunawayMaxOrders) + "/" + Num(cfgRunawayWindowSec) + "s");
            if (!string.IsNullOrEmpty(cfgHeartbeatUrl)) active.Add("heartbeat");

            StringBuilder sb = new StringBuilder();
            sb.Append(EmRocket).Append(" <b>Notifier online</b>").Append(Sep).Append("v").Append(AddOnVersion).Append("\n");
            sb.Append("Accounts: ").Append(Html(AccountListText())).Append("\n");
            if (cfgMismatchSeconds > 0 && ResolveLeaderName().Length > 0 && AccountFilterHasSeveral())
                sb.Append("Leader: ").Append(Html(ResolveLeaderName())).Append(" (other accounts follow)\n");
            sb.Append("Active: ").Append(Html(string.Join(", ", active.ToArray()))).Append("\n");
            sb.Append("EOD report: ").Append(eodTimeOfDay.ToString(@"hh\:mm")).Append(" ").Append(Html(eodTimeZone.Id)).Append("\n");
            sb.Append("Times: NinjaTrader clock\n");
            sb.Append("/flatten: ").Append(cfgUseGlobalFlattenEverything ? "<b>ALL accounts</b>" : "tracked accounts only").Append("\n\n");
            sb.Append("<b>Connections</b>\n").Append(BuildConnectionsBlock());
            return sb.ToString().TrimEnd('\n');
        }

        private void ShutdownNotifier()
        {
            bool wasStarted;
            lock (InstanceLock)
            {
                terminated = true;
                wasStarted = started;
                started    = false;
                if (ReferenceEquals(activeInstance, this))
                    activeInstance = null;
            }

            if (!wasStarted)
                return;

            DisposeTimer(ref housekeepingTimer);
            DisposeTimer(ref pollTimer);

            try { CbiConnection.ConnectionStatusUpdate -= OnConnectionStatusUpdate; }
            catch (Exception ex) { WriteError("Unsubscribe ConnectionStatusUpdate", ex); }

            List<CbiAccount> accounts;
            lock (accountsLock)
            {
                accounts = trackedAccounts.ToList();
                trackedAccounts.Clear();
            }
            foreach (CbiAccount a in accounts)
            {
                try { a.ExecutionUpdate -= OnExecutionUpdate; }
                catch (Exception ex) { WriteError("Unsubscribe ExecutionUpdate", ex); }
            }

            WriteOutput("Terminated handler running; writing clean-shutdown marker.");
            WriteHeartbeatFile(true);                           // read at next startup to tell normal exits from crashes

            StopFeedWatches();
            FlushPendingFills();

            stopDeadlineUtc = DateTime.UtcNow.AddSeconds(ShutdownDrainSec);
            stopping = true;
            try { sendSignal.Release(); } catch (Exception) { }

            if (cfgEnableWatchdog)
            {
                // Sent directly and synchronously, ahead of whatever is still queued.
                SendBlocking(EmStop + " <b>Notifier stopped</b>\nNinjaTrader is closing, or NinjaScript was recompiled.", StopMessageTimeoutMs);
            }

            CancellationTokenSource cts = lifetimeCts;
            if (cts != null)
            {
                try { cts.CancelAfter(TimeSpan.FromSeconds(ShutdownDrainSec)); } catch (Exception) { }
            }

            // On NinjaTrader shutdown the process exits right after this returns, so wait (bounded)
            // for the queue to drain; otherwise the final messages would be lost.
            Task t = senderTask;
            if (t != null)
            {
                try { t.Wait(TimeSpan.FromSeconds(ShutdownDrainSec + 1)); }
                catch (Exception) { }
            }

            WriteOutput("Stopped.");
        }

        private void HousekeepingCallback(object state)
        {
            if (Interlocked.CompareExchange(ref housekeepingBusy, 1, 0) != 0)
                return;
            try
            {
                if (terminated || !started)
                    return;

                if (cfgEnableWatchdog)
                    CheckUiResponsiveness();

                WriteHeartbeatFile(false);
                PingExternalHeartbeat();

                if (cfgEnableOrderRejectAlerts || cfgRunawayMaxOrders > 0)
                    ScanOrders();
                if (cfgEnableStrategyWatch)
                    CheckStrategies();

                CheckStaleData();
                CheckCopyMismatch();
                PruneTrackingSets();
                CheckEndOfDay();
            }
            catch (Exception ex)
            {
                WriteError("Housekeeping", ex);
            }
            finally
            {
                Interlocked.Exchange(ref housekeepingBusy, 0);
            }
        }

        #endregion

        #region Configuration file

        private static string ConfigPath()
        {
            string dir;
            try { dir = NinjaTrader.Core.Globals.UserDataDir; }
            catch (Exception) { dir = null; }
            if (string.IsNullOrEmpty(dir))
                dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8");
            return System.IO.Path.Combine(dir, ConfigFileName);
        }

        private static string DefaultConfigText()
        {
            return ""
                 + "# DirectTelegramNotifier configuration\r\n"
                 + "# After editing: press F5 in the NinjaScript Editor, or restart NinjaTrader.\r\n"
                 + "\r\n"
                 + "# --- Telegram ---\r\n"
                 + "# BotToken: from @BotFather. Leave blank to use the " + TokenEnvironmentVar + " environment variable instead.\r\n"
                 + "BotToken=\r\n"
                 + "# AuthorizedChatID: your numeric Telegram user ID. Alerts go here; only this user can send commands.\r\n"
                 + "AuthorizedChatID=\r\n"
                 + "\r\n"
                 + "# --- Accounts ---\r\n"
                 + "# AccountName: one account, a comma-separated list, or * for all connected accounts.\r\n"
                 + "AccountName=Sim101\r\n"
                 + "\r\n"
                 + "# --- Features ---\r\n"
                 + "EnableWatchdog=true\r\n"
                 + "EnableExecutions=true\r\n"
                 + "EnableRemoteCommands=true\r\n"
                 + "EnableOrderRejectAlerts=true\r\n"
                 + "# RunawayMaxOrders: alert when this many new orders appear within RunawayWindowSec. 0 = off.\r\n"
                 + "RunawayMaxOrders=30\r\n"
                 + "RunawayWindowSec=60\r\n"
                 + "# true = /flatten calls FlattenEverything() and hits EVERY connected account.\r\n"
                 + "UseGlobalFlattenEverything=false\r\n"
                 + "\r\n"
                 + "# --- End of day ---\r\n"
                 + "# EodReportTime: HHMMSS in EodTimeZoneId (a Windows time zone id; DST is handled automatically).\r\n"
                 + "EodReportTime=170000\r\n"
                 + "EodTimeZoneId=Eastern Standard Time\r\n"
                 + "# Used only when NinjaTrader reports zero commission for a fill (e.g. Sim without a commission template).\r\n"
                 + "EstimatedCommissionPerSide=0\r\n"
                 + "\r\n"
                 + "# --- Heartbeat and restart detection ---\r\n"
                 + "# HeartbeatUrl: optional dead-man's-switch ping URL (e.g. from Healthchecks.io). Blank = off.\r\n"
                 + "HeartbeatUrl=\r\n"
                 + "HeartbeatIntervalSec=60\r\n"
                 + "# RestartGapThresholdSec: on startup, report downtime longer than this many seconds.\r\n"
                 + "RestartGapThresholdSec=45\r\n"
                 + "\r\n"
                 + "# --- Monitoring ---\r\n"
                 + "EnableStrategyWatch=true\r\n"
                 + "# StaleDataSeconds: alert when an instrument you trade gets no ticks this long while the data connection says Connected. 0 = off.\r\n"
                 + "StaleDataSeconds=120\r\n"
                 + "# StaleDataSchedule: cme = ignore the daily 17:00-18:00 ET break and the weekend; always = never ignore.\r\n"
                 + "StaleDataSchedule=cme\r\n"
                 + "# StaleDataInstruments: extra instruments to always watch, comma-separated (e.g. MNQ 12-26). Instruments you trade or hold are watched automatically.\r\n"
                 + "StaleDataInstruments=\r\n"
                 + "# EnableAlertLog: also append every alert to DirectTelegramNotifier.log in this folder.\r\n"
                 + "EnableAlertLog=true\r\n"
                 + "\r\n"
                 + "# --- Trade copier check (e.g. Replikanto) ---\r\n"
                 + "# LeaderAccount: the account that leads. Blank = the first account in AccountName. Every other tracked account is a follower.\r\n"
                 + "LeaderAccount=\r\n"
                 + "# FollowerRatio: expected follower position as a multiple of the leader's (1 = same size).\r\n"
                 + "FollowerRatio=1\r\n"
                 + "# MismatchSeconds: alert when followers stay out of sync with the leader this long. 0 = off.\r\n"
                 + "MismatchSeconds=20\r\n"
                 + "\r\n"
                 + "# --- Convenience ---\r\n"
                 + "# FlattenConfirm: true = /flatten first shows Confirm / Cancel buttons (expire after 30 s). false = flatten immediately.\r\n"
                 + "FlattenConfirm=false\r\n"
                 + "# SilentRoutineAlerts: true = fills, reconnects, in-sync notices, reports and command replies arrive without a notification sound; problems still ring.\r\n"
                 + "SilentRoutineAlerts=false\r\n"
                 + "# EnableFillCsv: write every fill (and leader vs follower comparison) to DirectTelegramNotifier-fills.csv in this folder.\r\n"
                 + "EnableFillCsv=true\r\n";
        }

        private bool LoadConfiguration()
        {
            string path = ConfigPath();
            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, DefaultConfigText(), new UTF8Encoding(false));
                    WriteOutput("Created config template at " + path + ". Fill in BotToken and AuthorizedChatID, then press F5 in the NinjaScript Editor or restart NinjaTrader.");
                    return false;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                WriteError("Reading config " + path, ex);
                return false;
            }

            cfgBotToken                   = GetString(kv, "BotToken", string.Empty);
            cfgAuthorizedChatId           = GetString(kv, "AuthorizedChatID", string.Empty);
            cfgAccountName                = GetString(kv, "AccountName", "Sim101");
            cfgEnableWatchdog             = GetBool(kv, "EnableWatchdog", true);
            cfgEnableExecutions           = GetBool(kv, "EnableExecutions", true);
            cfgEnableRemoteCommands       = GetBool(kv, "EnableRemoteCommands", true);
            cfgUseGlobalFlattenEverything = GetBool(kv, "UseGlobalFlattenEverything", false);
            cfgEodReportTime              = GetInt(kv, "EodReportTime", 170000);
            cfgEodTimeZoneId              = GetString(kv, "EodTimeZoneId", "Eastern Standard Time");
            cfgEstimatedCommissionPerSide = Math.Max(0.0, GetDouble(kv, "EstimatedCommissionPerSide", 0.0));
            cfgHeartbeatUrl               = GetString(kv, "HeartbeatUrl", string.Empty);
            cfgHeartbeatIntervalSec       = Math.Max(15, GetInt(kv, "HeartbeatIntervalSec", 60));
            cfgRestartGapThresholdSec     = Math.Max(10, GetInt(kv, "RestartGapThresholdSec", 45));
            cfgEnableStrategyWatch        = GetBool(kv, "EnableStrategyWatch", true);
            cfgEnableAlertLog             = GetBool(kv, "EnableAlertLog", true);
            int staleSeconds              = GetInt(kv, "StaleDataSeconds", 120);
            cfgStaleDataSeconds           = staleSeconds <= 0 ? 0 : Math.Max(30, staleSeconds);
            cfgStaleDataSchedule          = GetString(kv, "StaleDataSchedule", "cme");
            cfgStaleDataInstruments       = GetString(kv, "StaleDataInstruments", string.Empty);
            cfgLeaderAccount              = GetString(kv, "LeaderAccount", string.Empty);
            cfgFollowerRatio              = Math.Max(0.0, GetDouble(kv, "FollowerRatio", 1.0));
            cfgMismatchSeconds            = Math.Max(0, GetInt(kv, "MismatchSeconds", 20));
            cfgFlattenConfirm             = GetBool(kv, "FlattenConfirm", false);
            cfgSilentRoutine              = GetBool(kv, "SilentRoutineAlerts", false);
            cfgEnableFillCsv              = GetBool(kv, "EnableFillCsv", true);
            cfgEnableOrderRejectAlerts    = GetBool(kv, "EnableOrderRejectAlerts", true);
            cfgRunawayMaxOrders           = Math.Max(0, GetInt(kv, "RunawayMaxOrders", 30));
            cfgRunawayWindowSec           = Math.Max(10, GetInt(kv, "RunawayWindowSec", 60));

            string token = cfgBotToken;
            if (token.Length == 0)
            {
                try { token = (Environment.GetEnvironmentVariable(TokenEnvironmentVar) ?? string.Empty).Trim(); }
                catch (Exception) { token = string.Empty; }
            }

            long id = 0;
            string problem = null;
            if (token.Length == 0 || token.IndexOf(':') < 0)
                problem = "BotToken is empty or malformed (set it in the config file or the " + TokenEnvironmentVar + " environment variable).";
            else if (!long.TryParse(cfgAuthorizedChatId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id <= 0)
                problem = "AuthorizedChatID must be your numeric Telegram user ID (a positive integer).";

            if (problem != null)
            {
                WriteOutput("CONFIG ERROR: " + problem + " Config file: " + path + ". Notifier is inactive until you fix it and press F5 or restart.");
                return false;
            }

            resolvedToken    = token;
            chatIdText       = cfgAuthorizedChatId;
            authorizedUserId = id;
            return true;
        }

        private static string GetString(Dictionary<string, string> kv, string key, string fallback)
        {
            string v;
            return kv.TryGetValue(key, out v) && v != null ? v : fallback;
        }

        private bool GetBool(Dictionary<string, string> kv, string key, bool fallback)
        {
            string v;
            if (!kv.TryGetValue(key, out v) || string.IsNullOrEmpty(v))
                return fallback;
            bool b;
            if (bool.TryParse(v, out b))
                return b;
            if (v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase))
                return true;
            if (v == "0" || v.Equals("no", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase))
                return false;
            WriteOutput("Config: could not parse " + key + "='" + v + "'; using " + fallback + ".");
            return fallback;
        }

        private int GetInt(Dictionary<string, string> kv, string key, int fallback)
        {
            string v;
            int i;
            if (!kv.TryGetValue(key, out v) || string.IsNullOrEmpty(v))
                return fallback;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                return i;
            WriteOutput("Config: could not parse " + key + "='" + v + "'; using " + fallback.ToString(CultureInfo.InvariantCulture) + ".");
            return fallback;
        }

        private double GetDouble(Dictionary<string, string> kv, string key, double fallback)
        {
            string v;
            double d;
            if (!kv.TryGetValue(key, out v) || string.IsNullOrEmpty(v))
                return fallback;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            WriteOutput("Config: could not parse " + key + "='" + v + "'; using " + fallback.ToString(CultureInfo.InvariantCulture) + ".");
            return fallback;
        }

        #endregion

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
                    CheckReentryAfterFlatten(ex);

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

        // Fills of the same instrument and side that arrive within FillAggregationMs are sent as one message,
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
                    await Task.Delay(FillAggregationMs);
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

        // Positive = the follower did worse than the leader (paid more on a buy, received less on a sell).
        private static double TickDifference(bool isBuy, double leaderAvg, double followerAvg, double tick)
        {
            return (isBuy ? followerAvg - leaderAvg : leaderAvg - followerAvg) / tick;
        }

        private static string DescribeTicks(double ticks)
        {
            double magnitude = Math.Round(Math.Abs(ticks), 1);
            if (magnitude < 0.1)
                return "same price";
            return magnitude.ToString("0.#", CultureInfo.InvariantCulture) + (magnitude == 1.0 ? " tick " : " ticks ") + (ticks > 0 ? "worse" : "better");
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

        // Hides internal copier order ids such as "73-007F6E72"; keeps readable names like "Entry" or "B27_Long".
        private static string CleanOrderName(string name)
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

            string command = ParseCommand(JsonString(message, "text"));
            if (command == null)
                return;

            WriteOutput("Remote command received: " + command);

            switch (command)
            {
                case "/status":
                    EnqueueQuiet(BuildStatusReport());
                    break;
                case "/flatten":
                    if (cfgFlattenConfirm)
                        RequestFlattenConfirmation();
                    else
                        await ExecuteFlattenAsync();
                    break;
                case "/version":
                    EnqueueQuiet(EmInfo + " DirectTelegramNotifier v" + AddOnVersion);
                    break;
                case "/diag":
                    EnqueueQuiet(BuildDiagReport());
                    break;
                case "/copy":
                    EnqueueQuiet(BuildCopyReport());
                    break;
                case "/eod":
                    EnqueueQuiet(BuildEodPreview());
                    break;
                case "/simulate":
                    RunSimulation();
                    break;
                case "/help":
                case "/start":
                    EnqueueQuiet(BuildHelpText());
                    break;
                default:
                    EnqueueQuiet(EmInfo + " Unknown command <code>" + Html(command) + "</code>. Send /help.");
                    break;
            }
        }

        private static string ParseCommand(string text)
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

        private async Task ExecuteFlattenAsync()
        {
            lastFlattenUtc = DateTime.UtcNow;
            reentryAlerted = false;

            StringBuilder sb = new StringBuilder();
            sb.Append(EmStop).Append(" <b>FLATTEN SENT</b>\n");

            try
            {
                if (cfgUseGlobalFlattenEverything)
                {
                    CbiAccount.FlattenEverything();
                    sb.Append("Scope: <b>ALL accounts</b> (FlattenEverything)\n");
                }
                else
                {
                    List<CbiAccount> accounts = SnapshotTrackedAccounts();
                    sb.Append("Scope: tracked accounts\n");
                    if (accounts.Count == 0)
                        sb.Append("No tracked accounts, nothing was sent.\n");

                    foreach (CbiAccount a in accounts)
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

            await Task.Delay(FlattenVerifyDelayMs);

            bool allFlat;
            string check = BuildPostFlattenCheck(out allFlat);
            sb.Append("\n").Append(allFlat ? EmOk + " <b>Flat</b>" : EmWarn + " <b>Exposure remains</b>")
              .Append(" after ").Append((FlattenVerifyDelayMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append("s\n")
              .Append(check);
            sb.Append("<i>Enabled strategies are not disabled and can re-enter.</i>");

            EnqueueText(sb.ToString());
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

        private string BuildPostFlattenCheck(out bool allFlat)
        {
            allFlat = true;
            StringBuilder sb = new StringBuilder();
            foreach (CbiAccount a in SnapshotTrackedAccounts())
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
                + "/status \u2014 connections, positions, strategies\n"
                + "/flatten \u2014 close positions and cancel working orders ("
                + (cfgUseGlobalFlattenEverything ? "ALL accounts" : "tracked accounts") + (cfgFlattenConfirm ? ", asks to confirm" : string.Empty) + ")\n"
                + "/copy \u2014 follower fills vs the leader today\n"
                + "/eod \u2014 preview the end-of-day report\n"
                + "/diag \u2014 health and diagnostics\n"
                + "/simulate \u2014 one sample of every alert (nothing real)\n"
                + "/version \u2014 add-on version\n"
                + "/help \u2014 this list";
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

        #region Schedule helpers

        private void InitializeSchedule()
        {
            eodTimeZone  = ResolveTimeZone(cfgEodTimeZoneId);
            eodTimeOfDay = ParseHhmmss(cfgEodReportTime);

            DateTime nowTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eodTimeZone);
            DateTime boundaryTz = nowTz.Date + eodTimeOfDay;

            if (nowTz.TimeOfDay < eodTimeOfDay)
                boundaryTz = boundaryTz.AddDays(-1);
            else
                lastEodReportDate = nowTz.Date;                  // started after today's report time: don't fire immediately

            // Execution timestamps are in NinjaTrader's configured time zone, which may differ from the PC clock.
            sessionStartNt = ConvertTzToLocal(boundaryTz) + NtClockOffset();
        }

        private TimeZoneInfo ResolveTimeZone(string id)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(id))
                    return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch (Exception)
            {
                WriteOutput("Unknown time zone id '" + id + "'. Falling back to the PC's local time zone.");
            }
            return TimeZoneInfo.Local;
        }

        private TimeSpan ParseHhmmss(int value)
        {
            int v = Math.Max(0, Math.Min(235959, value));
            int h = v / 10000;
            int m = (v / 100) % 100;
            int s = v % 100;
            if (h > 23 || m > 59 || s > 59 || v != value)
            {
                WriteOutput("Invalid EodReportTime " + value.ToString(CultureInfo.InvariantCulture) + " (expected HHMMSS). Using 170000.");
                return new TimeSpan(17, 0, 0);
            }
            return new TimeSpan(h, m, s);
        }

        private DateTime ConvertTzToLocal(DateTime tzTime)
        {
            try
            {
                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(tzTime, DateTimeKind.Unspecified), eodTimeZone, TimeZoneInfo.Local);
            }
            catch (Exception)
            {
                return DateTime.Now.Date;
            }
        }

        private static DateTime NtNow()
        {
            try { return NinjaTrader.Core.Globals.Now; }
            catch (Exception) { return DateTime.Now; }
        }

        private static TimeSpan NtClockOffset()
        {
            double minutes = (NtNow() - DateTime.Now).TotalMinutes;
            return TimeSpan.FromMinutes(Math.Round(minutes / 15.0) * 15.0);
        }

        #endregion

        #region Telegram transport (ordered queue, retry, backoff)

        private static HttpClient CreateHttpClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
            // The long poll holds one connection open; keep room for sends and button answers alongside it.
            try { ServicePointManager.FindServicePoint(new Uri(TelegramApiBase)).ConnectionLimit = 8; }
            catch (Exception) { }
            HttpClient client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;          // every request sets its own timeout
            return client;
        }

        private string ApiUrl(string method)
        {
            return TelegramApiBase + resolvedToken + "/" + method;
        }

        // Loud alert: always notifies.
        private void EnqueueText(string text)
        {
            Enqueue(text, false, null);
        }

        // Routine message: silent when SilentRoutineAlerts is on.
        private void EnqueueQuiet(string text)
        {
            Enqueue(text, cfgSilentRoutine, null);
        }

        private void EnqueueWithButtons(string text, string replyMarkup)
        {
            Enqueue(text, false, replyMarkup);
        }

        private void Enqueue(string text, bool silent, string replyMarkup)
        {
            LogAlert(text);
            sendQueue.Enqueue(new OutboundMessage { Text = text ?? string.Empty, CreatedUtc = DateTime.UtcNow, Silent = silent, ReplyMarkup = replyMarkup });
            try { sendSignal.Release(); } catch (Exception) { }
        }

        private async Task SenderLoopAsync()
        {
            while (true)
            {
                int delayMs = 0;
                try
                {
                    OutboundMessage msg;
                    if (!sendQueue.TryPeek(out msg))
                    {
                        if (stopping)
                            break;
                        await sendSignal.WaitAsync(1000);
                        continue;
                    }

                    if (stopping && DateTime.UtcNow > stopDeadlineUtc)
                        break;

                    CancellationTokenSource lifeCts = lifetimeCts;
                    SendOutcome outcome = await SendAsync(msg, lifeCts != null ? lifeCts.Token : CancellationToken.None);

                    if (outcome.Result == SendResult.Retry)
                    {
                        msg.Attempts++;
                        if ((DateTime.UtcNow - msg.CreatedUtc).TotalMinutes > SendRetryWindowMin)
                        {
                            OutboundMessage dropped;
                            sendQueue.TryDequeue(out dropped);
                            WriteOutput("Dropped a Telegram message after " + SendRetryWindowMin + " minutes of failed delivery.");
                        }
                        else
                        {
                            delayMs = outcome.RetryAfterSec > 0
                                ? outcome.RetryAfterSec * 1000
                                : Math.Min(60000, 1000 << Math.Min(msg.Attempts, 6));
                        }
                        if (stopping)
                            delayMs = Math.Min(delayMs, 500);
                    }
                    else
                    {
                        OutboundMessage done;
                        sendQueue.TryDequeue(out done);
                        if (outcome.Result == SendResult.Sent && !sendQueue.IsEmpty)
                            delayMs = stopping ? 200 : 1000;     // Telegram: about one message per second per chat
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
                    try { await Task.Delay(delayMs); }
                    catch (Exception) { }
                }
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
                return new SendOutcome(SendResult.Retry, (int)Math.Max(1, Math.Min(retryAfter, 300)));
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

            WriteOutput("sendMessage rejected (" + code.ToString(CultureInfo.InvariantCulture) + "): " + Truncate(description, 300) + hint);
            return new SendOutcome(SendResult.Permanent, 0);
        }

        #endregion

        #region Minimal JSON reader (no external dependencies)

        private static class MiniJson
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

        private static Dictionary<string, object> TryParseObject(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try { return MiniJson.Parse(json) as Dictionary<string, object>; }
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

        private void PingExternalHeartbeat()
        {
            string url = cfgHeartbeatUrl;
            if (string.IsNullOrEmpty(url) || uiFreezeAlerted)          // a frozen UI deliberately stops the pings
                return;

            DateTime now = DateTime.UtcNow;
            if ((now - lastHeartbeatPingUtc).TotalSeconds < cfgHeartbeatIntervalSec)
                return;
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

        // CME-style week in US Eastern time: closed Fri 17:00 to Sun 18:00 and daily 17:00 to 18:00.
        private bool IsMarketOpen(DateTime utc)
        {
            if (!string.Equals(cfgStaleDataSchedule, "cme", StringComparison.OrdinalIgnoreCase))
                return true;

            if (marketZone == null)
            {
                try { marketZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch (Exception) { marketZone = TimeZoneInfo.Local; }
            }

            DateTime et = TimeZoneInfo.ConvertTimeFromUtc(utc, marketZone);
            TimeSpan t = et.TimeOfDay;
            TimeSpan close = new TimeSpan(17, 0, 0);
            TimeSpan open = new TimeSpan(18, 0, 0);

            switch (et.DayOfWeek)
            {
                case DayOfWeek.Saturday: return false;
                case DayOfWeek.Sunday:   return t >= open;
                case DayOfWeek.Friday:   return t < close;
                default:                 return t < close || t >= open;
            }
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

        #region Buttons, clock check and diagnostics helpers

        // Optional safety step: /flatten shows Confirm / Cancel buttons. Only your own taps count and a tap is single-use.
        private void RequestFlattenConfirmation()
        {
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            lock (confirmLock)
            {
                confirmToken = token;
                confirmExpiresUtc = DateTime.UtcNow.AddSeconds(FlattenConfirmSec);
            }

            string markup = "{\"inline_keyboard\":[[{\"text\":\"" + EmStop + " Flatten now\",\"callback_data\":\"flat:" + token
                + "\"},{\"text\":\"Cancel\",\"callback_data\":\"cancel:" + token + "\"}]]}";

            EnqueueWithButtons(EmWarn + " <b>Confirm flatten?</b>\n"
                + "Scope: " + (cfgUseGlobalFlattenEverything ? "<b>ALL accounts</b>" : "tracked accounts") + "\n"
                + "Buttons expire in " + Num(FlattenConfirmSec) + "s.", markup);
        }

        private async Task HandleCallbackAsync(Dictionary<string, object> callback)
        {
            Dictionary<string, object> from = JsonObject(callback, "from");
            if (from == null || JsonLong(from, "id") != authorizedUserId)
                return;                                              // not you: ignored silently

            string id = JsonString(callback, "id");
            string data = JsonString(callback, "data") ?? string.Empty;

            bool isFlatten = data.StartsWith("flat:", StringComparison.Ordinal);
            bool isCancel = data.StartsWith("cancel:", StringComparison.Ordinal);
            string token = isFlatten ? data.Substring(5) : (isCancel ? data.Substring(7) : string.Empty);

            bool valid = false;
            lock (confirmLock)
            {
                if (token.Length > 0 && token == confirmToken)
                {
                    valid = DateTime.UtcNow <= confirmExpiresUtc;
                    confirmToken = string.Empty;                     // single use
                }
            }

            if (isFlatten && valid)
            {
                await AnswerCallbackAsync(id, "Flattening...");
                await ExecuteFlattenAsync();
            }
            else if (isCancel && valid)
            {
                await AnswerCallbackAsync(id, "Cancelled");
            }
            else
            {
                await AnswerCallbackAsync(id, "Expired. Send /flatten again.");
            }
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
                + "<i>/flatten does not disable strategies. Disable the strategy in the Strategies tab to stay flat.</i>";
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

        // Orders that NinjaTrader no longer lists can never come back, so their ids can be forgotten.
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
            sb.Append("  Waiting to send: ").Append(Num(sendQueue.Count)).Append("\n");
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

            EnqueueQuiet(EmInfo + " <b>SIMULATION</b>\nSample alerts follow. Nothing is wrong and nothing was changed.");

            FillGroup copied = new FillGroup { InstrumentName = "MNQ DEC26", IsBuy = true, FirstTime = now, SimLeader = leader, SimTick = 0.25 };
            copied.Legs[leader]    = new FillLeg { AccountName = leader,    ActionText = "Buy", Quantity = 3, Notional = 3 * 29411.00, FillCount = 1, FirstTime = now };
            copied.Legs[follower1] = new FillLeg { AccountName = follower1, ActionText = "Buy", Quantity = 3, Notional = 3 * 29410.00, FillCount = 1, FirstTime = now.AddMilliseconds(180) };
            copied.Legs[follower2] = new FillLeg { AccountName = follower2, ActionText = "Buy", Quantity = 3, Notional = 3 * 29411.25, FillCount = 2, FirstTime = now.AddMilliseconds(420) };
            EnqueueQuiet(BuildFillMessage(copied) + tag);

            FillGroup single = new FillGroup { InstrumentName = "MNQ DEC26", IsBuy = false, FirstTime = now };
            single.Legs[leader] = new FillLeg { AccountName = leader, ActionText = "Sell", OrderName = "ProfitTarget", Quantity = 3, Notional = 3 * 29435.75, FillCount = 1, FirstTime = now };
            EnqueueQuiet(BuildFillMessage(single) + tag);

            ConnNotice down = new ConnNotice { Connection = "MFFU", Down = true, Lost = true, Time = now, Detail = "Server closed the connection" };
            down.Channels.Add("orders");
            down.Channels.Add("data");
            EnqueueText(BuildConnectionMessage(down) + tag);

            ConnNotice up = new ConnNotice { Connection = "MFFU", Down = false, Time = now, Downtime = TimeSpan.FromSeconds(12) };
            up.Channels.Add("orders");
            up.Channels.Add("data");
            EnqueueQuiet(BuildConnectionMessage(up) + tag);

            EnqueueText(MsgFlapping("MFFU", "orders", 4) + tag);
            EnqueueText(MsgUiFrozen(52) + tag);
            EnqueueQuiet(MsgUiRecovered(TimeSpan.FromSeconds(71)) + tag);
            EnqueueText(MsgStrategyDisabled("MyStrategy", leader, "Realtime") + tag);
            EnqueueQuiet(MsgStrategyEnabled("MyStrategy", leader, "Realtime") + tag);
            EnqueueText(MsgStale("MNQ DEC26", TimeSpan.FromSeconds(140), true) + tag);
            EnqueueQuiet(MsgDataResumed("MNQ DEC26", TimeSpan.FromSeconds(185)) + tag);
            EnqueueText(MsgMismatch("MNQ DEC26", leader, 3, new List<string> { Html(follower1) + ": Flat, expected Long 3" }, TimeSpan.FromSeconds(25)) + tag);
            EnqueueQuiet(MsgInSync("MNQ DEC26", TimeSpan.FromSeconds(48)) + tag);
            EnqueueText(MsgOrderRate(34, cfgRunawayWindowSec, cfgRunawayMaxOrders, "MNQ DEC26 \u00D730, ES DEC26 \u00D74") + tag);
            EnqueueText(MsgReject(leader, "MNQ DEC26", "Sell", "StopMarket", "3", "Stop1", "Order price outside limits") + tag);
            EnqueueText(MsgReentry(leader, "MNQ DEC26", "BUY", 3, "B27_Long", 14) + tag);
            EnqueueText(MsgRestart(false, TimeSpan.FromSeconds(372), now.AddSeconds(-372), now) + tag);
            EnqueueText(MsgClockDrift(21) + tag);

            EnqueueQuiet(EmOk + " <b>Simulation finished</b>");
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
