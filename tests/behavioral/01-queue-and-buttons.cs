using System; using System.Collections; using System.Collections.Generic; using System.Reflection; using System.Text.RegularExpressions;
using NinjaTrader.Cbi;
class H2
{
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dtn-h2");
    static object O; static Type T;
    static BindingFlags B = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object Call(string name, params object[] args)
    {
        foreach (var m in T.GetMethods(B)) if (m.Name == name && m.GetParameters().Length == args.Length) return m.Invoke(m.IsStatic ? null : O, args);
        throw new Exception("no method " + name);
    }
    static void Set(string f, object v) { T.GetField(f, B).SetValue(O, v); }
    static object Get(string f) { return T.GetField(f, B).GetValue(O); }
    static string Plain(string s) { return Regex.Replace(s, "<[^>]+>", "").Replace("&amp;", "&"); }
    static List<string[]> Drain()
    {
        var res = new List<string[]>();
        while (true)
        {
            object m = Call("PeekNextMessage"); if (m == null) break; Call("RemoveMessage", m);
            res.Add(new[] { Plain((string)m.GetType().GetField("Text").GetValue(m)), ((bool)m.GetType().GetField("Silent").GetValue(m)) ? "silent" : "loud", (string)m.GetType().GetField("ReplyMarkup").GetValue(m) });
        }
        return res;
    }
    static int fails = 0;
    static void Check(string what, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; }
    static void Main()
    {
        if (System.IO.Directory.Exists(Dir)) System.IO.Directory.Delete(Dir, true);
        System.IO.Directory.CreateDirectory(Dir);
        NinjaTrader.Core.Globals.UserDataDir = Dir;
        T = Type.GetType("NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifier, AddOn");
        O = Activator.CreateInstance(T, true);
        Set("cfgAccountName", "LeadAcct,F1");
        var mnq = new Instrument { FullName = "MNQ DEC26", MasterInstrument = new MasterInstrument { PointValue = 2.0, TickSize = 0.25 } };
        Func<string,string,bool,int,double,Instrument,string,Execution> ex = (a, id, buy, q, px, i, n) => new Execution { Account = new Account { Name = a }, Instrument = i, Quantity = q, Price = px, Time = DateTime.Now, ExecutionId = id, MarketPosition = buy ? MarketPosition.Long : MarketPosition.Short, Order = new Order { OrderId = "o" + id, Name = n } };

        // quiet vs loud
        Set("cfgSilentRoutine", true);
        Call("EnqueueQuiet", "routine"); Call("EnqueueText", "problem");
        var d = Drain();
        Check("SilentRoutineAlerts=true: routine silent, problem loud", d.Count == 2 && d.Find(x => x[0] == "routine")[1] == "silent" && d.Find(x => x[0] == "problem")[1] == "loud");
        Check("a problem alert is sent before an earlier routine message", d[0][0] == "problem");
        Set("cfgSilentRoutine", false);
        Call("EnqueueQuiet", "routine");
        Check("SilentRoutineAlerts=false: routine rings like any other", Drain()[0][1] == "loud");

        // confirm buttons payload is valid JSON with the expected callbacks
        Call("RequestActionConfirmation", "flatten", (object)null);
        d = Drain();
        string markup = d[0][2];
        var json = Call("TryParseObject", markup) as Dictionary<string, object>;
        Check("flatten confirm sends a reply_markup", !string.IsNullOrEmpty(markup));
        Check("reply_markup is valid JSON", json != null);
        Check("buttons carry ok:/cancel: tokens", markup.Contains("\"callback_data\":\"ok:") && markup.Contains("\"callback_data\":\"cancel:"));
        Console.WriteLine("      " + d[0][0].Replace("\n", " | "));

        // opened flag + re-entry after flatten
        object[] a1 = { ex("LeadAcct", "e1", true, 3, 29411.0, mnq, "B27_Long"), null }; bool r1 = (bool)T.GetMethods(B).First2("RecordExecution", 2).Invoke(O, a1);
        Check("entry fill reports opened=true", r1 && (bool)a1[1]);
        object[] a2 = { ex("LeadAcct", "e2", false, 3, 29412.0, mnq, "Close"), null }; T.GetMethods(B).First2("RecordExecution", 2).Invoke(O, a2);
        Check("closing fill reports opened=false", !(bool)a2[1]);
        Set("lastFlattenUtc", DateTime.UtcNow.AddSeconds(-14)); Set("reentryAlerted", false);
        Call("CheckReentryAfterFlatten", ex("LeadAcct", "e3", true, 3, 29413.0, mnq, "B27_Long"));
        d = Drain();
        Check("fill 14s after /flatten raises a re-entry alert", d.Count == 1 && d[0][0].Contains("RE-ENTERED AFTER FLATTEN"));
        Call("CheckReentryAfterFlatten", ex("LeadAcct", "e4", true, 3, 29413.0, mnq, "B27_Long"));
        Check("only one re-entry alert per flatten", Drain().Count == 0);
        Set("lastFlattenUtc", DateTime.UtcNow.AddSeconds(-14)); Set("reentryAlerted", false);
        Call("CheckReentryAfterFlatten", ex("LeadAcct", "e5", false, 3, 29413.0, mnq, "Close"));
        Check("the flatten's own 'Close' orders never alert", Drain().Count == 0);
        Set("lastFlattenUtc", DateTime.UtcNow.AddSeconds(-400)); Set("reentryAlerted", false);
        Call("CheckReentryAfterFlatten", ex("LeadAcct", "e6", true, 3, 29413.0, mnq, "B27_Long"));
        Check("a fill long after /flatten is normal trading", Drain().Count == 0);

        // memory bounding
        var seenOrders = (HashSet<string>)Get("seenOrderIds"); var rejected = (HashSet<string>)Get("rejectedAlerted");
        seenOrders.Add("LeadAcct|a"); seenOrders.Add("LeadAcct|b"); seenOrders.Add("F1|a"); rejected.Add("LeadAcct|b");
        Call("PruneOrderIds", "LeadAcct", new HashSet<string> { "LeadAcct|a" });
        Check("order ids NinjaTrader dropped are forgotten", !seenOrders.Contains("LeadAcct|b") && !rejected.Contains("LeadAcct|b"));
        Check("ids still listed, and other accounts' ids, are kept", seenOrders.Contains("LeadAcct|a") && seenOrders.Contains("F1|a"));
        var seenExec = (Dictionary<string, DateTime>)Get("seenExecutionIds");
        int before = seenExec.Count;
        seenExec["ancient"] = DateTime.UtcNow.AddHours(-100);
        Set("lastPruneUtc", DateTime.UtcNow.AddHours(-1));
        Call("PruneTrackingSets");
        Check("execution ids older than 72h are pruned, recent ones kept", !seenExec.ContainsKey("ancient") && seenExec.Count == before);

        // plain-text fallback and clock helper
        string html = "<b>BUY</b> 3 &amp; x &lt;y&gt;";
        Check("plain-text fallback strips tags and decodes entities", (string)Call("ToPlainText", html) == "BUY 3 & x <y>");
        Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : "\n" + fails + " CHECK(S) FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
static class Ext { public static MethodInfo First2(this MethodInfo[] ms, string name, int n) { foreach (var m in ms) if (m.Name == name && m.GetParameters().Length == n) return m; throw new Exception(name); } }
