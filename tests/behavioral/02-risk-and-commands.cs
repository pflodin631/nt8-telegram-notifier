using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Reflection; using System.Text.RegularExpressions; using System.Threading;
using NinjaTrader.Cbi;
class H3
{
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dtn-h3");
    static object O; static Type T;
    static BindingFlags B = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object Call(string name, params object[] args)
    {
        foreach (var m in T.GetMethods(B)) if (m.Name == name && m.GetParameters().Length == args.Length) return m.Invoke(m.IsStatic ? null : O, args);
        throw new Exception("no method " + name);
    }
    static void Set(string f, object v) { var fi = T.GetField(f, B); if (fi == null) throw new Exception("no field " + f); fi.SetValue(O, v); }
    static object Get(string f) { return T.GetField(f, B).GetValue(O); }
    static string Plain(string s) { return Regex.Replace(s, "<[^>]+>", "").Replace("&amp;", "&"); }
    static List<string> Drain()
    {
        var res = new List<string>();
        while (true) { object m = Call("PeekNextMessage"); if (m == null) break; Call("RemoveMessage", m); res.Add(Plain((string)m.GetType().GetField("Text").GetValue(m))); }
        return res;
    }
    static int fails = 0;
    static void Check(string what, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; }
    static Instrument mnq = new Instrument { FullName = "MNQ DEC26", MasterInstrument = new MasterInstrument { PointValue = 2.0, TickSize = 0.25 } };
    static Account Acct(string name, double netLiq) { return new Account { Name = name, NetLiq = netLiq }; }
    static void Pos(Account a, MarketPosition mp, int q) { a.Positions.Clear(); if (q > 0) a.Positions.Add(new Position { Instrument = mnq, MarketPosition = mp, Quantity = q, AveragePrice = 29400 }); }
    static Order Ord(Account a, string id, string name, OrderType t, OrderAction act, int q, OrderState st) { var o = new Order { OrderId = id, Name = name, OrderType = t, OrderAction = act, Quantity = q, OrderState = st, Instrument = mnq }; a.Orders.Add(o); return o; }
    static bool Any(List<string> l, string needle) { return l.Any(x => x.Contains(needle)); }

    static void Main()
    {
        if (System.IO.Directory.Exists(Dir)) System.IO.Directory.Delete(Dir, true);
        System.IO.Directory.CreateDirectory(Dir);
        NinjaTrader.Core.Globals.UserDataDir = Dir;
        T = Type.GetType("NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifier, AddOn");
        O = Activator.CreateInstance(T, true);
        Set("flattenCheckMaxMs", 1500); Set("autoFlattenWatchMs", 0);     // the stand-in never clears a position: keep /flatten checks short, no follow-ups
        Set("cfgAccountName", "ACC1,ACC2,ACC3");
        var a1 = Acct("ACC1", 50296.10); var a2 = Acct("ACC2", 50314.10); var a3 = Acct("ACC3", 50302.10);
        var tracked = (IList)Get("trackedAccounts"); tracked.Add(a1); tracked.Add(a2); tracked.Add(a3);

        // ---------- unprotected position
        Set("cfgProtectiveCheckSec", 1); Set("cfgOrphanOrderSec", 1); Set("cfgMaxPositionContracts", 10);
        Pos(a1, MarketPosition.Long, 3);
        Call("CheckExposure"); Check("no alert the instant a position opens", Drain().Count == 0);
        Thread.Sleep(1200); Call("CheckExposure"); var d = Drain();
        Check("long 3 with no stop for >1s -> UNPROTECTED POSITION", d.Count == 1 && d[0].Contains("UNPROTECTED POSITION") && d[0].Contains("Long 3") && d[0].Contains("No working stop"));
        Call("CheckExposure"); Check("same episode does not repeat", Drain().Count == 0);
        var stop1 = Ord(a1, "s1", "Stop1", OrderType.StopMarket, OrderAction.Sell, 1, OrderState.Working);
        Ord(a1, "t1", "ProfitTarget", OrderType.Limit, OrderAction.Sell, 3, OrderState.Working);
        Call("CheckExposure"); Check("still partly unprotected inside the same episode: quiet", Drain().Count == 0);
        Ord(a1, "s2", "Stop2", OrderType.StopMarket, OrderAction.Sell, 2, OrderState.Working);
        Call("CheckExposure"); Check("stops now cover 3 of 3: episode ends, no alert", Drain().Count == 0);
        a1.Orders.RemoveAll(o => o.Name.StartsWith("Stop"));
        Call("CheckExposure"); Thread.Sleep(1200); Call("CheckExposure"); d = Drain();
        Check("stops removed again -> a NEW episode alerts again", d.Count == 1 && d[0].Contains("UNPROTECTED POSITION"));
        a1.Orders.Clear(); Ord(a1, "s3", "Stop3", OrderType.StopMarket, OrderAction.Sell, 3, OrderState.Working);
        Call("CheckExposure"); Drain();                                   // fully protected again: the episode ends
        // a profit target alone must not count as protection; a BUY stop does not protect a long
        a1.Orders.Clear(); Ord(a1, "t2", "ProfitTarget", OrderType.Limit, OrderAction.Sell, 3, OrderState.Working); Ord(a1, "s4", "Stop4", OrderType.StopMarket, OrderAction.Buy, 3, OrderState.Working);
        Call("CheckExposure"); Thread.Sleep(1200); Call("CheckExposure"); d = Drain();
        Check("a target and a wrong-side stop do not count as protection", Any(d, "UNPROTECTED POSITION"));

        // ---------- orphan exit orders
        Pos(a1, MarketPosition.Flat, 0); a1.Orders.Clear(); Call("CheckExposure"); Drain();
        Ord(a1, "o1", "Entry", OrderType.Limit, OrderAction.Buy, 3, OrderState.Working);
        Call("CheckExposure"); Thread.Sleep(1200); Call("CheckExposure");
        Check("a pending ENTRY order while flat is not an orphan", Drain().Count == 0);
        Ord(a1, "o2", "ProfitTarget", OrderType.Limit, OrderAction.Sell, 3, OrderState.Working);
        Call("CheckExposure"); Thread.Sleep(1200); Call("CheckExposure"); d = Drain();
        Check("flat with a working ProfitTarget >1s -> ORPHAN EXIT ORDERS", d.Count == 1 && d[0].Contains("ORPHAN EXIT ORDERS") && d[0].Contains("ProfitTarget"));
        a1.Orders.Clear(); Call("CheckExposure"); Check("orphan cleared: episode closes quietly", Drain().Count == 0);

        // ---------- position limit
        Pos(a2, MarketPosition.Long, 12); Ord(a2, "x", "Stop1", OrderType.StopMarket, OrderAction.Sell, 12, OrderState.Working);
        Call("CheckExposure"); d = Drain();
        Check("12 contracts over a limit of 10 alerts immediately", d.Count == 1 && d[0].Contains("POSITION LIMIT EXCEEDED") && d[0].Contains("12 contracts"));
        Call("CheckExposure"); Check("limit alert does not repeat", Drain().Count == 0);
        Pos(a2, MarketPosition.Long, 5); a2.Orders.Clear(); Call("CheckExposure"); Drain();
        Pos(a2, MarketPosition.Long, 11); Ord(a2, "y", "Stop1", OrderType.StopMarket, OrderAction.Sell, 11, OrderState.Working); Call("CheckExposure");
        Check("a second breach after recovering alerts again", Any(Drain(), "POSITION LIMIT EXCEEDED"));
        Pos(a2, MarketPosition.Flat, 0); a2.Orders.Clear(); Call("CheckExposure"); Drain();
        Check("episode bookkeeping fully cleaned up when nothing is active", ((IDictionary)Get("episodeStart")).Count == 0);

        // ---------- stuck orders (through the real order scan)
        Set("cfgStuckOrderSec", 1); Set("cfgRunawayMaxOrders", 0); Set("cfgEnableOrderRejectAlerts", true);
        a3.Orders.Clear(); var stuck = Ord(a3, "q1", "Stop1", OrderType.StopMarket, OrderAction.Sell, 3, OrderState.Submitted);
        Call("ScanOrders"); Thread.Sleep(1200); Call("ScanOrders"); d = Drain();
        Check("order in Submitted >1s -> ORDER STUCK", d.Count == 1 && d[0].Contains("ORDER STUCK") && d[0].Contains("Submitted"));
        Call("ScanOrders"); Check("stuck alert fires once per order state", Drain().Count == 0);
        stuck.OrderState = OrderState.Working; Call("ScanOrders"); Thread.Sleep(1200); Call("ScanOrders");
        Check("a Working order is never 'stuck'", Drain().Count == 0);


        // ---------- risk floor buffer
        Set("cfgRiskFloor", 48000.0); Set("riskBuffers", Call("ParseDollarList", "1500,750,300"));
        Set("cfgProtectiveCheckSec", 0); Set("cfgOrphanOrderSec", 0); Set("cfgMaxPositionContracts", 0); Set("cfgStuckOrderSec", 0);
        a1.Orders.Clear(); a2.Orders.Clear(); a3.Orders.Clear(); Pos(a1, MarketPosition.Flat, 0); Pos(a2, MarketPosition.Flat, 0); Pos(a3, MarketPosition.Flat, 0); Drain();
        a1.NetLiq = 50296.10; a2.NetLiq = 50314.10; a3.NetLiq = 50302.10;
        Call("RiskTick", DateTime.UtcNow); Check("buffer 2,296 above floor: no alert", Drain().Count == 0);
        a1.NetLiq = 49000; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("buffer 1,000 -> RISK BUFFER at the 1,500 level", d.Count == 1 && d[0].Contains("RISK BUFFER") && d[0].Contains("1,000.00 above") && d[0].Contains("alert level 1,500.00"));
        Call("RiskTick", DateTime.UtcNow); Check("same level does not repeat", Drain().Count == 0);
        a1.NetLiq = 48700; Call("RiskTick", DateTime.UtcNow); Check("buffer 700 -> tighter 750 level alerts", Any(Drain(), "alert level 750.00"));
        a1.NetLiq = 48200; Call("RiskTick", DateTime.UtcNow); Check("buffer 200 -> tighter 300 level alerts", Any(Drain(), "alert level 300.00"));
        a1.NetLiq = 47950; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("below the floor alerts as BELOW (tightest level)", d.Count == 1 && d[0].Contains("50.00 BELOW") && !d[0].Contains("alert level"));
        a1.NetLiq = 50296.10; Call("RiskTick", DateTime.UtcNow); d = Drain();
        Check("recovery sends RISK BUFFER RECOVERED once", d.Count == 1 && d[0].Contains("RECOVERED"));
        a1.NetLiq = 49000; Call("RiskTick", DateTime.UtcNow); Check("a later dip alerts again after recovery", Any(Drain(), "RISK BUFFER"));
        a1.NetLiq = double.NaN; Call("RiskTick", DateTime.UtcNow); Check("unreadable Net Liquidation is skipped without an alert or crash", Drain().Count == 0);
        a1.NetLiq = 50296.10; Call("RiskTick", DateTime.UtcNow); Drain();

        // ---------- /floor command and persistence
        string reply = (string)Call("HandleFloorCommand", "48250");
        Check("/floor 48250 sets a floor for every tracked account", reply.Contains("set for every tracked account") && (double)Call("FloorFor", "ACC1") == 48250.0);
        reply = (string)Call("HandleFloorCommand", "2 48500");
        Check("/floor 2 48500 sets just ACC2", (double)Call("FloorFor", "ACC2") == 48500.0 && (double)Call("FloorFor", "ACC1") == 48250.0 && reply.Contains("ACC2"));
        string floorsFile = Dir + "/DirectTelegramNotifier.floors";
        Check("floors are written to disk", System.IO.File.Exists(floorsFile) && System.IO.File.ReadAllText(floorsFile).Contains("ACC2=48500"));
        ((IDictionary)Get("floorOverrides")).Clear(); Call("LoadFloorOverrides");
        Check("floors survive a restart (reload from disk)", (double)Call("FloorFor", "ACC2") == 48500.0 && (double)Call("FloorFor", "ACC3") == 48250.0);
        Check("bad amount shows usage", ((string)Call("HandleFloorCommand", "abc")).Contains("Usage"));
        Check("unknown account is reported", ((string)Call("HandleFloorCommand", "zzz 48500")).Contains("No tracked account"));
        Call("HandleFloorCommand", "2 clear");
        Check("/floor 2 clear removes only that override", (double)Call("FloorFor", "ACC2") == 48250.0);
        Call("HandleFloorCommand", "clear");
        Check("/floor clear falls back to the config floor and deletes the file", (double)Call("FloorFor", "ACC2") == 48000.0 && !System.IO.File.Exists(floorsFile));
        Check("/status shows net liq, floor and buffer", ((string)Call("BuildStatusReport")).Contains("buffer"));

        // ---------- auto-flatten (opt in; confirmed by time; latches the account)
        Connection.Connections.Clear(); Connection.Connections.Add(new Connection { Status = ConnectionStatus.Connected, PriceStatus = ConnectionStatus.Connected, Options = new ConnectionOptions { Name = "Sim" } });
        Set("cfgRiskAutoFlatten", true); Set("cfgRiskFlattenBuffer", 150.0); Set("cfgRiskConfirmSec", 2);
        Pos(a1, MarketPosition.Long, 3); a1.NetLiq = 48100; int before = a1.FlattenCount; DateTime t0 = DateTime.UtcNow;
        Call("RiskTick", t0); d = Drain();
        Check("first reading inside the flatten buffer does NOT flatten", a1.FlattenCount == before && !Any(d, "LOCKED"));
        Call("RiskTick", t0.AddSeconds(1)); d = Drain();
        Check("1s inside the buffer (< RiskConfirmSeconds) still does not flatten", a1.FlattenCount == before);
        Call("RiskTick", t0.AddSeconds(2.1)); d = Drain();
        Check("2s inside the buffer locks and flattens that account", a1.FlattenCount == before + 1 && Any(d, "ACCOUNT LOCKED") && Any(d, "MNQ DEC26"));
        Call("RiskTick", t0.AddSeconds(2.5)); Drain();
        Check("no second flatten while the first is fresh", a1.FlattenCount == before + 1);
        Check("only the account in trouble was flattened", a2.FlattenCount == 0 && a3.FlattenCount == 0);
        Call("ExecuteUnlockCommand", "1"); Pos(a1, MarketPosition.Flat, 0);
        a1.NetLiq = 50296.10; Call("RiskTick", DateTime.UtcNow); Drain();
        Set("cfgRiskAutoFlatten", false);

        // ---------- targeted flatten and cancel
        Pos(a1, MarketPosition.Long, 3); Pos(a2, MarketPosition.Long, 3); Pos(a3, MarketPosition.Flat, 0);
        a1.Orders.Clear(); a2.Orders.Clear(); Ord(a2, "w1", "Stop1", OrderType.StopMarket, OrderAction.Sell, 3, OrderState.Working); Ord(a2, "w2", "ProfitTarget", OrderType.Limit, OrderAction.Sell, 3, OrderState.Working);
        object[] ra = { "2", null }; var rm = T.GetMethod("ResolveAccounts", B); var res = (List<Account>)rm.Invoke(O, ra);
        Check("filter '2' resolves to exactly ACC2", res != null && res.Count == 1 && res[0].Name == "ACC2" && ra[1] == null);
        ra = new object[] { "ACC", null }; res = (List<Account>)rm.Invoke(O, ra);
        Check("filter 'ACC' is ambiguous and says so", res == null && ((string)ra[1]).Contains("several"));
        ra = new object[] { "zzz", null }; res = (List<Account>)rm.Invoke(O, ra);
        Check("unknown filter is reported", res == null && ((string)ra[1]).Contains("No tracked account"));
        int f1 = a1.FlattenCount, f2 = a2.FlattenCount, f3 = a3.FlattenCount;
        ((System.Threading.Tasks.Task)Call("ExecuteFlattenAsync", "2")).Wait(); d = Drain();
        Check("/flatten 2 flattens only ACC2", a2.FlattenCount == f2 + 1 && a1.FlattenCount == f1 && a3.FlattenCount == f3 && Any(d, "Scope: ACC2"));
        ((System.Threading.Tasks.Task)Call("ExecuteFlattenAsync", (object)null)).Wait(); d = Drain();
        Check("/flatten with no argument flattens every account that has exposure", a1.FlattenCount == f1 + 1 && a2.FlattenCount == f2 + 2 && a3.FlattenCount == f3 && Any(d, "tracked accounts") && Any(d, "ACC3: already flat"));
        Ord(a2, "w3", "Stop1", OrderType.StopMarket, OrderAction.Sell, 3, OrderState.Working);
        ((System.Threading.Tasks.Task)Call("ExecuteCancelAsync", "ACC2")).Wait(); d = Drain();
        Check("/cancel ACC2 cancels its working orders and keeps positions", a2.CancelCalls == 1 && a2.Orders.All(o => o.OrderState == OrderState.Cancelled) && a2.Positions.Count == 1 && Any(d, "CANCEL SENT") && Any(d, "No working orders left"));
        ((System.Threading.Tasks.Task)Call("ExecuteCancelAsync", (object)null)).Wait(); d = Drain();
        Check("/cancel with nothing working says so", Any(d, "ACC1: no working orders") && Any(d, "ACC3: no working orders"));
        Set("cfgFlattenConfirm", true); Call("RequestActionConfirmation", "flatten", "2"); d = Drain();
        Check("confirm prompt names the account", Any(d, "Confirm flatten?") && Any(d, "Scope: ACC2"));
        Call("RequestActionConfirmation", "flatten", "zzz"); Check("confirm prompt rejects an unknown account", Any(Drain(), "No tracked account"));
        Set("cfgFlattenConfirm", false);

        // ---------- machine health
        Call("CheckMachineHealth"); Thread.Sleep(1100); Call("CheckMachineHealth");
        Check("CPU, memory and disk are sampled", (int)Get("lastMemoryMb") > 0 && (DateTime)Get("lastCpuSampleUtc") > DateTime.MinValue && (long)Get("lastDiskFreeMb") >= 0);
        Drain(); DateTime now = DateTime.UtcNow;
        Set("cfgCpuAlertPercent", 90); Set("cfgCpuAlertSeconds", 30);
        Call("EvaluateCpu", 95.0, now); Check("high CPU is noted but silent at first", Drain().Count == 0);
        Call("EvaluateCpu", 95.0, now.AddSeconds(40)); d = Drain();
        Check("CPU >=90% for >=30s alerts", d.Count == 1 && d[0].Contains("NINJATRADER CPU HIGH") && d[0].Contains("95%"));
        Call("EvaluateCpu", 95.0, now.AddSeconds(90)); Check("CPU alert has a 30-minute cooldown", Drain().Count == 0);
        Call("EvaluateCpu", 20.0, now.AddSeconds(100)); Check("CPU recovery resets the timer silently", Drain().Count == 0 && (DateTime)Get("cpuHighSince") == DateTime.MinValue);
        Set("cfgMemoryAlertMb", 100); Call("EvaluateMemory", 150, now); Check("memory over the limit alerts", Any(Drain(), "MEMORY HIGH"));
        Call("EvaluateMemory", 150, now.AddMinutes(5)); Check("memory alert is rate-limited", Drain().Count == 0);
        Set("cfgDiskAlertMb", 2000); Call("EvaluateDisk", "C:", 500L, now); Check("low disk space alerts", Any(Drain(), "DISK SPACE LOW"));
        Call("EvaluateDisk", "C:", 5000L, now.AddHours(7)); Check("plenty of disk space stays quiet", Drain().Count == 0);

        // ---------- the new samples, as /simulate prints them
        Call("RunSimulation"); d = Drain();
        Console.WriteLine("\n===== the 9 new /simulate samples =====");
        foreach (string m in d.Skip(Math.Max(0, d.Count - 11)).Take(10)) Console.WriteLine("--- \n" + m);
        Check("/simulate sends 49 messages, in order and unmerged (29 in v0.2 + 10 in v0.3 + 8 in v0.4 + 2 in v0.5)", d.Count == 49 && d[0].Contains("SIMULATION") && d[48].Contains("Simulation finished"));
        Console.WriteLine("\n===== /help =====\n" + Plain((string)Call("BuildHelpText")));

        Console.WriteLine(fails == 0 ? "\nALL v0.2 CHECKS PASSED" : "\n" + fails + " CHECK(S) FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
