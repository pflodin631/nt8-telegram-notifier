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

// DirectTelegramNotifier: The config file (template, loading, validation) and the end-of-day schedule.
// One part of a partial class: see DirectTelegramNotifier.cs for the overview.

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier
    {
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
                 + "# StaleDataSchedule: when no ticks are expected, so silence is not an alarm.\r\n"
                 + "#   cme    = CME-style futures week: daily 17:00-18:00 and Fri 17:00 to Sun 18:00, US Eastern\r\n"
                 + "#   always = the feed should tick around the clock (crypto, or to switch the schedule off)\r\n"
                 + "#   custom = your own quiet windows below, in StaleDataTimeZone\r\n"
                 + "StaleDataSchedule=cme\r\n"
                 + "# StaleDataQuietWindows: used with custom. Semicolon-separated 'Days HH:mm-HH:mm'; days are Mon..Sun, ranges (Mon-Thu), Daily, Weekdays or Weekends.\r\n"
                 + "# A window cannot cross midnight: split it. Example (US stocks, Eastern): Weekdays 00:00-09:30; Weekdays 16:00-24:00; Weekends 00:00-24:00\r\n"
                 + "StaleDataQuietWindows=\r\n"
                 + "# StaleDataTimeZone: Windows time zone id for custom windows, e.g. Eastern Standard Time, Central Europe Standard Time, GMT Standard Time.\r\n"
                 + "StaleDataTimeZone=Eastern Standard Time\r\n"
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
                 + "# --- Safety checks ---\r\n"
                 + "# ProtectiveCheckSeconds: alert when an open position has no working stop order for this long. 0 = off.\r\n"
                 + "# Turn it on only if your strategies place real stop orders (not internal stop logic).\r\n"
                 + "ProtectiveCheckSeconds=0\r\n"
                 + "# OrphanOrderSeconds: alert when an account is flat but exit-type orders (name contains one of OrphanOrderNames) keep working this long. 0 = off.\r\n"
                 + "OrphanOrderSeconds=20\r\n"
                 + "OrphanOrderNames=Stop,Target\r\n"
                 + "# StuckOrderSeconds: alert when an order sits in one of StuckOrderStates this long. 0 = off.\r\n"
                 + "StuckOrderSeconds=30\r\n"
                 + "# StuckOrderStates: NinjaTrader order states that should pass quickly. Add Accepted only if your broker moves orders past it quickly.\r\n"
                 + "StuckOrderStates=Submitted,ChangePending,ChangeSubmitted,CancelPending,CancelSubmitted\r\n"
                 + "# MaxPositionContracts: alert when an account holds more than this many contracts in total. 0 = off.\r\n"
                 + "MaxPositionContracts=0\r\n"
                 + "\r\n"
                 + "# --- Risk: drawdown floor and daily loss limit ---\r\n"
                 + "# Enter YOUR rules: these are not firm presets. Check /risk against your broker's or firm's own dashboard.\r\n"
                 + "# Any line marked (per account) can also be set for one account:  Key:AccountName=value  e.g.  DrawdownAmount:Acct002=2500\r\n"
                 + "#\r\n"
                 + "# DrawdownMode (per account): how the floor is found.\r\n"
                 + "#   off      = no floor checks\r\n"
                 + "#   manual   = the floor is RiskFloor below, or whatever you set with /floor\r\n"
                 + "#   static   = StartingBalance - DrawdownAmount; it never moves\r\n"
                 + "#   eod      = trails the highest end-of-day balance (at EodReportTime): floor = high - DrawdownAmount, capped at FloorLock\r\n"
                 + "#   intraday = trails the highest live net liquidation, including open profit, capped at FloorLock\r\n"
                 + "# A /floor setting always overrides the mode until /floor clear.\r\n"
                 + "DrawdownMode=manual\r\n"
                 + "# RiskFloor (per account): the floor for manual mode, in net liquidation dollars. Blank = none.\r\n"
                 + "RiskFloor=\r\n"
                 + "# StartingBalance (per account): account size. Needed for static; trailing modes start from it (else from current net liquidation).\r\n"
                 + "StartingBalance=\r\n"
                 + "# DrawdownAmount (per account): the maximum drawdown in dollars, for static, eod and intraday.\r\n"
                 + "DrawdownAmount=\r\n"
                 + "# FloorLock (per account): a trailing floor stops rising at this value (often starting balance + a little). Blank = never locks.\r\n"
                 + "FloorLock=\r\n"
                 + "# RiskBufferAlerts: alert as net liquidation comes within these many dollars of the floor.\r\n"
                 + "RiskBufferAlerts=1500,750,300\r\n"
                 + "# RiskAutoFlatten: true = an account that stays within RiskFlattenBuffer of its floor for RiskConfirmSeconds is flattened and LOCKED:\r\n"
                 + "# anything that opens on it afterwards is closed again until /unlock or the session ends. Test on Sim first.\r\n"
                 + "RiskAutoFlatten=false\r\n"
                 + "RiskFlattenBuffer=150\r\n"
                 + "# RiskConfirmSeconds: how long a breach must last before auto-flatten acts (filters a single bad reading). 1-30.\r\n"
                 + "RiskConfirmSeconds=2\r\n"
                 + "# FloorJumpConfirm: /floor asks for a button press when it moves a floor by more than this, or puts it inside your alert levels. 0 = only the latter.\r\n"
                 + "FloorJumpConfirm=1000\r\n"
                 + "#\r\n"
                 + "# DailyLossLimit (per account): maximum loss per session, measured from net liquidation at the session start (EodReportTime).\r\n"
                 + "# Dollars (1000) or percent of the session-start value (2%). Blank = off.\r\n"
                 + "DailyLossLimit=\r\n"
                 + "# DailyLossAlerts: percentages of the limit that alert.\r\n"
                 + "DailyLossAlerts=50,75,90\r\n"
                 + "# DailyLossAutoFlatten: true = flatten and lock an account that reaches its daily loss limit.\r\n"
                 + "DailyLossAutoFlatten=false\r\n"
                 + "\r\n"
                 + "# --- Machine health (the NinjaTrader process) ---\r\n"
                 + "# CpuAlertPercent / CpuAlertSeconds: alert when NinjaTrader's CPU stays at or above this level this long. 0 = off.\r\n"
                 + "CpuAlertPercent=90\r\n"
                 + "CpuAlertSeconds=180\r\n"
                 + "# MemoryAlertMB: alert when NinjaTrader uses more memory than this. 0 = off.\r\n"
                 + "MemoryAlertMB=0\r\n"
                 + "# DiskAlertMB: alert when the drive holding the config folder has less free space than this. 0 = off.\r\n"
                 + "DiskAlertMB=2000\r\n"
                 + "\r\n"
                 + "# --- Convenience ---\r\n"
                 + "# FlattenConfirm: true = /flatten and /lock first show Confirm / Cancel buttons (expire after 30 s). false = act immediately.\r\n"
                 + "FlattenConfirm=false\r\n"
                 + "# FlattenHoldSeconds: after /flatten, anything that opens on those accounts in this many seconds is closed again\r\n"
                 + "# (catches a copier or strategy putting the position back, or flipping a follower). 0 = off.\r\n"
                 + "FlattenHoldSeconds=30\r\n"
                 + "# FillGroupMs: fills of the same instrument and side within this many milliseconds become one message (500-10000).\r\n"
                 + "FillGroupMs=2000\r\n"
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
            cfgProtectiveCheckSec         = Math.Max(0, GetInt(kv, "ProtectiveCheckSeconds", 0));
            cfgOrphanOrderSec             = Math.Max(0, GetInt(kv, "OrphanOrderSeconds", 20));
            cfgOrphanOrderNames           = GetString(kv, "OrphanOrderNames", "Stop,Target");
            cfgStuckOrderSec              = Math.Max(0, GetInt(kv, "StuckOrderSeconds", 30));
            cfgMaxPositionContracts       = Math.Max(0, GetInt(kv, "MaxPositionContracts", 0));
            cfgRiskFloor                  = ParseOptionalDouble(GetString(kv, "RiskFloor", string.Empty));
            riskBuffers                   = ParseDollarList(GetString(kv, "RiskBufferAlerts", "1500,750,300"));
            cfgRiskAutoFlatten            = GetBool(kv, "RiskAutoFlatten", false);
            cfgRiskFlattenBuffer          = Math.Max(0.0, GetDouble(kv, "RiskFlattenBuffer", 150.0));
            cfgCpuAlertPercent            = Math.Max(0, Math.Min(100, GetInt(kv, "CpuAlertPercent", 90)));
            cfgCpuAlertSeconds            = Math.Max(30, GetInt(kv, "CpuAlertSeconds", 180));
            cfgMemoryAlertMb              = Math.Max(0, GetInt(kv, "MemoryAlertMB", 0));
            cfgDiskAlertMb                = Math.Max(0, GetInt(kv, "DiskAlertMB", 2000));
            cfgFillGroupMs                = Math.Max(500, Math.Min(10000, GetInt(kv, "FillGroupMs", 2000)));
            cfgStaleDataQuietWindows      = GetString(kv, "StaleDataQuietWindows", string.Empty);
            cfgStaleDataTimeZone          = GetString(kv, "StaleDataTimeZone", "Eastern Standard Time");
            cfgDrawdownMode               = GetString(kv, "DrawdownMode", "manual");
            cfgStartingBalance            = ParseOptionalDouble(GetString(kv, "StartingBalance", string.Empty));
            cfgDrawdownAmount             = ParseOptionalDouble(GetString(kv, "DrawdownAmount", string.Empty));
            cfgFloorLock                  = ParseOptionalDouble(GetString(kv, "FloorLock", string.Empty));
            cfgDailyLossLimit             = GetString(kv, "DailyLossLimit", string.Empty);
            dailyLossAlerts               = ParseDollarList(GetString(kv, "DailyLossAlerts", "50,75,90"));
            cfgDailyLossAutoFlatten       = GetBool(kv, "DailyLossAutoFlatten", false);
            cfgRiskConfirmSec             = Math.Max(1, Math.Min(30, GetInt(kv, "RiskConfirmSeconds", 2)));
            cfgFlattenHoldSec             = Math.Max(0, Math.Min(600, GetInt(kv, "FlattenHoldSeconds", 30)));
            cfgFloorJumpConfirm           = Math.Max(0.0, GetDouble(kv, "FloorJumpConfirm", 1000.0));

            List<string> warnings = new List<string>();
            foreach (string unknown in Dtn.FindUnknownKeys(kv.Keys))
                warnings.Add("Unrecognized setting " + unknown);
            ApplyStaleSchedule(warnings);
            stuckStates = ParseStuckStates(GetString(kv, "StuckOrderStates", DefaultStuckStates), warnings);

            // Per-account overrides:  RiskFloor:Account=, DrawdownMode:Account=, StartingBalance:Account= ...
            lock (riskLock)
            {
                accountSettings.Clear();
                foreach (KeyValuePair<string, string> pair in kv)
                {
                    int colon = pair.Key.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    string baseKey = pair.Key.Substring(0, colon).Trim();
                    string account = pair.Key.Substring(colon + 1).Trim();
                    if (account.Length > 0 && Dtn.IsPerAccountKey(baseKey))
                        accountSettings[baseKey.ToLowerInvariant() + "|" + account] = (pair.Value ?? string.Empty).Trim();
                }
            }
            if (Dtn.NormalizeMode(cfgDrawdownMode) == null)
                warnings.Add("DrawdownMode '" + cfgDrawdownMode + "' is not off, manual, static, eod or intraday; using manual");
            lock (riskLock)
            {
                foreach (KeyValuePair<string, string> pair in accountSettings)
                {
                    if (pair.Key.StartsWith("drawdownmode|", StringComparison.Ordinal) && Dtn.NormalizeMode(pair.Value) == null)
                        warnings.Add("DrawdownMode:" + pair.Key.Substring("drawdownmode|".Length) + " '" + pair.Value + "' is not recognized; using manual");
                }
            }
            configWarnings = warnings;
            foreach (string w in warnings)
                WriteOutput("Config warning: " + w);
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
    }
}
