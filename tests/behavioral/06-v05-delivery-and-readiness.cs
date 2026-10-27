using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Net; using System.Net.Http; using System.Reflection; using System.Text.RegularExpressions; using System.Threading; using System.Threading.Tasks;
using NinjaTrader.Cbi;
class H8
{
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dtn-h8");
    static object O; static Type T;
    static BindingFlags B = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object Call(string name, params object[] args)
    {
        foreach (var m in T.GetMethods(B)) if (m.Name == name && m.GetParameters().Length == args.Length) return m.Invoke(m.IsStatic ? null : O, args);
        throw new Exception("no method " + name);
    }
    static void Set(string f, object v) { var fi = T.GetField(f, B); if (fi == null) throw new Exception("no field " + f); fi.SetValue(O, v); }
    static object Get(string f) { var fi = T.GetField(f, B); if (fi == null) throw new Exception("no field " + f); return fi.GetValue(O); }
    static object F(object o, string f) { return o.GetType().GetField(f, B).GetValue(o); }
    static void SetF(object o, string f, object v) { o.GetType().GetField(f, B).SetValue(o, v); }
    static string Plain(string s) { return Regex.Replace(s, "<[^>]+>", "").Replace("&amp;", "&"); }
    class Msg { public string Text; public bool Silent; public bool Priority; public string Markup; }
    static List<Msg> Drain()
    {
        var res = new List<Msg>();
        while (true)
        {
            object m = Call("PeekNextMessage"); if (m == null) break; Call("RemoveMessage", m);
            res.Add(new Msg { Text = Plain((string)F(m, "Text")), Silent = (bool)F(m, "Silent"), Priority = (bool)F(m, "Priority"), Markup = (string)F(m, "ReplyMarkup") });
        }
        return res;
    }
    static bool Any(List<Msg> l, string needle) { return l.Any(x => x.Text.Contains(needle)); }
    static int fails = 0, passes = 0;
    static void Check(string what, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (ok) passes++; else fails++; }
    static Instrument mnq = new Instrument { FullName = "MNQ DEC26", MasterInstrument = new MasterInstrument { PointValue = 2.0, TickSize = 0.25 } };
    static void Pos(Account a, MarketPosition mp, int q) { a.Positions.Clear(); if (q > 0) a.Positions.Add(new Position { Instrument = mnq, MarketPosition = mp, Quantity = q, AveragePrice = 29400 }); }
    static int execSeq = 0;
    static void Fill(Account a, bool buy, int qty, string orderName)
    {
        var ex = new Execution { Account = a, Instrument = mnq, Quantity = qty, Price = 29400, Time = DateTime.Now, ExecutionId = "x" + (++execSeq), MarketPosition = buy ? MarketPosition.Long : MarketPosition.Short, Order = new Order { OrderId = "o" + execSeq, Name = orderName, OrderAction = buy ? OrderAction.Buy : OrderAction.Sell } };
        Call("OnExecutionUpdate", null, new ExecutionEventArgs { Execution = ex });
    }
    static object LockOf(string acct) { var d = (IDictionary)Get("accountLocks"); return d.Contains(acct) ? d[acct] : null; }
    static object Rs(string acct) { return Call("RiskStateFor", acct); }
    static void AgeLock(string acct) { var lk = LockOf(acct); if (lk != null) SetF(lk, "LastEnforceUtc", DateTime.UtcNow.AddSeconds(-10)); }
    static void Setting(string key, string acct, string v) { lock (Get("riskLock")) ((IDictionary)Get("accountSettings"))[key.ToLowerInvariant() + "|" + acct] = v; }
    static Dictionary<string, object> Update(string text) { return new Dictionary<string, object> { { "message", new Dictionary<string, object> { { "from", new Dictionary<string, object> { { "id", 42L }, { "is_bot", false } } }, { "date", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds }, { "text", text } } } }; }
    static Dictionary<string, object> Button(string data) { return new Dictionary<string, object> { { "callback_query", new Dictionary<string, object> { { "id", "cb" }, { "from", new Dictionary<string, object> { { "id", 42L } } }, { "data", data } } } }; }
    static void Wait(Task t) { t.Wait(); }

    static void Main()
    {
        if (System.IO.Directory.Exists(Dir)) System.IO.Directory.Delete(Dir, true);
        System.IO.Directory.CreateDirectory(Dir);
        NinjaTrader.Core.Globals.UserDataDir = Dir;
        T = Type.GetType("NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifier, AddOn");
        O = Activator.CreateInstance(T, true);
        Set("flattenCheckMaxMs", 1500); Set("autoFlattenWatchMs", 0); Set("cfgFlattenHoldSec", 0);
        Set("cfgAccountName", "LEAD,F1,F2,G1"); Set("cfgLeaderAccount", "LEAD"); Set("authorizedUserId", 42L);
        Set("startedUtc", DateTime.UtcNow.AddMinutes(-30)); Set("started", true);
        Set("eodTimeZone", TimeZoneInfo.Utc); Set("eodTimeOfDay", new TimeSpan(21, 0, 0));
        Set("riskBuffers", Call("ParseDollarList", "1500,750,300"));
        Set("inactivityAlerts", Call("ParseDollarList", "3,2,1"));
        Set("cfgEnableAlertLog", true); Set("alertLogPath", Dir + "/DirectTelegramNotifier.log");
        Connection.Connections.Clear();
        var connOk = new Connection { Status = ConnectionStatus.Connected, PriceStatus = ConnectionStatus.Connected, Options = new ConnectionOptions { Name = "Orders+Data" } };
        var connBad = new Connection { Status = ConnectionStatus.Connected, PriceStatus = ConnectionStatus.Connected, Options = new ConnectionOptions { Name = "OtherFeed" } };
        Connection.Connections.Add(connOk); Connection.Connections.Add(connBad);
        var L = new Account { Name = "LEAD", NetLiq = 50000 }; var F1 = new Account { Name = "F1", NetLiq = 50000 }; var F2 = new Account { Name = "F2", NetLiq = 50000 }; var G1 = new Account { Name = "G1", NetLiq = 50000 };
        var tracked = (IList)Get("trackedAccounts"); tracked.Add(L); tracked.Add(F1); tracked.Add(F2); tracked.Add(G1);
        L.Connection = connOk; F1.Connection = connOk; F2.Connection = connOk; G1.Connection = connBad;
        List<Msg> d; string token;

        // the backup channel's sender is replaced by a recorder
        List<string[]> sent = new List<string[]>();
        Func<string, string, string, Task> recorder = delegate(string u, string b, string c) { lock (sent) { sent.Add(new[] { u, b, c }); } return Task.FromResult(0); };
        Action settle = () => Thread.Sleep(300);
        Func<string, int> sentWith = needle => { lock (sent) return sent.Count(x => x[1].Contains(needle)); };
        Func<bool> noAmounts = () => { lock (sent) return sent.All(x => !Regex.IsMatch(x[1], @"\d[\d,]*\.\d\d") && !x[1].ToLowerInvariant().Contains("net liq") && !x[1].Contains("MNQ") && !x[1].Contains("Long ") && !x[1].Contains("Short ")); };
        Func<string, string, string> upd = (id, text) => text;

        Console.WriteLine("\n--- 1. every permanent send refusal holds the heartbeat, not just three");
        Call("InterpretResponse", new HttpResponseMessage(HttpStatusCode.NotFound), "{\"ok\":false,\"error_code\":404,\"description\":\"Not Found\"}");
        Check("a 404 (wrong token) is a refusal", (DateTime)Get("lastFatalSendUtc") > DateTime.MinValue);
        Set("lastFatalSendUtc", DateTime.MinValue);
        Call("InterpretResponse", new HttpResponseMessage(HttpStatusCode.BadRequest), "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is too long\"}");
        Check("a message-specific 400 is not", (DateTime)Get("lastFatalSendUtc") == DateTime.MinValue);

        Console.WriteLine("\n--- 2. /unlock asks first when buttons are on");
        Set("cfgFlattenConfirm", true);
        Call("ExecuteLockCommand", "F1");
        object reply = null;
        Wait((Task)Call("HandleUpdateAsync", Update("/unlock F1"))); d = Drain();
        Check("a prompt, and the lock is still there", Any(d, "Confirm unlock?") && LockOf("F1") != null && d.Any(m => m.Markup != null));
        token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Wait((Task)Call("HandleUpdateAsync", Button("cancel:" + token))); Drain();
        Check("Cancel leaves it locked", LockOf("F1") != null);
        Wait((Task)Call("HandleUpdateAsync", Update("/unlock F1"))); d = Drain();
        token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + token))); d = Drain();
        Check("the button unlocks it", LockOf("F1") == null && Any(d, "UNLOCKED"));
        Set("cfgFlattenConfirm", false);
        Call("ExecuteLockCommand", "F1");
        Wait((Task)Call("HandleUpdateAsync", Update("/unlock F1"))); d = Drain();
        Check("with buttons off it unlocks at once", LockOf("F1") == null && Any(d, "UNLOCKED"));

        Console.WriteLine("\n--- 3. the audit log records commands, buttons and refused senders");
        Wait((Task)Call("HandleUpdateAsync", Update("/status"))); Drain();
        Set("cfgFlattenConfirm", true);
        Wait((Task)Call("HandleUpdateAsync", Update("/flatten F2"))); d = Drain();
        token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Wait((Task)Call("HandleUpdateAsync", Button("cancel:" + token))); Drain();
        Wait((Task)Call("HandleUpdateAsync", Button("ok:deadbeef"))); Drain();
        Set("cfgFlattenConfirm", false);
        var stranger = new Dictionary<string, object> { { "message", new Dictionary<string, object> { { "from", new Dictionary<string, object> { { "id", 99L }, { "is_bot", false } } }, { "date", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds }, { "text", "/flatten" } } } };
        Wait((Task)Call("HandleUpdateAsync", stranger)); d = Drain();
        string log = System.IO.File.ReadAllText(Dir + "/DirectTelegramNotifier.log");
        Check("an authorized command is logged", log.Contains("AUDIT command /status"));
        Check("a command with an argument is logged with it", log.Contains("AUDIT command /flatten F2"));
        Check("a button press is logged, and so is cancelling", log.Contains("AUDIT button cancel flatten F2"));
        Check("a stale or unknown button is logged as such", log.Contains("AUDIT button confirm  (expired or already used)") || log.Contains("(expired or already used)"));
        Check("a message from another Telegram user is logged and does nothing", log.Contains("AUDIT ignored a message from another Telegram user (id 99)") && d.Count == 0 && F2.FlattenCount == 0);

        Console.WriteLine("\n--- 4. a flatten is followed until it is really flat, and the outcome is reported");
        Set("cfgBackupUrl", "https://ntfy.example/secret-topic"); Set("backupPost", recorder); Set("cfgBackupFormat", "text");
        Set("autoFlattenWatchMs", 1500); Set("exposureRepeatMs", 3600000);      // repeats are tested in section 8: here they must not fire
        Pos(F1, MarketPosition.Long, 2);
        var clear1 = new Thread(() => { Thread.Sleep(300); Pos(F1, MarketPosition.Flat, 0); }); clear1.Start();
        string lockText = Plain((string)Call("LockAndFlatten", F1, "test breach"));
        Check("the lock alert itself comes back at once", lockText.Contains("ACCOUNT LOCKED"));
        Check("nothing about the outcome yet", Drain().Count == 0);
        Thread.Sleep(2300); d = Drain();
        Check("it went flat: FLAT CONFIRMED, quiet, no backup alert", Any(d, "FLAT CONFIRMED") && Any(d, "automatic flatten") && !d.First(m => m.Text.Contains("FLAT CONFIRMED")).Priority && sentWith("STILL EXPOSED") == 0);
        ((IDictionary)Get("accountLocks")).Remove("F1"); Pos(F1, MarketPosition.Long, 2); lock (sent) sent.Clear();
        Call("LockAndFlatten", F1, "test breach"); Drain();
        Thread.Sleep(2800); d = Drain(); settle();
        Check("it never went flat: STILL EXPOSED, loud, with what is open", Any(d, "STILL EXPOSED") && Any(d, "Long 2") && d.First(m => m.Text.Contains("STILL EXPOSED")).Priority && !d.First(m => m.Text.Contains("STILL EXPOSED")).Silent);
        Check("...and it also went to the backup channel, as a short message", sentWith("STILL EXPOSED (") == 1 && sentWith("account F1 did not go flat. Close it by hand now.") == 1 && noAmounts());
        ((IDictionary)Get("accountLocks")).Remove("F1");

        Set("flattenCheckMaxMs", 5000); Pos(F1, MarketPosition.Long, 1);
        var clear2 = new Thread(() => { Thread.Sleep(700); Pos(F1, MarketPosition.Flat, 0); }); clear2.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Wait((Task)Call("ExecuteFlattenAsync", "F1")); long ms = sw.ElapsedMilliseconds; d = Drain();
        Check("/flatten stops checking as soon as it is flat (" + ms + " ms of a 5,000 ms budget)", ms >= 900 && ms < 2500 && Any(d, "Flat") && Any(d, "after 1.0s") && !Any(d, "STILL EXPOSED"));
        Set("flattenCheckMaxMs", 1200); Pos(F1, MarketPosition.Long, 1); lock (sent) sent.Clear();
        lock (Get("backupLock")) ((IDictionary)Get("backupRecent")).Clear();      // an identical alert a moment ago would be (rightly) suppressed
        Wait((Task)Call("ExecuteFlattenAsync", "F1")); d = Drain(); settle();
        Check("a flatten that never completes says STILL EXPOSED and tells you what to do", Any(d, "STILL EXPOSED") && Any(d, "close it by hand") && Any(d, "not sent again"));
        Check("...loudly, and through the backup channel", d.First(m => m.Text.Contains("STILL EXPOSED")).Priority && sentWith("STILL EXPOSED") == 1);
        Pos(F1, MarketPosition.Flat, 0); Set("autoFlattenWatchMs", 0); Set("flattenCheckMaxMs", 1500);

        Console.WriteLine("\n--- 5. the self-test says whether auto-flatten could act");
        Set("cfgRiskAutoFlatten", true);
        Setting("RiskFloor", "LEAD", "48000"); Setting("RiskFloor", "F1", "48000");
        F2.NetLiq = double.NaN;                                         // F2: net liquidation cannot be read
        Setting("DrawdownMode", "G1", "eod"); Setting("StartingBalance", "G1", "50000");   // G1: a trailing mode with no DrawdownAmount
        Set("cfgDrawdownAmount", double.NaN);
        string st = Plain((string)Call("BuildSelfTestText", Call("AssessReadiness")));
        Check("a complete account is ARMED", Regex.IsMatch(st, "Auto-flatten: ARMED for [^\\n]*LEAD") && st.Contains("LEAD") );
        Check("F2 cannot read net liquidation and is NOT ARMED, with the reason", st.Contains("NOT ARMED for") && st.Contains("F2") && st.Contains("net liquidation cannot be read") && st.Contains("Net liq UNREADABLE"));
        Check("G1 has a trailing mode but no DrawdownAmount: reported, with no floor to act on", st.Contains("DrawdownAmount is not set") && st.Contains("RiskAutoFlatten is on but there is no floor"));
        Check("the data check in use is listed per account", st.Contains("Data check: the account's own connection 'Orders+Data'") && st.Contains("OtherFeed"));
        Check("open P&L is shown", st.Contains("open P&L 0.00"));
        F1.Unrealized = double.NaN; string st2 = Plain((string)Call("BuildSelfTestText", Call("AssessReadiness"))); F1.Unrealized = 0;
        Check("unreadable open P&L is called out only where the closing balance needs it", st2.Contains("F1") && st2.Contains("open P&L UNREADABLE") && !st2.Contains("open P&L cannot be read, so the closing balance is unknown"));
        Setting("DrawdownMode", "F1", "eod"); Setting("StartingBalance", "F1", "50000"); Setting("DrawdownAmount", "F1", "2000"); F1.Unrealized = double.NaN;
        string st3 = Plain((string)Call("BuildSelfTestText", Call("AssessReadiness"))); F1.Unrealized = 0;
        Check("an end-of-day account whose open P&L is unreadable is NOT ARMED for that reason", st3.Contains("open P&L cannot be read, so the closing balance is unknown"));
        Setting("DrawdownMode", "F1", "manual");
        string line = Plain((string)Call("AutoFlattenLine"));
        Check("/status carries the one-line summary", line.StartsWith("Auto-flatten: ") && line.Contains("ARMED for") && line.Contains("NOT ARMED for") && Plain((string)Call("BuildStatusReport")).Contains("Auto-flatten: "));
        Wait((Task)Call("HandleUpdateAsync", Update("/selftest"))); d = Drain();
        Check("/selftest replies", Any(d, "SELF-TEST"));
        Set("readinessChecked", false); Set("startedUtc", DateTime.UtcNow.AddSeconds(-5)); Call("CheckReadinessOnce");
        Check("not before 20 s after startup", Drain().Count == 0);
        Set("startedUtc", DateTime.UtcNow.AddSeconds(-60)); Call("CheckReadinessOnce"); d = Drain();
        Check("after that, once: LOUD when auto-flatten is on and cannot act", d.Count == 1 && Any(d, "SELF-TEST") && d[0].Priority && !d[0].Silent);
        Call("CheckReadinessOnce"); Check("...and only once", Drain().Count == 0);
        Set("cfgRiskAutoFlatten", false); Set("readinessChecked", false);
        Call("CheckReadinessOnce"); d = Drain();
        Check("with auto-flatten off the same report is quiet or routine", d.Count == 1 && !d[0].Priority);
        Set("cfgDrawdownAmount", 2000.0); F2.NetLiq = 50000;
        lock (Get("riskLock")) { var sa = (IDictionary)Get("accountSettings"); sa.Remove("drawdownmode|G1"); sa.Remove("startingbalance|G1"); sa.Remove("riskfloor|LEAD"); sa.Remove("riskfloor|F1"); sa.Remove("drawdownmode|F1"); sa.Remove("startingbalance|F1"); sa.Remove("drawdownamount|F1"); }

        Console.WriteLine("\n--- 6. the risk log: what the check saw is recorded, and replaying it gives the same decisions");
        Pos(G1, MarketPosition.Long, 1); Setting("RiskFloor", "G1", "48000"); Set("cfgUnknownFlattenSec", 120);
        foreach (string a in new[] { "LEAD", "F1", "F2", "G1" }) ((IDictionary)Get("accountLocks")).Remove(a);
        lock (sent) sent.Clear(); Drain();
        DateTime t0 = new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc);
        connBad.PriceStatus = ConnectionStatus.Connected; Call("RiskTick", t0);
        connBad.PriceStatus = ConnectionStatus.ConnectionLost;
        foreach (int sec in new[] { 10, 40, 70, 130 }) Call("RiskTick", t0.AddSeconds(sec));
        d = Drain(); settle();
        Check("live: three RISK UNKNOWN messages (at 10 s, and a reminder each minute) then the account is flattened and locked", d.Count(m => m.Text.Contains("RISK UNKNOWN") && m.Text.Contains("G1 holds")) == 3 && G1.FlattenCount == 1 && LockOf("G1") != null && Any(d, "risk data unavailable for 120s"));
        Check("the backup channel got the first UNKNOWN and the lock, not the reminders", sentWith("RISK UNKNOWN: account G1") == 1 && sentWith("LOCKED: account G1 was flattened and locked") == 1 && sent.Count == 2);
        Check("...and what it was sent has no amounts, positions or reasons", noAmounts() && !sent.Any(x => x[1].Contains("120s") || x[1].Contains("data connection")));
        string[] rows = System.IO.File.ReadAllLines(Dir + "/DirectTelegramNotifier-risk.csv");
        Check("the log has a header, the account's rules once, and readings", rows[0].StartsWith("#") && rows.Count(r => r.StartsWith("S,") && r.Contains(",G1,")) == 1 && rows.Count(r => r.StartsWith("R,") && r.Contains(",G1,")) >= 4);
        Check("a reading with the data down is recorded as unknown, with why", rows.Any(r => r.StartsWith("R,2026-10-30T15:00:10.000,G1,") && r.Contains("data connection is down")));
        List<string> replay = NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifierCore.ReplayRiskLog(rows.Where(r => r.Contains(",G1,")));
        Check("replay: Risk UNKNOWN at 15:00:10, reminders at 15:01:10 and 15:02:10", replay.Any(r => r.StartsWith("2026-10-30 15:00:10 G1 Unknown loud")) && replay.Any(r => r.StartsWith("2026-10-30 15:01:10 G1 Unknown loud A=60.00")) && replay.Any(r => r.StartsWith("2026-10-30 15:02:10 G1 Unknown loud A=120.00")));
        Check("replay: the flatten fires at 15:02:10 with the same reason the live check gave", replay.Any(r => r.StartsWith("2026-10-30 15:02:10 G1 FIRE risk data unavailable for 120s")));
        Check("live and replay agree on how many UNKNOWN alerts there were", replay.Count(r => r.Contains(" Unknown loud")) == 3);
        long size = new System.IO.FileInfo(Dir + "/DirectTelegramNotifier-risk.csv").Length;
        Call("RiskTick", t0.AddSeconds(131)); Check("the reading after the lock differs (the account is locked now), so it is recorded", new System.IO.FileInfo(Dir + "/DirectTelegramNotifier-risk.csv").Length > size);
        size = new System.IO.FileInfo(Dir + "/DirectTelegramNotifier-risk.csv").Length;
        Call("RiskTick", t0.AddSeconds(132)); Check("an unchanged reading one second later writes nothing", new System.IO.FileInfo(Dir + "/DirectTelegramNotifier-risk.csv").Length == size);
        Call("RiskTick", t0.AddSeconds(165)); Check("...but one is written at least every 30 s", new System.IO.FileInfo(Dir + "/DirectTelegramNotifier-risk.csv").Length > size);
        Set("cfgEnableRiskLog", false); size = new System.IO.FileInfo(Dir + "/DirectTelegramNotifier-risk.csv").Length; G1.NetLiq = 49000; Call("RiskTick", t0.AddSeconds(200));
        Check("EnableRiskLog=false stops recording", new System.IO.FileInfo(Dir + "/DirectTelegramNotifier-risk.csv").Length == size); Set("cfgEnableRiskLog", true);
        ((IDictionary)Get("accountLocks")).Remove("G1"); Pos(G1, MarketPosition.Flat, 0); G1.NetLiq = 50000; connBad.PriceStatus = ConnectionStatus.Connected;
        lock (Get("riskLock")) ((IDictionary)Get("accountSettings")).Remove("riskfloor|G1"); Set("cfgUnknownFlattenSec", 0); Call("RiskTick", DateTime.UtcNow); Drain();

        Console.WriteLine("\n--- 7. the second alert channel");
        lock (sent) sent.Clear();
        string fullText = "<b>ACCOUNT LOCKED</b>\nF1 net liq 50,123.45 &amp; co";
        Call("SendBackup", fullText, "LOCKED: account F1. Open NinjaTrader now."); settle();
        Check("by default the third-party channel gets the MINIMAL text, as plain text, to the URL", sent.Count == 1 && sent[0][0] == "https://ntfy.example/secret-topic" && sent[0][1] == "[NT8 notifier] LOCKED: account F1. Open NinjaTrader now." && sent[0][2] == "text/plain");
        Check("...with none of the full alert in it", noAmounts());
        Call("SendBackup", fullText, "LOCKED: account F1. Open NinjaTrader now."); settle();
        Check("the same text within a minute is not sent twice", sent.Count == 1);
        Set("cfgBackupDetail", "full"); lock (sent) sent.Clear();
        Call("SendBackup", fullText, "LOCKED: account F1. Open NinjaTrader now."); settle();
        Check("BackupAlertDetail=full sends the whole alert (for a service you run yourself)", sent.Count == 1 && sent[0][1] == "[NT8 notifier] ACCOUNT LOCKED\nF1 net liq 50,123.45 & co");
        Set("cfgBackupDetail", "minimal");
        Set("cfgBackupFormat", "json"); lock (sent) sent.Clear();
        Call("SendBackup", "<b>Line \"one\"</b>\nline\ttwo \\ end", "<b>Line \"one\"</b>\nline\ttwo \\ end"); settle();
        Check("json format: the same text under text, content and message, escaped", sent.Count == 1 && sent[0][2] == "application/json"
              && sent[0][1] == "{\"text\":\"[NT8 notifier] Line \\\"one\\\"\\nline\\ttwo \\\\ end\",\"content\":\"[NT8 notifier] Line \\\"one\\\"\\nline\\ttwo \\\\ end\",\"message\":\"[NT8 notifier] Line \\\"one\\\"\\nline\\ttwo \\\\ end\"}");
        Set("cfgBackupFormat", "text");

        lock (sent) sent.Clear(); Pos(L, MarketPosition.Flat, 0);
        Setting("RiskFloor", "F2", "48000");
        foreach (double nl in new[] { 49000.0, 48700.0, 48200.0, 47900.0 }) { F2.NetLiq = nl; Call("RiskTick", DateTime.UtcNow); }
        d = Drain(); settle();
        Check("floor alerts: all four reach Telegram", d.Count(m => m.Text.Contains("RISK BUFFER")) == 4);
        Check("...but only the tightest level (300) and below it reach the backup channel", sent.Count == 2 && sentWith("NEAR FLOOR: account F2") == 1 && sentWith("BELOW FLOOR: account F2") == 1);
        Check("...as short messages with no amounts", noAmounts());
        lock (Get("riskLock")) ((IDictionary)Get("accountSettings")).Remove("riskfloor|F2"); F2.NetLiq = 50000; Call("RiskTick", DateTime.UtcNow); Drain();

        lock (sent) sent.Clear();
        Call("SetLock", "F1", "test", true, 0); Pos(F1, MarketPosition.Long, 3); AgeLock("F1");
        Call("RiskTick", DateTime.UtcNow); d = Drain(); settle();
        Check("a lock being enforced reaches both channels", (Any(d, "LOCK ENFORCED") || Any(d, "RE-ENTRY CLOSED")) && (sentWith("LOCK ENFORCED: account F1") + sentWith("RE-ENTRY CLOSED: account F1")) == 1 && noAmounts());
        ((IDictionary)Get("accountLocks")).Remove("F1"); Pos(F1, MarketPosition.Flat, 0);

        lock (sent) sent.Clear(); Pos(L, MarketPosition.Flat, 0);
        Call("EnqueueQuiet", "routine fill"); Call("EnqueueText", "a problem alert"); settle();
        Check("routine messages and ordinary problem alerts do not use the backup channel", sent.Count == 0);
        Call("EnqueueCritical", "<b>CRITICAL</b> thing with 1,234.56", "CRITICAL thing"); settle(); d = Drain();
        Check("EnqueueCritical goes to Telegram and to the backup", Any(d, "1,234.56") && sentWith("CRITICAL thing") == 1 && !sent.Any(x => x[1].Contains("1,234.56")));

        Console.WriteLine("\n   - Telegram going down is reported through the second channel");
        lock (sent) sent.Clear();
        Set("lastSendOkUtc", DateTime.UtcNow.AddSeconds(-400)); Set("lastPollOkUtc", DateTime.MinValue); Set("lastProbeOkUtc", DateTime.MinValue); Set("lastFatalSendUtc", DateTime.MinValue);
        Set("lastDeliveryBackupCheckUtc", DateTime.MinValue); Call("CheckDeliveryBackup", DateTime.UtcNow); settle();
        Check("no successful Telegram call for 6+ minutes: the backup channel says alerts are not getting through", sent.Count == 1 && sent[0][1].Contains("Telegram alerts are NOT getting through") && sent[0][1].Contains("no successful Telegram call"));
        Set("lastDeliveryBackupCheckUtc", DateTime.MinValue); Call("CheckDeliveryBackup", DateTime.UtcNow); settle();
        Check("...once, not every 30 s", sent.Count == 1);
        Set("lastDeliveryBackupCheckUtc", DateTime.UtcNow.AddSeconds(-5)); Call("CheckDeliveryBackup", DateTime.UtcNow); settle();
        Check("...and the check itself is throttled to every 30 s", sent.Count == 1);
        Set("lastSendOkUtc", DateTime.UtcNow); Set("lastDeliveryBackupCheckUtc", DateTime.MinValue); Call("CheckDeliveryBackup", DateTime.UtcNow); settle();
        Check("when sends work again it says so", sent.Count == 2 && sent[1][1].Contains("getting through again"));
        Drain();

        Console.WriteLine("\n   - failures, tests and configuration");
        int attempts = 0;
        Set("backupPost", (Func<string, string, string, Task>)delegate(string u, string b, string c) { attempts++; if (attempts == 1) throw new Exception("network down " + u); lock (sent) sent.Add(new[] { u, b, c }); return Task.FromResult(0); });
        lock (sent) sent.Clear();
        Call("SendBackup", "<b>RETRY</b> me", "<b>RETRY</b> me"); Thread.Sleep(3700);
        Check("a failed post is retried once and then arrives", attempts == 2 && sent.Count == 1);
        Set("backupPost", (Func<string, string, string, Task>)delegate(string u, string b, string c) { throw new Exception("boom " + u); });
        string failed = ((Task<string>)Call("TestBackupAsync")).Result;
        Check("/testbackup reports a failure without leaking the URL", failed.Contains("FAILED") && !failed.Contains("secret-topic"));
        Set("backupPost", recorder); lock (sent) sent.Clear();
        string worked = ((Task<string>)Call("TestBackupAsync")).Result;
        Check("/testbackup reports success and really posts", worked.Contains("Test sent") && sent.Count == 1 && sent[0][1].Contains("Test: the backup alert channel works"));
        Wait((Task)Call("HandleUpdateAsync", Update("/testbackup"))); settle(); settle(); d = Drain();
        Check("/testbackup works as a command", Any(d, "Test sent"));
        Check("the URL is redacted from anything printed or logged", !((string)Call("Redact", "posting to https://ntfy.example/secret-topic failed")).Contains("secret-topic"));
        Set("cfgBackupUrl", ""); lock (sent) sent.Clear();
        Call("SendBackup", "<b>NOTHING</b> to send", "<b>NOTHING</b> to send"); settle();
        Check("with no URL nothing is sent", sent.Count == 0);
        Check("/testbackup says so when there is no URL", (((Task<string>)Call("TestBackupAsync")).Result).Contains("BackupAlertUrl is not set"));

        System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.config",
            "BotToken=123456:ABCdefGHIjklMNOpqrSTUvwxYZ123456789\nAuthorizedChatID=42\nAccountName=LEAD,F1\nBackupAlertUrl=ftp://nope\nBackupAlertFormat=xml\nEnableRiskLog=false\n");
        Call("LoadConfiguration");
        var w = (List<string>)Get("configWarnings");
        Check("a non-http backup URL is rejected and the channel stays off", w.Any(x => x.Contains("BackupAlertUrl must start with https://")) && (string)Get("cfgBackupUrl") == "");
        Check("an unknown format falls back to text", w.Any(x => x.Contains("BackupAlertFormat 'xml'")) && (string)Get("cfgBackupFormat") == "text");
        Check("EnableRiskLog=false is read", (bool)Get("cfgEnableRiskLog") == false);
        System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.config",
            "BotToken=123456:ABCdefGHIjklMNOpqrSTUvwxYZ123456789\nAuthorizedChatID=42\nAccountName=LEAD,F1\nBackupAlertUrl=http://192.168.1.5:8080/alerts\nBackupAlertFormat=JSON\n");
        Call("LoadConfiguration"); w = (List<string>)Get("configWarnings");
        Check("an http URL is allowed with a warning about plain text, and the format is case-insensitive", w.Any(x => x.Contains("unencrypted")) && (string)Get("cfgBackupUrl") == "http://192.168.1.5:8080/alerts" && (string)Get("cfgBackupFormat") == "json" && (bool)Get("cfgEnableRiskLog") == true);

        Console.WriteLine("\n--- 8. STILL EXPOSED repeats until flat or unlocked, on both channels");
        Set("cfgBackupUrl", "https://ntfy.example/secret-topic"); Set("cfgBackupFormat", "text"); Set("cfgBackupDetail", "minimal"); Set("backupPost", recorder);
        Set("exposureRepeatMs", 400); Set("autoFlattenWatchMs", 1200);
        foreach (string a in new[] { "LEAD", "F1", "F2", "G1" }) ((IDictionary)Get("accountLocks")).Remove(a);
        Pos(F1, MarketPosition.Long, 2); lock (sent) sent.Clear(); Drain();
        Call("LockAndFlatten", F1, "test breach"); Drain();
        Thread.Sleep(3600); d = Drain(); settle();
        int still = d.Count(m => m.Text.Contains("STILL EXPOSED"));
        Check("a locked account that stays exposed: the first alert at ~1.2 s and then a repeat every 0.4 s (" + still + " in 3.6 s)", still >= 4);
        Check("every one is loud", d.Where(m => m.Text.Contains("STILL EXPOSED")).All(m => m.Priority && !m.Silent));
        Check("...and every one reached the backup channel too, each with its own elapsed time", sentWith("STILL EXPOSED (") >= 4 && sent.Select(x => x[1]).Distinct().Count() == sent.Count && noAmounts());
        Pos(F1, MarketPosition.Flat, 0); Thread.Sleep(1200); d = Drain();
        Check("once it is flat: a quiet FLAT CONFIRMED (late), and then it stops", Any(d, "FLAT CONFIRMED") && Any(d, "late") && !d.First(m => m.Text.Contains("FLAT CONFIRMED")).Priority);
        Thread.Sleep(1200); Check("...no more STILL EXPOSED after that", Drain().Count(m => m.Text.Contains("STILL EXPOSED")) == 0);

        ((IDictionary)Get("accountLocks")).Remove("F1"); Pos(F1, MarketPosition.Long, 2); Drain();
        Call("LockAndFlatten", F1, "test breach"); Drain();
        Thread.Sleep(2000); Drain();
        ((IDictionary)Get("accountLocks")).Remove("F1");                  // you unlocked it yourself
        Thread.Sleep(1000); d = Drain();
        Check("unlocking ends the repeats, with a quiet note", Any(d, "No longer watching") && !Any(d, "STILL EXPOSED"));
        Thread.Sleep(1000); Check("...and nothing more", Drain().Count == 0);
        Pos(F1, MarketPosition.Flat, 0);

        Set("autoFlattenWatchMs", 0); Set("flattenCheckMaxMs", 1000); Pos(F2, MarketPosition.Long, 1); lock (sent) sent.Clear();
        Wait((Task)Call("ExecuteFlattenAsync", "F2")); Drain();
        Thread.Sleep(1500); d = Drain();
        Check("a /flatten that did not complete repeats too", d.Count(m => m.Text.Contains("STILL EXPOSED")) >= 2 && sentWith("STILL EXPOSED (") >= 2);
        Pos(F2, MarketPosition.Flat, 0); Thread.Sleep(1000); d = Drain();
        Check("...until it is flat", Any(d, "FLAT CONFIRMED"));
        ((IDictionary)Get("accountLocks")).Remove("F2"); Set("flattenCheckMaxMs", 1500);

        Console.WriteLine("\n--- 9. open P&L reading exactly 0.00 while holding a position");
        DateTime p0 = new DateTime(2026, 10, 30, 16, 0, 0, DateTimeKind.Utc);
        Pos(F2, MarketPosition.Long, 1); F2.Unrealized = 0; F2.NetLiq = 50000; lock (sent) sent.Clear(); Drain();
        string hint = Plain((string)Call("BuildSelfTestText", Call("AssessReadiness")));
        Check("the self-test already hints when a held position shows exactly 0.00", hint.Contains("Holding a position and open P&L reads exactly 0.00"));
        double[] walk = { 50000, 50002, 50006, 50010 };
        for (int i = 0; i < walk.Length; i++) { F2.NetLiq = walk[i]; Call("RiskTick", p0.AddSeconds(10 * i)); }
        d = Drain();
        Check("net liquidation moved by 10 over 30 s with open P&L stuck at 0.00: OPEN P&L LOOKS WRONG, loud", d.Count(m => m.Text.Contains("OPEN P&L LOOKS WRONG")) == 1 && d.First(m => m.Text.Contains("OPEN P&L LOOKS WRONG")).Priority && Any(d, "moved by 10.00") && Any(d, "F2"));
        Check("...and it is a Telegram alert, not a backup one", sent.Count == 0);
        F2.NetLiq = 50020; Call("RiskTick", p0.AddSeconds(40)); Check("...once, not every second", Drain().Count(m => m.Text.Contains("OPEN P&L LOOKS WRONG")) == 0);
        string suspect = Plain((string)Call("BuildSelfTestText", Call("AssessReadiness")));
        Check("/selftest says so while it lasts", suspect.Contains("Open P&L has read exactly 0.00 while net liquidation moved by"));
        F2.Unrealized = 20; F2.NetLiq = 50020; Call("RiskTick", p0.AddSeconds(50));
        Check("a real open P&L clears it", !Plain((string)Call("BuildSelfTestText", Call("AssessReadiness"))).Contains("Open P&L has read exactly 0.00 while"));
        Pos(F2, MarketPosition.Flat, 0); F2.Unrealized = 0; F2.NetLiq = 50000; Drain();

        System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.config",
            "BotToken=123456:ABCdefGHIjklMNOpqrSTUvwxYZ123456789\nAuthorizedChatID=42\nAccountName=LEAD,F1\nBackupAlertUrl=https://ntfy.example/t\nBackupAlertDetail=Everything\n");
        Call("LoadConfiguration"); w = (List<string>)Get("configWarnings");
        Check("an unknown BackupAlertDetail falls back to minimal, with a warning", w.Any(x => x.Contains("BackupAlertDetail 'everything'")) && (string)Get("cfgBackupDetail") == "minimal");
        System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.config",
            "BotToken=123456:ABCdefGHIjklMNOpqrSTUvwxYZ123456789\nAuthorizedChatID=42\nAccountName=LEAD,F1\nBackupAlertUrl=https://ntfy.example/t\nBackupAlertDetail=FULL\n");
        Call("LoadConfiguration"); Check("BackupAlertDetail=FULL is read, case-insensitively", (string)Get("cfgBackupDetail") == "full");
        Check("the default is minimal", (string)Activator.CreateInstance(T, true).GetType().GetField("cfgBackupDetail", B).GetValue(Activator.CreateInstance(T, true)) == "minimal");

        Console.WriteLine("\n" + passes + " passed, " + fails + " failed");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
