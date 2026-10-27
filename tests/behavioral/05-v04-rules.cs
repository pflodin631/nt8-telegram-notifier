using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Net; using System.Net.Http; using System.Reflection; using System.Text.RegularExpressions; using System.Threading; using System.Threading.Tasks;
using NinjaTrader.Cbi;
class H7
{
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dtn-h7");
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
        Set("consistencyAlerts", Call("ParseDollarList", "75,90"));
        Set("dailyLossAlerts", Call("ParseDollarList", "50,75,90"));
        Set("inactivityAlerts", Call("ParseDollarList", "2,1"));
        Connection.Connections.Clear();
        var connOk = new Connection { Status = ConnectionStatus.Connected, PriceStatus = ConnectionStatus.Connected, Options = new ConnectionOptions { Name = "Orders+Data" } };
        var connBad = new Connection { Status = ConnectionStatus.Connected, PriceStatus = ConnectionStatus.Connected, Options = new ConnectionOptions { Name = "OtherFeed" } };
        Connection.Connections.Add(connOk); Connection.Connections.Add(connBad);
        var L = new Account { Name = "LEAD", NetLiq = 50000 }; var F1 = new Account { Name = "F1", NetLiq = 50000 }; var F2 = new Account { Name = "F2", NetLiq = 50000 };
        var tracked = (IList)Get("trackedAccounts"); tracked.Add(L); tracked.Add(F1); tracked.Add(F2);
        List<Msg> d; string token;
        Func<string, string> TokenOf = x => x;

        Console.WriteLine("\n--- 1. heartbeat: a refused send is not delivery, even while polling works");
        Set("cfgHeartbeatUrl", "https://hc-ping.example/abc");
        Set("lastSendOkUtc", DateTime.UtcNow.AddSeconds(-400)); Set("lastPollOkUtc", DateTime.UtcNow); Set("lastProbeOkUtc", DateTime.UtcNow);
        Check("polling and getMe fresh: heartbeat is fine", Call("DeliveryProblem", DateTime.UtcNow) == null);
        var r403 = new HttpResponseMessage(HttpStatusCode.Forbidden);
        object outcome = Call("InterpretResponse", r403, "{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot was blocked by the user\"}");
        Check("a 403 is a permanent failure", outcome.ToString() != null && (int)outcome.GetType().GetField("Result").GetValue(outcome) == 2);
        string prob = (string)Call("DeliveryProblem", DateTime.UtcNow);
        Check("...and the heartbeat is now held although polling still works (" + prob + ")", prob != null && prob.Contains("refuses sends") && prob.Contains("403"));
        Set("lastHeartbeatPingUtc", DateTime.MinValue); Call("PingExternalHeartbeat");
        Check("the ping is not sent", (DateTime)Get("lastHeartbeatPingUtc") == DateTime.MinValue);
        d = Drain();
        Check("a silent delivery test goes out so a fix is noticed", d.Count == 1 && d[0].Text.Contains("Delivery check") && d[0].Silent);
        Call("DeliveryProblem", DateTime.UtcNow); Check("...but not again within 5 minutes", Drain().Count == 0);
        Set("lastSendOkUtc", DateTime.UtcNow);
        Check("once a send succeeds, the heartbeat resumes", Call("DeliveryProblem", DateTime.UtcNow) == null);
        Set("lastFatalSendUtc", DateTime.MinValue); Set("lastSendOkUtc", DateTime.UtcNow.AddSeconds(-400));
        Call("InterpretResponse", new HttpResponseMessage(HttpStatusCode.BadRequest), "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is too long\"}");
        Check("a message-specific 400 is not treated as a dead path", (DateTime)Get("lastFatalSendUtc") == DateTime.MinValue);
        Call("InterpretResponse", new HttpResponseMessage(HttpStatusCode.BadRequest), "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}");
        Check("'chat not found' is", (DateTime)Get("lastFatalSendUtc") > DateTime.MinValue);
        Set("lastFatalSendUtc", DateTime.MinValue); Set("cfgHeartbeatUrl", ""); Drain();

        Console.WriteLine("\n--- 2. Risk UNKNOWN is scoped to the account's own data connection");
        L.Connection = connOk; F1.Connection = connBad;
        Setting("RiskFloor", "LEAD", "48000"); Setting("RiskFloor", "F1", "48000");
        Pos(L, MarketPosition.Long, 1); Pos(F1, MarketPosition.Long, 1);
        connBad.PriceStatus = ConnectionStatus.ConnectionLost;
        DateTime t1 = DateTime.UtcNow;
        Call("RiskTick", t1); d = Drain();
        Check("F1 (its connection lost data) is UNKNOWN", Any(d, "RISK UNKNOWN") && Any(d, "F1 holds a position") && Any(d, "this account's data connection is down"));
        Check("LEAD (different connection, data fine) is NOT", !Any(d, "LEAD holds a position"));
        Call("RiskTick", t1.AddSeconds(30)); Check("no repeat after 30s", Drain().Count == 0);
        Call("RiskTick", t1.AddSeconds(61)); d = Drain();
        Check("reminder after a minute, with how long it has lasted", Any(d, "RISK UNKNOWN") && Any(d, "still 1m"));
        Call("RiskTick", t1.AddSeconds(125)); d = Drain();
        Check("and again after another minute", Any(d, "still 2m"));
        Check("with UnknownFlattenSeconds=0 it never flattens", F1.FlattenCount == 0 && LockOf("F1") == null);
        Set("cfgUnknownFlattenSec", 150); Set("cfgRiskAutoFlatten", false);
        Call("RiskTick", t1.AddSeconds(140)); d = Drain();
        Check("before the limit nothing is flattened", F1.FlattenCount == 0);
        Call("RiskTick", t1.AddSeconds(151)); d = Drain();
        Check("after UnknownFlattenSeconds the account is flattened and locked", F1.FlattenCount == 1 && LockOf("F1") != null && Any(d, "ACCOUNT LOCKED") && Any(d, "risk data unavailable for 150s"));
        Check("the reminder says it will flatten when UnknownFlattenSeconds is set", ((string)Call("MsgRiskUnknown", "F1", "x", TimeSpan.Zero, 150)).Contains("flattened and locked if this lasts 150s") && !((string)Call("MsgRiskUnknown", "F1", "x", TimeSpan.Zero, 0)).Contains("flattened"));
        ((IDictionary)Get("accountLocks")).Remove("F1"); Pos(F1, MarketPosition.Flat, 0); Pos(L, MarketPosition.Flat, 0);
        connBad.PriceStatus = ConnectionStatus.Connected; Call("RiskTick", DateTime.UtcNow); Drain();
        Set("cfgUnknownFlattenSec", 0); L.Connection = null; F1.Connection = null;
        lock (Get("riskLock")) { var s0 = (IDictionary)Get("accountSettings"); s0.Remove("riskfloor|LEAD"); s0.Remove("riskfloor|F1"); }
        Pos(L, MarketPosition.Long, 1); Setting("RiskFloor", "LEAD", "48000"); connOk.PriceStatus = ConnectionStatus.ConnectionLost;
        Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("an account whose connection is unknown falls back to 'any data connection down'", Any(d, "RISK UNKNOWN") && Any(d, "LEAD holds a position"));
        connOk.PriceStatus = ConnectionStatus.Connected; Pos(L, MarketPosition.Flat, 0); Call("RiskTick", DateTime.UtcNow); Drain();
        lock (Get("riskLock")) ((IDictionary)Get("accountSettings")).Remove("riskfloor|LEAD");

        Console.WriteLine("\n--- 3. end-of-day high-water mark uses the closing BALANCE, not net liquidation with open profit");
        Setting("DrawdownMode", "F2", "eod"); Setting("StartingBalance", "F2", "50000"); Setting("DrawdownAmount", "F2", "2000");
        F2.NetLiq = 50600; F2.Unrealized = 400;                         // 400 of open profit; balance 50,200
        Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("account already above StartingBalance: warns the floor may be too low", Any(d, "HIGH-WATER STARTED AT STARTING BALANCE") && Any(d, "50,200.00") && Any(d, "/risk seed F2"));
        SetF(Rs("F2"), "Session", "2000-01-01"); Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("rollover uses the 50,200 closing balance: high-water 50,200, floor 48,200", (double)F(Rs("F2"), "HighWater") == 50200.0 && (double)Call("FloorFor", "F2") == 48200.0);
        Check("(net liquidation would have given 48,600)", (double)Call("FloorFor", "F2") != 48600.0);
        Setting("HighWaterBasis", "F1", "netliq"); Setting("DrawdownMode", "F1", "eod"); Setting("StartingBalance", "F1", "50000"); Setting("DrawdownAmount", "F1", "2000");
        F1.NetLiq = 50600; F1.Unrealized = 400; Call("RiskTick", DateTime.UtcNow); Drain();
        SetF(Rs("F1"), "Session", "2000-01-01"); Call("RiskTick", DateTime.UtcNow); Drain();
        Check("HighWaterBasis=netliq per account follows net liquidation: floor 48,600", (double)Call("FloorFor", "F1") == 48600.0);
        Setting("DrawdownMode", "F1", "off"); lock (Get("riskLock")) ((IDictionary)Get("accountSettings")).Remove("highwaterbasis|F1");

        Console.WriteLine("\n--- 4. an unmonitored gap across the boundary does not guess");
        SetF(Rs("F2"), "Session", "2000-01-01"); SetF(Rs("F2"), "LastSeenUtc", DateTime.UtcNow.AddHours(-3)); SetF(Rs("F2"), "LastBalance", 50900.0);
        F2.NetLiq = 50200; F2.Unrealized = 0;                          // the balance moved 700 while nobody was watching
        double hwBefore = (double)F(Rs("F2"), "HighWater");
        Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("CLOSING BALANCE UNCERTAIN, and the high-water mark is left alone", Any(d, "CLOSING BALANCE UNCERTAIN") && (double)F(Rs("F2"), "HighWater") == hwBefore && Any(d, "/risk seed F2"));
        SetF(Rs("F2"), "Session", "2000-01-01"); SetF(Rs("F2"), "LastSeenUtc", DateTime.UtcNow.AddHours(-3)); SetF(Rs("F2"), "LastBalance", 51000.0);
        F2.NetLiq = 51000.50; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("a gap with the balance unchanged is trusted: NinjaTrader just restarted overnight", !Any(d, "UNCERTAIN") && (double)F(Rs("F2"), "HighWater") == 51000.0);
        Check("floor follows: 51,000 - 2,000 = 49,000", (double)Call("FloorFor", "F2") == 49000.0);

        Console.WriteLine("\n--- 5. /risk seed: set the real high-water mark, with a button");
        F2.NetLiq = 51200;
        object sr = Call("HandleRiskCommand", "seed F2 51500"); d = Drain();
        Check("asks first, and shows the resulting floor and buffer", sr == null && Any(d, "Confirm high-water 51,500.00?") && Any(d, "49,000.00 \u2192 49,500.00") && Any(d, "1,700.00 buffer"));
        Check("nothing changed yet", (double)F(Rs("F2"), "HighWater") == 51000.0);
        token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + token))); d = Drain();
        Check("the button sets it: high-water 51,500, floor 49,500", (double)F(Rs("F2"), "HighWater") == 51500.0 && (double)Call("FloorFor", "F2") == 49500.0 && Any(d, "high-water 51,500.00 set"));
        Call("SaveRiskState");
        Check("it is saved", System.IO.File.ReadAllText(Dir + "/DirectTelegramNotifier.state").Contains("hw|F2=51500"));
        Call("HandleRiskCommand", "seed F2 99999"); d = Drain();
        Check("a typo above net liquidation is flagged in the prompt", Any(d, "at or above the current net liquidation"));
        Check("seeding a manual-mode account is explained", ((string)Call("HandleRiskCommand", "seed LEAD 51000")).Contains("no trailing high-water mark"));
        Check("an unknown account is reported", ((string)Call("HandleRiskCommand", "seed zzz 51000")).Contains("No tracked account"));
        Check("a bad value shows usage", ((string)Call("HandleRiskCommand", "seed F2 abc")).Contains("Usage"));
        Call("HandleRiskCommand", "seed F2 bestday 800"); d = Drain();
        token = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + token))); Drain();
        Check("/risk seed F2 bestday 800 sets the best day", (double)F(Rs("F2"), "BestDay") == 800.0);

        Console.WriteLine("\n--- 6. several confirmations can be pending at once");
        Set("cfgFlattenConfirm", true); Pos(L, MarketPosition.Long, 2);
        Call("RequestActionConfirmation", "flatten", "LEAD"); d = Drain();
        string flatToken = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        L.NetLiq = 50296.10; Set("cfgFloorJumpConfirm", 1000.0);
        object fr = Call("HandleFloorCommand", "58250"); d = Drain();
        string floorToken = Regex.Match(d.First(m => m.Markup != null).Markup, "ok:([0-9a-f]+)").Groups[1].Value;
        Check("a /floor prompt after a /flatten prompt", fr == null && flatToken != floorToken);
        int lf = L.FlattenCount;
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + flatToken))); Thread.Sleep(3000); d = Drain();
        Check("the earlier /flatten button still works (it used to be cancelled)", L.FlattenCount == lf + 1 && Any(d, "FLATTEN SENT"));
        Wait((Task)Call("HandleUpdateAsync", Button("ok:" + floorToken))); d = Drain();
        Check("and so does the /floor button", (double)Call("FloorFor", "LEAD") == 58250.0);
        Call("HandleFloorCommand", "clear"); Drain(); Set("cfgFlattenConfirm", false);
        foreach (string a in new[] { "LEAD", "F1", "F2" }) ((IDictionary)Get("accountLocks")).Remove(a);
        Pos(L, MarketPosition.Flat, 0);

        Console.WriteLine("\n--- 7. LockGroup: one locked account locks them all");
        Pos(F1, MarketPosition.Long, 1); Pos(L, MarketPosition.Long, 1);
        Call("LockAndFlatten", F1, "test breach"); Drain();
        Check("default: only the breaching account is locked", LockOf("F1") != null && LockOf("LEAD") == null && LockOf("F2") == null);
        foreach (string a in new[] { "LEAD", "F1", "F2" }) ((IDictionary)Get("accountLocks")).Remove(a);
        Set("cfgLockGroup", true); int leadFlat = L.FlattenCount;
        string groupText = Plain((string)Call("LockAndFlatten", F1, "test breach"));
        Check("LockGroup=true: every tracked account is locked and flattened", LockOf("F1") != null && LockOf("LEAD") != null && LockOf("F2") != null && L.FlattenCount == leadFlat + 1);
        Check("the alert says so", groupText.Contains("Group lock") && groupText.Contains("LEAD"));
        Set("cfgLockGroup", false); foreach (string a in new[] { "LEAD", "F1", "F2" }) ((IDictionary)Get("accountLocks")).Remove(a);
        Pos(F1, MarketPosition.Flat, 0); Pos(L, MarketPosition.Flat, 0);

        Console.WriteLine("\n--- 8. consistency rule");
        Setting("DrawdownMode", "F2", "off"); Setting("ConsistencyPercent", "F1", "30"); Setting("StartingBalance", "F1", "50000");
        F1.NetLiq = 51000; F1.Unrealized = 0; SetF(Rs("F1"), "Session", "2000-01-01"); Call("RiskTick", DateTime.UtcNow); Drain();
        Check("session baseline: 51,000 balance, 1,000 banked", (double)F(Rs("F1"), "SessionStartBalance") == 51000.0);
        F1.NetLiq = 51300; Call("RiskTick", DateTime.UtcNow); Check("today +300 of 428.57: quiet", Drain().Count == 0);
        F1.NetLiq = 51330; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("today +330 = 77% of the cap: CONSISTENCY 75%", Any(d, "CONSISTENCY 75%") && Any(d, "of 428.57 allowed") && Any(d, "more than 30%"));
        F1.NetLiq = 51395; Call("RiskTick", DateTime.UtcNow); Check("+395: CONSISTENCY 90%", Any(Drain(), "CONSISTENCY 90%"));
        F1.NetLiq = 51430; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("+430 passes the cap: CONSISTENCY CAP REACHED, and no auto-flatten while it is off", Any(d, "CONSISTENCY CAP REACHED") && LockOf("F1") == null);
        string rl = Plain((string)Call("BuildRiskLines", F1, true));
        Check("/risk shows the cap and the best day's share of total profit", rl.Contains("Consistency 30%: today +430.00 of 428.57 allowed") && rl.Contains("best day 430.00 = 30% of total profit"));
        F1.NetLiq = 51100; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("falling back under the levels sends a quiet recovery", Any(d, "CONSISTENCY BACK UNDER") && !d.First(m => m.Text.Contains("CONSISTENCY BACK")).Priority);
        Set("cfgConsistencyAutoFlatten", true); Set("cfgRiskConfirmSec", 2); DateTime tc = DateTime.UtcNow; Pos(F1, MarketPosition.Long, 1); int f1c = F1.FlattenCount;
        F1.NetLiq = 51440; Call("RiskTick", tc); Drain();
        Check("auto-flatten waits RiskConfirmSeconds", F1.FlattenCount == f1c);
        Call("RiskTick", tc.AddSeconds(2.2)); d = Drain();
        Check("then flattens and locks, naming the rule", F1.FlattenCount == f1c + 1 && Any(d, "ACCOUNT LOCKED") && Any(d, "consistency rule"));
        ((IDictionary)Get("accountLocks")).Remove("F1"); Set("cfgConsistencyAutoFlatten", false); Pos(F1, MarketPosition.Flat, 0);
        SetF(Rs("F1"), "LastBalance", 51450.0); SetF(Rs("F1"), "LastSeenUtc", DateTime.UtcNow); SetF(Rs("F1"), "Session", "2000-01-01"); F1.NetLiq = 51450;
        Call("RiskTick", DateTime.UtcNow); Drain();
        Check("at the next session the day's profit (450) becomes the best day", (double)F(Rs("F1"), "BestDay") == 450.0);
        Check("consistency measured against the profit target: 50% of a 3,000 target allows 1,500 on a day-one account",
              Math.Abs(DirectTelegramNotifierCoreCap(0, 50, 3000) - 1500) < 1e-9);
        lock (Get("riskLock")) { var s1 = (IDictionary)Get("accountSettings"); s1.Remove("consistencypercent|F1"); }
        Setting("ConsistencyPercent", "F2", "30"); lock (Get("riskLock")) { var s2 = (IDictionary)Get("accountSettings"); s2.Remove("startingbalance|F2"); }
        Check("without StartingBalance /risk says what is needed", Plain((string)Call("BuildRiskLines", F2, true)).Contains("Consistency 30% needs StartingBalance"));
        lock (Get("riskLock")) { var s3 = (IDictionary)Get("accountSettings"); s3.Remove("consistencypercent|F2"); }

        Console.WriteLine("\n--- 9. inactivity clock");
        Set("cfgInactivityDays", 7.0);
        DateTime now0 = DateTime.UtcNow;
        Action<string, double> traded = (acct, daysAgo) => { lock (Get("riskLock")) { ((IDictionary)Get("lastActivity"))[acct] = now0.AddDays(-daysAgo); ((HashSet<string>)Get("activityAssumed")).Remove(acct); } };
        Action check = () => { Set("lastInactivityCheckUtc", DateTime.MinValue); Call("CheckInactivity", DateTime.UtcNow); };
        traded("LEAD", 4); traded("F1", 4); traded("F2", 4); check(); d = Drain();
        Check("4 days since the last trade, 7-day rule: quiet", d.Count == 0);
        traded("F1", 5.1); check(); d = Drain();
        Check("1.9 days left: INACTIVITY CLOCK (loud), naming the account and days left", Any(d, "INACTIVITY CLOCK") && Any(d, "F1") && Any(d, "1.9 days left of 7 days") && d[0].Priority && !d[0].Silent);
        check(); Check("not repeated", Drain().Count == 0);
        traded("F1", 6.5); check(); d = Drain();
        Check("0.5 days left: second alert", Any(d, "0.5 days left"));
        traded("F1", 7.2); check(); d = Drain();
        Check("past the limit: INACTIVITY LIMIT REACHED", Any(d, "INACTIVITY LIMIT REACHED") && Any(d, "0.2 days past"));
        Fill(F1, true, 1, "B27_Long"); check(); d = Drain();
        Check("a fill on the account resets the clock, quietly", Any(d, "INACTIVITY CLOCK RESET") && !d.First(m => m.Text.Contains("RESET")).Priority);
        Setting("InactivityDays", "F2", "3"); traded("F2", 2.5); check(); d = Drain();
        Check("a per-account limit overrides: F2 has 0.5 of 3 days left", Any(d, "F2") && Any(d, "0.5 days left of 3 days") && !Any(d, "LEAD"));
        Setting("InactivityDays", "F2", "0"); check(); Drain();
        lock (Get("riskLock")) { ((IDictionary)Get("lastActivity")).Remove("LEAD"); }
        check(); d = Drain();
        Check("an account with no record starts a clock now and says it is assumed", Any(d, "INACTIVITY CLOCK STARTED") && Any(d, "LEAD"));
        string act = Plain((string)Call("BuildActivityReport"));
        Check("/activity marks it as assumed", act.Contains("assumed"));
        string ar = Plain((string)Call("HandleActivityCommand", "LEAD 5.5"));
        Check("/activity LEAD 5.5 sets the last trade 5.5 days ago", ar.Contains("LEAD: last trade set to 5d 12h"));
        Check("...and clears 'assumed'", !Plain((string)Call("BuildActivityReport")).Contains("assumed"));
        Drain(); check(); d = Drain(); Check("and the next check alerts (1.5 days left)", Any(d, "1.5 days left"));
        Check("a bad /activity value shows usage", ((string)Call("HandleActivityCommand", "LEAD abc")).Contains("Usage"));
        Check("/activity for an unknown account is reported", ((string)Call("HandleActivityCommand", "zzz 2")).Contains("No tracked account"));
        // a real, older fill replaces an assumed starting point
        lock (Get("riskLock")) { ((IDictionary)Get("lastActivity")).Remove("F1"); }
        check(); Drain();
        Call("NoteActivity", "F1", DateTime.UtcNow.AddDays(-3));
        Check("a real earlier trade replaces the assumed clock start", !((HashSet<string>)Get("activityAssumed")).Contains("F1") && Math.Abs((DateTime.UtcNow - (DateTime)((IDictionary)Get("lastActivity"))["F1"]).TotalDays - 3) < 0.01);
        Call("SaveRiskState"); ((IDictionary)Get("lastActivity")).Clear(); ((HashSet<string>)Get("activityAssumed")).Clear(); Call("LoadRiskState");
        Check("trade times survive a restart", ((IDictionary)Get("lastActivity")).Contains("F1") && Math.Abs((DateTime.UtcNow - (DateTime)((IDictionary)Get("lastActivity"))["F1"]).TotalDays - 3) < 0.01);
        Check("so do the other new fields", Plain((string)Call("BuildRiskReport")).Length > 0 && System.IO.File.ReadAllText(Dir + "/DirectTelegramNotifier.state").Contains("act|LEAD="));
        Check("/status shows the days left", Plain((string)Call("BuildStatusReport")).Contains("Last trade"));

        Console.WriteLine("\n--- 9b. review follow-ups: which data check is in use, open P&L, consistency cure");
        Func<string, bool> logged = needle => { lock (Get("recentLines")) return ((Queue<string>)Get("recentLines")).Any(x => x.Contains(needle)); };
        L.Connection = connOk; Call("RiskTick", DateTime.UtcNow);
        Check("the Output window names the connection when an account uses its own", logged("Risk data check for LEAD: using the account's own connection 'Orders+Data'"));
        lock (Get("recentLines")) ((Queue<string>)Get("recentLines")).Clear();
        L.Connection = null; F1.Connection = null; F2.Connection = null; Call("RiskTick", DateTime.UtcNow);
        Check("...and says ANY data connection when NinjaTrader does not say which one the account uses", logged("Risk data check for LEAD: using ANY data connection"));
        L.Connection = connOk; Call("RiskTick", DateTime.UtcNow);
        lock (Get("recentLines")) ((Queue<string>)Get("recentLines")).Clear(); Call("RiskTick", DateTime.UtcNow);
        Check("it is logged when it changes, not every second", !logged("Risk data check"));
        Check("/diag lists the check in use per account", Plain((string)Call("BuildDiagReport")).Contains("Risk data check LEAD: the account's own connection 'Orders+Data'"));
        L.Connection = null;

        Setting("DrawdownMode", "F2", "eod"); Setting("StartingBalance", "F2", "50000"); Setting("DrawdownAmount", "F2", "2000");
        F2.NetLiq = 50600; F2.Unrealized = 400; Call("RiskTick", DateTime.UtcNow); Drain();
        Check("/risk shows the open P&L the closing balance is derived from", Plain((string)Call("BuildRiskLines", F2, true)).Contains("Net liq 50,600.00 \u00B7 open P&L +400.00"));
        Setting("DrawdownMode", "F2", "off"); F2.Unrealized = 0;

        Setting("ConsistencyPercent", "F1", "30"); Setting("StartingBalance", "F1", "50000");
        SetF(Rs("F1"), "SessionStartBalance", 51000.0); SetF(Rs("F1"), "BestDay", 450.0); F1.NetLiq = 51000; F1.Unrealized = 0;
        string cureText = Plain((string)Call("BuildRiskLines", F1, true));
        Check("an earlier day already over the limit is called out: best day 450 of 1,000 total needs 500.00 more profit", cureText.Contains("best day 450.00 = 45% of total profit") && cureText.Contains("Over the limit: 500.00 more total profit"));
        F1.NetLiq = 51500;
        cureText = Plain((string)Call("BuildRiskLines", F1, true));
        Check("today's 500 becomes the best day: 500 of 1,500 total (33%) needs 166.67 more", cureText.Contains("best day 500.00 = 33% of total profit") && cureText.Contains("166.67 more total profit"));
        SetF(Rs("F1"), "SessionStartBalance", 52000.0); SetF(Rs("F1"), "BestDay", 450.0); F1.NetLiq = 52100;
        cureText = Plain((string)Call("BuildRiskLines", F1, true));
        Check("best day 450 of 2,100 total (21%): within 30%, so no warning", cureText.Contains("best day 450.00 = 21% of total profit") && !cureText.Contains("Over the limit"));
        lock (Get("riskLock")) { var s9 = (IDictionary)Get("accountSettings"); s9.Remove("consistencypercent|F1"); s9.Remove("startingbalance|F1"); }

        Console.WriteLine("\n--- 10. config");
        System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.config",
            "BotToken=123456:ABCdefGHIjklMNOpqrSTUvwxYZ123456789\nAuthorizedChatID=42\nAccountName=LEAD,F1,F2\n" +
            "HighWaterBasis=cash\nConsistencyPercent=150\nInactivityDays=-3\nInactivityDay:F1=7\nUnknownFlattenSecond=60\nConsistencyPercent:F2=30\nInactivityDays:F2=7\nHighWaterBasis:F1=netliq\n");
        Call("LoadConfiguration");
        var w = (List<string>)Get("configWarnings");
        foreach (var x in w) Console.WriteLine("      warning: " + x);
        Check("an invalid HighWaterBasis is reported", w.Any(x => x.Contains("HighWaterBasis 'cash'")));
        Check("ConsistencyPercent above 100 is reported", w.Any(x => x.Contains("ConsistencyPercent must be between 0 and 100")));
        Check("a negative InactivityDays is reported", w.Any(x => x.Contains("InactivityDays must be 0 or more")));
        Check("typos in the new keys get suggestions", w.Any(x => x.Contains("InactivityDay:F1 (did you mean InactivityDays:F1?)")) && w.Any(x => x.Contains("UnknownFlattenSecond (did you mean UnknownFlattenSeconds?)")));
        Check("valid per-account new keys are accepted silently", !w.Any(x => x.Contains("ConsistencyPercent:F2") || x.Contains("InactivityDays:F2") || x.Contains("HighWaterBasis:F1")));
        Check("InactivityAlerts defaults to 3,2,1 days", ((List<double>)Get("inactivityAlerts")).Count == 3 && ((List<double>)Get("inactivityAlerts"))[2] == 3.0);
        Check("an invalid basis falls back to balance", (string)Call("BasisFor", "LEAD") == "balance" && (string)Call("BasisFor", "F1") == "netliq");

        Console.WriteLine("\n" + passes + " passed, " + fails + " failed");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
    static double DirectTelegramNotifierCoreCap(double prior, double pct, double target) { return NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifierCore.ConsistencyCap(prior, pct, target); }
}
