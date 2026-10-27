using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Net; using System.Net.Http; using System.Reflection; using System.Text.RegularExpressions; using System.Threading; using System.Threading.Tasks;
using NinjaTrader.Cbi;
class H5
{
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dtn-h5");
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
        Set("flattenCheckMaxMs", 1500); Set("autoFlattenWatchMs", 0);     // the stand-in never clears a position: keep /flatten checks short, no follow-ups
        Set("cfgAccountName", "LEAD,F1,F2"); Set("cfgLeaderAccount", "LEAD"); Set("authorizedUserId", 42L);
        Set("startedUtc", DateTime.UtcNow.AddMinutes(-30)); Set("started", true);
        Set("eodTimeZone", TimeZoneInfo.Utc); Set("eodTimeOfDay", new TimeSpan(21, 0, 0));
        Set("riskBuffers", Call("ParseDollarList", "1500,750,300"));
        Connection.Connections.Clear();
        var conn = new Connection { Status = ConnectionStatus.Connected, PriceStatus = ConnectionStatus.Connected, Options = new ConnectionOptions { Name = "Sim" } };
        Connection.Connections.Add(conn);
        var L = new Account { Name = "LEAD", NetLiq = 50296.10 }; var F1 = new Account { Name = "F1", NetLiq = 50100 }; var F2 = new Account { Name = "F2", NetLiq = 50000 };
        var tracked = (IList)Get("trackedAccounts"); tracked.Add(L); tracked.Add(F1); tracked.Add(F2);
        List<Msg> d;

        Console.WriteLine("\n--- 1. auto-flatten latches the account; re-entries are closed at once");
        Set("cfgRiskAutoFlatten", true); Set("cfgRiskFlattenBuffer", 150.0); Set("cfgRiskConfirmSec", 2);
        Setting("RiskFloor", "F1", "50000");
        Pos(F1, MarketPosition.Long, 3); F1.NetLiq = 50100; DateTime t0 = DateTime.UtcNow;
        Call("RiskTick", t0); Call("RiskTick", t0.AddSeconds(2.1)); d = Drain();
        Check("follower inside its flatten buffer for 2s is LOCKED and flattened", F1.FlattenCount == 1 && Any(d, "ACCOUNT LOCKED") && Any(d, "F1"));
        Check("the lock alert is a loud priority alert", d.Any(m => m.Text.Contains("ACCOUNT LOCKED") && !m.Silent && m.Priority));
        Check("only F1 was touched", L.FlattenCount == 0 && F2.FlattenCount == 0);
        Pos(F1, MarketPosition.Flat, 0);
        Pos(F1, MarketPosition.Long, 3); AgeLock("F1");
        Fill(F1, true, 3, "73-007F6E72"); Thread.Sleep(300); d = Drain();             // the copier re-syncs the follower
        Check("copier re-entry on the locked follower is flattened from the execution event", F1.FlattenCount == 2 && Any(d, "LOCK ENFORCED") && Any(d, "1 time since the lock"));
        Fill(F1, true, 1, "73-007F6E73"); Thread.Sleep(300); d = Drain();
        Check("a second fill within 3s does not flatten again (no double close)", F1.FlattenCount == 2);
        AgeLock("F1"); F1.Orders.Add(new Order { OrderId = "c1", Name = "Close", OrderState = OrderState.Working, Instrument = mnq });
        Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("while NinjaTrader's own Close order is working, no new flatten is sent", F1.FlattenCount == 2);
        F1.Orders.Clear(); Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("after it finishes, the still-open position is flattened again", F1.FlattenCount == 3);
        Check("repeat enforcement within a minute is not re-announced", !Any(d, "LOCK ENFORCED"));
        Pos(F1, MarketPosition.Flat, 0); F1.NetLiq = 52000; Call("RiskTick", DateTime.UtcNow); Drain();
        Check("the lock holds even after net liq recovers", LockOf("F1") != null);

        Console.WriteLine("\n--- 2. locks survive a restart within the session, and end with the session");
        Call("SaveRiskState");
        string state = System.IO.File.ReadAllText(Dir + "/DirectTelegramNotifier.state");
        Check("the lock is written to the state file", state.Contains("lock|F1=") && state.Contains("net liq"));
        ((IDictionary)Get("accountLocks")).Clear(); Call("LoadRiskState");
        Check("reloading restores this session's lock", LockOf("F1") != null);
        SetF(Rs("F1"), "Session", "2000-01-01"); SetF(LockOf("F1"), "Session", "2000-01-01");      // as after a real rollover
        Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("session rollover releases the lock and says so", LockOf("F1") == null && Any(d, "UNLOCKED") && Any(d, "the session ended"));
        System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.state", state.Replace("lock|F1=" + (string)Call("CurrentSessionKey"), "lock|F1=2000-01-01"));
        Call("LoadRiskState");
        Check("a lock from an earlier session is not restored", LockOf("F1") == null);
        Setting("RiskFloor", "F1", "");
        lock (Get("riskLock")) ((IDictionary)Get("accountSettings")).Remove("riskfloor|F1");

        Console.WriteLine("\n--- 3. /lock and /unlock");
        Pos(F2, MarketPosition.Short, 2);
        string reply = (string)Call("ExecuteLockCommand", "F2");
        Check("/lock F2 flattens F2 and locks it", F2.FlattenCount == 1 && LockOf("F2") != null && Plain(reply).Contains("LOCKED"));
        reply = (string)Call("ExecuteLockCommand", "F2");
        Check("/lock on a locked account says so and does not flatten again", F2.FlattenCount == 1 && reply.Contains("already locked"));
        reply = (string)Call("ExecuteUnlockCommand", "F2");
        Check("/unlock F2 releases it", LockOf("F2") == null && Plain(reply).Contains("UNLOCKED"));
        Check("/unlock with nothing locked says so", ((string)Call("ExecuteUnlockCommand", "F2")).Contains("Nothing was locked"));
        Pos(F2, MarketPosition.Flat, 0);

        Console.WriteLine("\n--- 4. /flatten: a short hold catches the copier flipping a follower");
        Set("cfgFlattenHoldSec", 30);
        Pos(L, MarketPosition.Long, 3); Pos(F1, MarketPosition.Long, 3);
        Wait((Task)Call("ExecuteFlattenAsync", "")); d = Drain();
        Check("FLATTEN SENT mentions the hold", Any(d, "Hold: anything new on these accounts is closed again for 30s"));
        Check("every tracked account is on hold", LockOf("LEAD") != null && LockOf("F1") != null && (bool)LockOf("F1").GetType().GetProperty("IsHold").GetValue(LockOf("F1")));
        Pos(L, MarketPosition.Flat, 0); Pos(F1, MarketPosition.Flat, 0);
        Pos(F2, MarketPosition.Short, 3); AgeLock("F2"); int f2h = F2.FlattenCount;
        Fill(F2, false, 3, "73-00ABCDEF"); Thread.Sleep(300); d = Drain();          // copier copies the leader's close onto the flat follower
        Check("follower flipped short by the copier is closed again from the execution event", F2.FlattenCount == f2h + 1 && Any(d, "RE-ENTRY CLOSED") && Any(d, "since /flatten"));
        Pos(F2, MarketPosition.Flat, 0);
        Pos(F1, MarketPosition.Long, 2); AgeLock("F1"); int f1h = F1.FlattenCount;            // books disagree with the real position
        Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("the 1s risk tick closes a re-entry the execution event missed", F1.FlattenCount == f1h + 1 && Any(d, "RE-ENTRY CLOSED"));
        Pos(F1, MarketPosition.Flat, 0);
        Fill(L, false, 3, "Close"); Thread.Sleep(200);
        Check("the flatten's own Close fills never trigger enforcement", L.FlattenCount == 1);
        foreach (string acct in new[] { "LEAD", "F1", "F2" }) if (LockOf(acct) != null) SetF(LockOf(acct), "UntilUtc", DateTime.UtcNow.AddSeconds(-1));
        Call("RiskTick", DateTime.UtcNow); Drain();
        Check("holds expire on their own", LockOf("LEAD") == null && LockOf("F1") == null && LockOf("F2") == null);
        Set("lastFlattenUtc", DateTime.UtcNow.AddSeconds(-40)); Set("reentryAlerted", false);
        Pos(L, MarketPosition.Short, 1); Fill(L, false, 1, "S12_Short"); d = Drain();
        Check("after the hold, a re-entry gets the normal RE-ENTERED AFTER FLATTEN alert pointing to /lock (no auto-close)", Any(d, "RE-ENTERED AFTER FLATTEN") && Any(d, "Send /lock") && L.FlattenCount == 1);
        Pos(L, MarketPosition.Flat, 0);

        Console.WriteLine("\n--- 5. /flatten no longer blocks other commands");
        Set("cfgFlattenConfirm", false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Wait((Task)Call("HandleUpdateAsync", Update("/flatten")));
        long ms = sw.ElapsedMilliseconds;
        Wait((Task)Call("HandleUpdateAsync", Update("/version")));
        d = Drain();
        Check("the /flatten handler returns at once (" + ms + " ms, verification runs in the background)", ms < 1000);
        Check("a command sent right after /flatten is answered before the flatten report", d.Count >= 1 && d[0].Text.Contains("v0.5.1"));
        Thread.Sleep(3200); d = Drain();
        Check("the flatten report still arrives after its 2.5s check", Any(d, "FLATTEN SENT"));
        foreach (string a in new[] { "LEAD", "F1", "F2" }) ((IDictionary)Get("accountLocks")).Remove(a);

        Console.WriteLine("\n--- 6. floor typos need confirmation");
        Set("cfgRiskAutoFlatten", true); Set("cfgFloorJumpConfirm", 1000.0);
        L.NetLiq = 50296.10; F1.NetLiq = 50314.10; F2.NetLiq = 50302.10;
        Check("a sensible floor applies at once", ((string)Call("HandleFloorCommand", "48250")).Contains("set for every tracked account"));
        object r = Call("HandleFloorCommand", "58250"); d = Drain();
        Check("/floor 58250 (typo) is not applied but asks for confirmation", r == null && (double)Call("FloorFor", "LEAD") == 48250.0 && Any(d, "Confirm floor 58,250.00?"));
        Check("the prompt explains why: above net liq, and a 10,000 jump", Any(d, "at or above the current net liquidation") && Any(d, "moves the floor by 10,000.00") && Any(d, "Auto-flatten is on"));
        string token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Wait((Task)Call("HandleUpdateAsync", Button("cancel:" + token))); Drain();
        Check("Cancel leaves the floor unchanged", (double)Call("FloorFor", "LEAD") == 48250.0);
        Call("HandleFloorCommand", "1 49900"); d = Drain();
        token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Check("a floor inside the alert levels needs confirmation", Any(d, "inside your alert levels") && Any(d, "LEAD") == false || Any(d, "F1"));
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + token))); d = Drain();
        Check("pressing the button applies it, for that account only", (double)Call("FloorFor", "F1") == 49900.0 && (double)Call("FloorFor", "LEAD") == 48250.0 && Any(d, "Floor 49,900.00 set for F1"));
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + token))); d = Drain();
        Check("a button works only once", (double)Call("FloorFor", "F1") == 49900.0);
        Call("HandleFloorCommand", "clear"); Drain();

        Console.WriteLine("\n--- 7. drawdown modes compute the floor");
        Set("cfgRiskAutoFlatten", false);
        Setting("DrawdownMode", "F2", "static"); Setting("StartingBalance", "F2", "50000"); Setting("DrawdownAmount", "F2", "2000");
        Check("static: F2 floor = 50,000 - 2,000", (double)Call("FloorFor", "F2") == 48000.0);
        Setting("DrawdownMode", "LEAD", "intraday"); Setting("StartingBalance", "LEAD", "50000"); Setting("DrawdownAmount", "LEAD", "2500");
        foreach (double v in new[] { 50000.0, 50400.0, 50250.0, 50900.0, 50100.0 }) { L.NetLiq = v; Call("RiskTick", DateTime.UtcNow); }
        Check("intraday: floor follows the live peak (50,900 - 2,500 = 48,400)", (double)Call("FloorFor", "LEAD") == 48400.0);
        Setting("DrawdownMode", "F1", "eod"); Setting("StartingBalance", "F1", "50000"); Setting("DrawdownAmount", "F1", "2000"); Setting("FloorLock", "F1", "50100");
        F1.NetLiq = 50500; Call("RiskTick", DateTime.UtcNow); Drain();
        Check("eod: starts at StartingBalance - drawdown and ignores intraday highs", (double)Call("FloorFor", "F1") == 48000.0);
        F1.NetLiq = 51000; Call("RiskTick", DateTime.UtcNow);
        SetF(Rs("F1"), "Session", "2000-01-01"); F1.NetLiq = 51010; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("eod: at the session boundary it trails the closing balance (51,000 -> floor 49,000)", (double)Call("FloorFor", "F1") == 49000.0 && Any(d, "FLOOR TRAILED") && Any(d, "48,000.00 \u2192 49,000.00"));
        F1.NetLiq = 52600; Call("RiskTick", DateTime.UtcNow); SetF(Rs("F1"), "Session", "2000-01-02"); Call("RiskTick", DateTime.UtcNow); Drain();
        Check("eod: capped at FloorLock 50,100", (double)Call("FloorFor", "F1") == 50100.0);
        F1.NetLiq = 49500; Call("RiskTick", DateTime.UtcNow); SetF(Rs("F1"), "Session", "2000-01-03"); Call("RiskTick", DateTime.UtcNow); Drain();
        Check("eod: never moves down", (double)Call("FloorFor", "F1") == 50100.0);
        Call("HandleFloorCommand", "F1 49150"); Drain();
        object[] fa = { "F1", null }; double ov = (double)T.GetMethods(B).First(m => m.Name == "FloorFor" && m.GetParameters().Length == 2).Invoke(O, fa);
        Check("a /floor setting overrides the computed floor and is labeled", ov == 49150.0 && (string)fa[1] == "/floor");
        Call("HandleFloorCommand", "F1 clear"); Drain();
        Call("SaveRiskState");
        var O2 = Activator.CreateInstance(T, true); var save = O; O = O2;
        Set("eodTimeZone", TimeZoneInfo.Utc); Set("eodTimeOfDay", new TimeSpan(21, 0, 0)); Call("LoadRiskState");
        foreach (var kv in new[] { "drawdownmode|F1=eod", "drawdownamount|F1=2000", "floorlock|F1=50100" }) { var p = kv.Split('='); ((IDictionary)Get("accountSettings"))[p[0]] = p[1]; }
        Check("trailing floor and high-water survive a restart", (double)Call("FloorFor", "F1") == 50100.0);
        O = save;
        object resetReply = Call("HandleRiskCommand", "reset F1"); d = Drain();
        Check("/risk reset asks for a button press first", resetReply == null && Any(d, "Confirm trailing reset?") && (double)Call("FloorFor", "F1") == 50100.0);
        token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + token))); Call("RiskTick", DateTime.UtcNow); Drain();
        Check("...and the button restarts the trailing floor from StartingBalance", (double)Call("FloorFor", "F1") == 48000.0);
        string risk = Plain((string)Call("BuildRiskReport"));
        Check("/risk shows each mode", risk.Contains("intraday trailing") && risk.Contains("end-of-day trailing") && risk.Contains("static drawdown") && risk.Contains("locks at 50,100.00"));
        Check("/risk shows high-water and buffer", risk.Contains("high-water 50,900.00") && risk.Contains("buffer"));
        Console.WriteLine(risk);

        Console.WriteLine("\n--- 8. stale data while holding a position: risk UNKNOWN, no automatic action");
        Set("cfgRiskAutoFlatten", true); Setting("DrawdownMode", "LEAD", "manual"); Setting("RiskFloor", "LEAD", "48000");
        Pos(L, MarketPosition.Long, 3); L.NetLiq = 48100; int lf = L.FlattenCount;
        conn.PriceStatus = ConnectionStatus.ConnectionLost;
        DateTime t1 = DateTime.UtcNow;
        Call("RiskTick", t1); Call("RiskTick", t1.AddSeconds(3)); Call("RiskTick", t1.AddSeconds(6)); d = Drain();
        Check("RISK UNKNOWN is sent once, loud", d.Count(m => m.Text.Contains("RISK UNKNOWN")) == 1 && d.First(m => m.Text.Contains("RISK UNKNOWN")).Priority);
        Check("frozen numbers never trigger auto-flatten", L.FlattenCount == lf && LockOf("LEAD") == null);
        conn.PriceStatus = ConnectionStatus.Connected; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("data back -> RISK DATA BACK", Any(d, "RISK DATA BACK"));
        Set("cfgStaleDataSeconds", 120);
        var fwType = T.GetNestedType("FeedWatch", B); var fw = Activator.CreateInstance(fwType, true); SetF(fw, "Name", "MNQ DEC26"); SetF(fw, "Stale", true);
        ((IDictionary)Get("feeds"))["MNQ DEC26"] = fw;
        Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("a stale feed for the held instrument also makes risk UNKNOWN", Any(d, "market data for MNQ DEC26 is stale"));
        Pos(L, MarketPosition.Flat, 0); ((IDictionary)Get("feeds")).Clear(); Call("RiskTick", DateTime.UtcNow); Drain();
        Pos(F2, MarketPosition.Long, 1); lock (Get("riskLock")) { var s2 = (IDictionary)Get("accountSettings"); s2.Remove("drawdownmode|F2"); s2.Remove("startingbalance|F2"); s2.Remove("drawdownamount|F2"); }
        conn.PriceStatus = ConnectionStatus.ConnectionLost; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("an account with no floor or daily limit gets no UNKNOWN alert", !d.Any(m => m.Text.Contains("RISK UNKNOWN") && m.Text.Contains("F2")));
        conn.PriceStatus = ConnectionStatus.Connected; Pos(F2, MarketPosition.Flat, 0); Call("RiskTick", DateTime.UtcNow); Drain();
        Set("cfgRiskAutoFlatten", false);

        Console.WriteLine("\n--- 9. daily loss limit");
        Setting("DailyLossLimit", "F2", "2%"); F2.NetLiq = 50000; SetF(Rs("F2"), "Session", "2000-01-01"); Call("RiskTick", DateTime.UtcNow); Drain();
        Check("session rollover sets the baseline", (double)F(Rs("F2"), "SessionStart") == 50000.0);
        Set("dailyLossAlerts", Call("ParseDollarList", "50,75,90"));
        F2.NetLiq = 49490; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("down 510 of a 2% (1,000) limit -> DAILY LOSS 50%", Any(d, "DAILY LOSS 50%") && Any(d, "limit 1,000.00"));
        F2.NetLiq = 49480; Call("RiskTick", DateTime.UtcNow); Check("same level is not repeated", Drain().Count == 0);
        F2.NetLiq = 49050; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("down 950 jumps straight to the 90% alert", Any(d, "DAILY LOSS 90%") && !Any(d, "75%"));
        Set("cfgDailyLossAutoFlatten", true); Pos(F2, MarketPosition.Long, 2); int f2c = F2.FlattenCount; DateTime t2 = DateTime.UtcNow;
        F2.NetLiq = 48990; Call("RiskTick", t2); d = Drain();
        Check("limit reached -> alert, but no flatten before RiskConfirmSeconds", Any(d, "DAILY LOSS LIMIT REACHED") && F2.FlattenCount == f2c);
        Call("RiskTick", t2.AddSeconds(2.1)); d = Drain();
        Check("after 2s at the limit the account is locked and flattened", F2.FlattenCount == f2c + 1 && Any(d, "ACCOUNT LOCKED") && Any(d, "daily loss limit"));
        ((IDictionary)Get("accountLocks")).Remove("F2"); Pos(F2, MarketPosition.Flat, 0); Set("cfgDailyLossAutoFlatten", false);
        F2.NetLiq = 49800; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("back under the lowest level -> recovered message (silent-able)", Any(d, "DAILY LOSS BACK UNDER ALERT LEVELS") && !d.First(m => m.Text.Contains("BACK UNDER")).Priority);
        Check("/status shows today's result against the limit", Plain((string)Call("BuildStatusReport")).Contains("of 1,000.00 daily loss limit"));

        Console.WriteLine("\n--- 10. priority lane, merging, rate limit");
        Drain();
        for (int i = 1; i <= 6; i++) Call("EnqueueQuiet", "fill " + i);
        Call("EnqueueText", "BELOW FLOOR");
        d = Drain();
        Check("a problem alert queued behind 6 routine messages is sent first", d[0].Text == "BELOW FLOOR");
        Check("the 6 routine messages go out as ONE merged message", d.Count == 2 && d[1].Text.Contains("fill 1") && d[1].Text.Contains("fill 6"));
        for (int i = 1; i <= 2; i++) Call("EnqueueQuiet", "solo " + i);
        Check("two routine messages are not merged", Drain().Count == 2);
        Call("EnqueueQuiet", "a"); Call("EnqueueQuiet", "b"); Call("EnqueueQuiet", "c"); Call("EnqueueSample", "sample", true); Call("EnqueueQuiet", "d");
        d = Drain();
        Check("merging stops at a /simulate sample, which keeps its order", d.Count == 3 && d[1].Text == "sample" && d[2].Text == "d");
        Call("EnqueueWithButtons", "confirm?", "{}"); Check("button prompts go in the priority lane", Drain()[0].Priority);
        Call("EnqueueQuiet", new string('x', 3000)); Call("EnqueueQuiet", new string('y', 3000)); Call("EnqueueQuiet", "z1"); Call("EnqueueQuiet", "z2");
        Check("merging respects Telegram's size limit", Drain().All(m => m.Text.Length <= 4096));
        var resp = new HttpResponseMessage((HttpStatusCode)429);
        Call("InterpretResponse", resp, "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":7}}");
        double gate = ((DateTime)Get("rateLimitedUntilUtc") - DateTime.UtcNow).TotalSeconds;
        Check("Telegram's 429 sets a gate for every message (" + gate.ToString("0.0") + "s)", gate > 6 && gate <= 7);
        Set("rateLimitedUntilUtc", DateTime.MinValue);

        Console.WriteLine("\n--- 11. external heartbeat vouches for delivery");
        Set("cfgHeartbeatUrl", "https://hc-ping.example/abc"); Set("resolvedToken", "");
        Set("lastSendOkUtc", DateTime.UtcNow.AddSeconds(-10));
        Check("recent successful send -> heartbeat OK", Call("DeliveryProblem", DateTime.UtcNow) == null);
        Set("lastSendOkUtc", DateTime.UtcNow.AddSeconds(-200)); Set("lastPollOkUtc", DateTime.MinValue); Set("lastProbeOkUtc", DateTime.MinValue);
        string prob = (string)Call("DeliveryProblem", DateTime.UtcNow);
        Check("no successful Telegram call for 3+ minutes -> heartbeat held (" + prob + ")", prob != null && prob.Contains("no successful Telegram call"));
        Set("lastHeartbeatPingUtc", DateTime.MinValue); Call("PingExternalHeartbeat");
        Check("...and the ping is not sent", (DateTime)Get("lastHeartbeatPingUtc") == DateTime.MinValue && ((string)Get("heartbeatHoldReason")).Length > 0);
        Set("lastPollOkUtc", DateTime.UtcNow);
        Call("EnqueueQuiet", "old"); var lane = (IList)Get("routineLane"); SetF(lane[0], "CreatedUtc", DateTime.UtcNow.AddMinutes(-5));
        prob = (string)Call("DeliveryProblem", DateTime.UtcNow);
        Check("alerts stuck in the queue for 5 minutes -> heartbeat held (" + prob + ")", prob != null && prob.Contains("waiting"));
        Drain(); Check("queue drained -> heartbeat OK again", Call("DeliveryProblem", DateTime.UtcNow) == null);
        string diag = Plain((string)Call("BuildDiagReport"));
        Check("/diag shows the heartbeat state", diag.Contains("External heartbeat:") && diag.Contains("paused (no successful Telegram call"));
        
        Set("cfgHeartbeatUrl", "");

        Console.WriteLine("\n--- 12. config: misspelled keys, schedules, stuck states, leader");
        System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.config",
            "BotToken=123456:ABCdefGHIjklMNOpqrSTUvwxYZ123456789\nAuthorizedChatID=42\nAccountName=LEAD,F1,F2\n" +
            "RiskAutoFlaten=true\nStartingBalnce:F1=50000\nDrawdownMode=trailing\nStaleDataSchedule=custom\nStaleDataQuietWindows=Mon 18:00-17:00\n" +
            "StuckOrderStates=Submitted,Pending\nFillGroupMs=100\nRiskFloor:F2=48000\nDailyLossLimit:F1=2%\n");
        Call("LoadConfiguration");
        var w = (List<string>)Get("configWarnings");
        foreach (var x in w) Console.WriteLine("      warning: " + x);
        Check("misspelled RiskAutoFlaten is reported with a suggestion", w.Any(x => x.Contains("RiskAutoFlaten (did you mean RiskAutoFlatten?)")));
        Check("misspelled per-account key is reported", w.Any(x => x.Contains("StartingBalnce:F1 (did you mean StartingBalance:F1?)")));
        Check("unknown DrawdownMode is reported", w.Any(x => x.Contains("DrawdownMode 'trailing'")));
        Check("a bad quiet window is reported and cme is used instead", w.Any(x => x.Contains("crosses midnight")) && ((IList)Get("staleQuietWindows")).Count == 4);
        Check("an unknown order state is reported", w.Any(x => x.Contains("'Pending' is not an order state")));
        Check("valid per-account keys are accepted silently", !w.Any(x => x.Contains("RiskFloor:F2") || x.Contains("DailyLossLimit:F1")));
        Check("FillGroupMs is clamped to at least 500", (int)Get("cfgFillGroupMs") == 500);
        Check("per-account floor from the config is used", (double)Call("FloorFor", "F2") == 48000.0);
        Set("cfgStaleDataSchedule", "custom"); Set("cfgStaleDataQuietWindows", "Daily 00:00-08:00"); Set("cfgStaleDataTimeZone", "UTC");
        Call("ApplyStaleSchedule", new List<string>());
        DateTime day = DateTime.UtcNow.Date;
        Check("custom quiet window: 07:00 UTC is quiet, 09:00 is expected to tick", !(bool)Call("IsMarketOpen", day.AddHours(7)) && (bool)Call("IsMarketOpen", day.AddHours(9)));
        reply = Plain((string)Call("HandleLeaderCommand", "F1"));
        Check("/leader F1 makes F1 the leader", (string)Call("ResolveLeaderName") == "F1" && reply.Contains("Leader is now F1"));
        Check("the leader choice is saved", System.IO.File.ReadAllText(Dir + "/DirectTelegramNotifier.state").Contains("leader=F1"));
        Call("HandleLeaderCommand", "clear");
        Check("/leader clear goes back to the config file", (string)Call("ResolveLeaderName") == "LEAD");

        Console.WriteLine("\n" + passes + " passed, " + fails + " failed");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
