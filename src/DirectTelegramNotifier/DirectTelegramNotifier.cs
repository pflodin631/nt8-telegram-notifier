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

// ============================================================================================
//  DirectTelegramNotifier  -  NinjaTrader 8 AddOn
//
//  Telegram alerts and remote commands for NinjaTrader 8, talking straight to the Telegram Bot
//  API over HttpClient (no Share Services, no third-party servers, no external DLLs).
//  Starts and stops with NinjaTrader itself, independent of any chart or workspace.
//
//  Alerts:    fills, connection loss/restore (orders and data), UI freeze, rejected orders,
//             order-rate spikes, strategy disabled, stale market data, trade-copier mismatch
//             (leader vs followers), unprotected positions, orphan and stuck orders, position limit,
//             drawdown floor (manual, static, end-of-day or intraday trailing), daily loss limit, risk UNKNOWN
//             while data is stale, NinjaTrader CPU/memory/disk, restart/crash detection, end-of-day summary.
//             Every alert is also appended to Documents\NinjaTrader 8\DirectTelegramNotifier.log.
//  Commands:  /status  /flatten  /cancel  /lock  /unlock  /risk  /floor  /leader  /copy  /eod  /diag  /simulate  /version  /help
//             (accepted only from AuthorizedChatID)
//  Safety:    optional auto-flatten that LOCKS the account (re-entries are closed again until /unlock or
//             the session ends); a short hold after /flatten that closes copier or strategy re-entries.
//  Optional:  external heartbeat ping (dead-man's switch, e.g. Healthchecks.io) that stops when alerts can't be delivered.
//
//  Files:     this add-on is a partial class split across the DirectTelegramNotifier.*.cs files in this folder,
//             plus DirectTelegramNotifier.Core.cs (pure logic, unit-tested outside NinjaTrader).
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
//  Version 0.3.0, MIT License. Not affiliated with or endorsed by NinjaTrader or Telegram.
//  Provided as is, without warranty. Automated trading and remote order control carry risk.
//
//  Written in C# 5 syntax so it compiles on any NinjaScript compiler version.
// ============================================================================================

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier : AddOnBase
    {
        #region Constants

        private const string TelegramApiBase      = "https://api.telegram.org/bot";
        private const string TokenEnvironmentVar  = "NT_TELEGRAM_BOT_TOKEN";
        private const string ConfigFileName       = "DirectTelegramNotifier.config";
        private const int    PollIntervalMs       = 3000;
        private const int    HousekeepingMs       = 5000;
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
        private const int    PollHttpTimeoutMs    = 35000;        // long poll (25s) plus margin
        private const int    PollIdleMs           = 250;
        private const int    FlattenConfirmSec    = 30;
        private const int    ClockDriftAlertSec   = 15;
        private const int    ReentryWindowSec     = 90;
        private const int    RiskTickMs           = 1000;         // floors, daily loss and locks are checked every second
        private const int    LockEnforceGapMs     = 3000;         // never re-flatten a locked account more often than this
        private const int    LockAlertGapSec      = 60;           // repeated lock enforcements are summarized at most once a minute
        private const int    RoutineMergeAt       = 4;            // this many routine messages waiting -> send them as one
        private const int    MergeMaxChars        = 3500;
        private const int    DeliveryStaleSec     = 180;          // no successful Telegram call for this long -> hold the external heartbeat
        private const string DefaultStuckStates   = "Submitted,ChangePending,ChangeSubmitted,CancelPending,CancelSubmitted";
        private const string StateFileName        = "DirectTelegramNotifier.state";
        private const string AddOnVersion        = "0.3.0";
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
        private const string EmLock   = "\U0001F512";
        private const string EmUnlock = "\U0001F513";
        private const string EmUp     = "\U0001F4C8";

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
            public bool Priority;          // problems and command replies: sent before routine messages
            public bool NoMerge;           // /simulate samples keep their own message
        }

        // Per-account risk bookkeeping. Guarded by riskLock; saved to the state file so restarts keep it.
        private sealed class RiskState
        {
            public double HighWater = double.NaN;        // trailing modes: highest closing (eod) or live (intraday) net liquidation
            public double TrailFloor = double.NaN;       // trailing modes: computed floor, never moves down
            public double SessionStart = double.NaN;     // net liquidation when the session began: the daily-loss baseline
            public string Session = string.Empty;        // which session SessionStart belongs to
            public bool   BaselineMidSession;            // tracking began after the session had started
            public double LastNetLiq = double.NaN;
            public int    DailyLevel;                    // highest daily-loss percentage already alerted
            public DateTime FloorBreachSince = DateTime.MinValue;
            public DateTime DailyBreachSince = DateTime.MinValue;
            public bool   Unknown;                       // holding a position while net liquidation can't be trusted
        }

        // A locked account is kept flat: anything that opens on it is closed again.
        private sealed class AccountLock
        {
            public string Reason;
            public string Session;                       // session locks end when this session ends
            public DateTime UntilUtc = DateTime.MaxValue; // a short hold after /flatten ends at this time
            public DateTime SinceUtc;
            public DateTime LastEnforceUtc;
            public DateTime LastAlertUtc = DateTime.MinValue;
            public int Enforced;
            public bool IsHold { get { return UntilUtc != DateTime.MaxValue; } }
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
        private int    cfgProtectiveCheckSec = 0;
        private int    cfgOrphanOrderSec = 20;
        private string cfgOrphanOrderNames = "Stop,Target";
        private int    cfgStuckOrderSec = 30;
        private int    cfgMaxPositionContracts = 0;
        private double cfgRiskFloor = double.NaN;
        private bool   cfgRiskAutoFlatten = false;
        private double cfgRiskFlattenBuffer = 150.0;
        private int    cfgCpuAlertPercent = 90;
        private int    cfgCpuAlertSeconds = 180;
        private int    cfgMemoryAlertMb = 0;
        private int    cfgDiskAlertMb = 2000;
        private int    cfgFillGroupMs = 2000;
        private string cfgStaleDataQuietWindows = string.Empty;
        private string cfgStaleDataTimeZone = "Eastern Standard Time";
        private string staleZoneId = "Eastern Standard Time";
        private bool   staleScheduleAlways;
        private List<Dtn.QuietWindow> staleQuietWindows = new List<Dtn.QuietWindow>();
        private HashSet<string> stuckStates = new HashSet<string>(DefaultStuckStates.Split(','), StringComparer.OrdinalIgnoreCase);
        private string cfgDrawdownMode = "manual";
        private double cfgStartingBalance = double.NaN;
        private double cfgDrawdownAmount = double.NaN;
        private double cfgFloorLock = double.NaN;
        private string cfgDailyLossLimit = string.Empty;
        private List<double> dailyLossAlerts = new List<double>();
        private bool   cfgDailyLossAutoFlatten = false;
        private int    cfgRiskConfirmSec = 2;
        private int    cfgFlattenHoldSec = 30;
        private double cfgFloorJumpConfirm = 1000.0;
        private List<string> configWarnings = new List<string>();
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

        // Two lanes: problems and command replies first, routine messages after (merged when they pile up).
        private readonly object queueLock = new object();
        private readonly List<OutboundMessage> priorityLane = new List<OutboundMessage>();
        private readonly List<OutboundMessage> routineLane = new List<OutboundMessage>();
        private DateTime rateLimitedUntilUtc = DateTime.MinValue;      // Telegram said "wait": nothing is sent before this
        private DateTime lastProbeOkUtc = DateTime.MinValue;
        private int probeBusy;
        private string heartbeatHoldReason = string.Empty;
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

        private readonly object episodeLock = new object();
        private readonly Dictionary<string, DateTime> episodeStart = new Dictionary<string, DateTime>();
        private readonly HashSet<string> episodeAlerted = new HashSet<string>();
        private readonly Dictionary<string, StateStamp> orderStateSince = new Dictionary<string, StateStamp>();     // guarded by orderLock
        private readonly HashSet<string> stuckAlerted = new HashSet<string>();                                       // guarded by orderLock

        private readonly object riskLock = new object();
        private readonly Dictionary<string, string> accountSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // "key|Account" -> value, from Key:Account= lines
        private readonly Dictionary<string, double> floorOverrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);  // from /floor ("*" = all)
        private readonly Dictionary<string, double> riskLevel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RiskState> riskStates = new Dictionary<string, RiskState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AccountLock> accountLocks = new Dictionary<string, AccountLock>(StringComparer.OrdinalIgnoreCase);
        private List<double> riskBuffers = new List<double>();
        private bool riskReadProblemReported;
        private string leaderOverride = string.Empty;          // set with /leader, saved in the state file
        private bool riskStateDirty;
        private DateTime lastRiskSaveUtc = DateTime.MinValue;
        private System.Threading.Timer riskTimer;
        private int riskBusy;

        private DateTime lastCpuSampleUtc = DateTime.MinValue;
        private double lastCpuSeconds;
        private double lastCpuPercent;
        private DateTime cpuHighSince = DateTime.MinValue;
        private DateTime lastCpuAlertUtc = DateTime.MinValue;
        private int lastMemoryMb;
        private DateTime lastMemAlertUtc = DateTime.MinValue;
        private long lastDiskFreeMb = -1;
        private DateTime lastDiskCheckUtc = DateTime.MinValue;
        private DateTime lastDiskAlertUtc = DateTime.MinValue;
        private string confirmFilter = string.Empty;

        private readonly object confirmLock = new object();
        private string confirmToken = string.Empty;
        private DateTime confirmExpiresUtc = DateTime.MinValue;
        private string confirmKind = string.Empty;             // "flatten", "lock" or "floor"
        private double confirmValue = double.NaN;

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
            LoadFloorOverrides();

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
            LoadRiskState();

            senderTask = Task.Run(() => SenderLoopAsync());

            // Always subscribed: it re-attaches accounts after reconnects even with the watchdog off.
            CbiConnection.ConnectionStatusUpdate += OnConnectionStatusUpdate;

            Task.Run(new Action(AttachAccounts));

            housekeepingTimer = new System.Threading.Timer(HousekeepingCallback, null, 5000, HousekeepingMs);
            riskTimer = new System.Threading.Timer(RiskTimerCallback, null, 3000, RiskTickMs);

            if (cfgEnableRemoteCommands)
                pollTimer = new System.Threading.Timer(PollTimerCallback, null, 2000, Timeout.Infinite);

            WriteOutput("v" + AddOnVersion + " active. EOD report at " + eodTimeOfDay.ToString(@"hh\:mm\:ss") + " " + eodTimeZone.Id + ". Config: " + ConfigPath());

            if (cfgEnableWatchdog)
            {
                EnqueueQuiet(BuildOnlineMessage());
                if (!string.IsNullOrEmpty(startupGapNote))
                    EnqueueText(startupGapNote);
            }
            if (configWarnings.Count > 0)
                EnqueueText(MsgConfigWarning(configWarnings));
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
            if (cfgProtectiveCheckSec > 0) active.Add("stop check " + Num(cfgProtectiveCheckSec) + "s");
            if (cfgOrphanOrderSec > 0)     active.Add("orphan orders " + Num(cfgOrphanOrderSec) + "s");
            if (cfgStuckOrderSec > 0)      active.Add("stuck orders " + Num(cfgStuckOrderSec) + "s");
            if (cfgMaxPositionContracts > 0) active.Add("position limit " + Num(cfgMaxPositionContracts));
            string riskText = RiskFeaturesText();
            if (riskText.Length > 0) active.Add(riskText);
            if (cfgCpuAlertPercent > 0 || cfgDiskAlertMb > 0 || cfgMemoryAlertMb > 0) active.Add("machine health");

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
            DisposeTimer(ref riskTimer);
            SaveRiskState();

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
                CheckExposure();
                CheckMachineHealth();
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
    }
}
