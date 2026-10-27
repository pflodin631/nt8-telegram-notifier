// Unit tests for DirectTelegramNotifierCore. No NinjaTrader, no NuGet, no test framework:
// run tests\run-tests.ps1 on Windows (uses the C# compiler that ships with .NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.Globalization;
using NinjaTrader.NinjaScript.AddOns;
using C = NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifierCore;

internal static class CoreTests
{
    private static int passed;
    private static int failed;

    private static void Check(bool ok, string name)
    {
        if (ok) { passed++; return; }
        failed++;
        Console.WriteLine("FAIL  " + name);
    }

    private static void Eq(double expected, double actual, string name)
    {
        bool ok = (double.IsNaN(expected) && double.IsNaN(actual)) || expected == actual || Math.Abs(expected - actual) < 1e-9;
        if (!ok)
            Console.WriteLine("      expected " + expected.ToString(CultureInfo.InvariantCulture) + ", got " + actual.ToString(CultureInfo.InvariantCulture));
        Check(ok, name);
    }

    private static void Eq(string expected, string actual, string name)
    {
        bool ok = expected == actual;
        if (!ok)
            Console.WriteLine("      expected [" + (expected ?? "null") + "], got [" + (actual ?? "null") + "]");
        Check(ok, name);
    }

    private static int Main()
    {
        RiskLevels();
        StaticAndTrailingFloors();
        DailyLoss();
        FloorValidation();
        Modes();
        Sessions();
        QuietWindows();
        ConfigKeys();
        FillsAndCommands();
        Json();

        Console.WriteLine();
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    private static void RiskLevels()
    {
        List<double> levels = C.ParseDollarList("1500, 750;300");
        Check(levels.Count == 3 && levels[0] == 300 && levels[2] == 1500, "ParseDollarList sorts ascending");
        Eq(double.PositiveInfinity, C.RiskLevel(2296.10, levels), "buffer above all levels -> none");
        Eq(1500, C.RiskLevel(1000, levels), "buffer 1000 -> 1500 level");
        Eq(750, C.RiskLevel(750, levels), "buffer exactly 750 -> 750 level");
        Eq(300, C.RiskLevel(120, levels), "buffer 120 -> 300 level");
        Eq(0, C.RiskLevel(0, levels), "buffer 0 -> at floor");
        Eq(0, C.RiskLevel(-50, levels), "negative buffer -> below floor");
        Eq(double.PositiveInfinity, C.RiskLevel(double.NaN, levels), "unknown buffer -> no level");
    }

    private static void StaticAndTrailingFloors()
    {
        Eq(48000, C.StaticFloor(50000, 2000), "static floor 50k - 2k");
        Eq(double.NaN, C.StaticFloor(50000, 0), "static floor needs a drawdown");
        Eq(double.NaN, C.StaticFloor(double.NaN, 2000), "static floor needs a starting balance");

        // End-of-day trailing, 50k account, 2,000 drawdown, locks at 50,100.  Worked by hand:
        double hw = 50000, floor = double.NaN, lockAt = 50100;
        floor = C.TrailingFloor(hw, 2000, lockAt, floor);
        Eq(48000, floor, "EOD day 0: floor = 50,000 - 2,000");
        hw = C.NextHighWater(hw, 51000); floor = C.TrailingFloor(hw, 2000, lockAt, floor);
        Eq(49000, floor, "EOD day 1 closes 51,000 -> floor 49,000");
        hw = C.NextHighWater(hw, 50500); floor = C.TrailingFloor(hw, 2000, lockAt, floor);
        Eq(51000, hw, "EOD day 2 losing close keeps high-water 51,000");
        Eq(49000, floor, "EOD day 2 floor unchanged");
        hw = C.NextHighWater(hw, 52600); floor = C.TrailingFloor(hw, 2000, lockAt, floor);
        Eq(50100, floor, "EOD day 3 closes 52,600 -> 50,600 capped at lock 50,100");
        hw = C.NextHighWater(hw, 55000); floor = C.TrailingFloor(hw, 2000, lockAt, floor);
        Eq(50100, floor, "after lock the floor stays at 50,100");
        hw = C.NextHighWater(hw, 49000); floor = C.TrailingFloor(hw, 2000, lockAt, floor);
        Eq(50100, floor, "floor never moves down");

        // Intraday trailing without a lock: follows the live peak.
        double ihw = double.NaN, ifloor = double.NaN;
        foreach (double netLiq in new[] { 50000.0, 50400.0, 50250.0, 50900.0, 50100.0 })
        {
            ihw = C.NextHighWater(ihw, netLiq);
            ifloor = C.TrailingFloor(ihw, 2500, double.NaN, ifloor);
        }
        Eq(50900, ihw, "intraday high-water is the live peak");
        Eq(48400, ifloor, "intraday floor = peak 50,900 - 2,500");
        Eq(48400, C.TrailingFloor(double.NaN, 2500, double.NaN, 48400), "no high-water keeps the previous floor");
        Eq(51000, C.NextHighWater(51000, double.NaN), "missing reading keeps the high-water");
    }

    private static void DailyLoss()
    {
        List<double> pct = new List<double> { 50, 75, 90 };
        Eq(0, C.DailyLossLevel(400, 1000, pct), "loss 400 of 1000 -> no alert");
        Eq(50, C.DailyLossLevel(500, 1000, pct), "loss 500 -> 50%");
        Eq(75, C.DailyLossLevel(760, 1000, pct), "loss 760 -> 75%");
        Eq(90, C.DailyLossLevel(900, 1000, pct), "loss 900 -> 90%");
        Eq(100, C.DailyLossLevel(1000, 1000, pct), "loss 1000 -> limit");
        Eq(100, C.DailyLossLevel(1350, 1000, pct), "loss beyond the limit -> limit");
        Eq(0, C.DailyLossLevel(-300, 1000, pct), "a winning day -> nothing");
        Eq(0, C.DailyLossLevel(800, double.NaN, pct), "no limit -> nothing");

        Eq(1000, C.ResolveAmountOrPercent("1000", 50000), "dollar limit");
        Eq(1000, C.ResolveAmountOrPercent("$1,000", 50000), "dollar limit with $ and comma");
        Eq(1000, C.ResolveAmountOrPercent("2%", 50000), "2% of 50,000");
        Eq(double.NaN, C.ResolveAmountOrPercent("2%", double.NaN), "percent without a base");
        Eq(double.NaN, C.ResolveAmountOrPercent("", 50000), "blank -> off");
        Eq(double.NaN, C.ResolveAmountOrPercent("abc", 50000), "garbage -> off");
        Eq(double.NaN, C.ResolveAmountOrPercent("-5", 50000), "negative -> off");
    }

    private static void FloorValidation()
    {
        Eq(null, C.ValidateFloor(48250, 50296.10, 300, 48000, 1000), "sensible floor passes");
        string typo = C.ValidateFloor(58250, 50296.10, 300, 48250, 1000);
        Check(typo != null && typo.Contains("at or above"), "typo above net liq is caught");
        Check(typo != null && typo.Contains("moves the floor by 10,000.00"), "typo is also a big jump");
        string close = C.ValidateFloor(50100, 50296.10, 300, double.NaN, 1000);
        Check(close != null && close.Contains("only 196.10 below"), "floor inside the smallest alert level is caught");
        Check(C.ValidateFloor(49500, 50296.10, 300, 48000, 1000) != null, "jump of 1,500 > 1,000 needs confirmation");
        Eq(null, C.ValidateFloor(49500, 50296.10, 300, 48000, 0), "jump check off");
        Eq(null, C.ValidateFloor(49500, double.NaN, 300, double.NaN, 1000), "no net liq and no previous floor -> nothing to check");
    }

    private static void Modes()
    {
        Eq("manual", C.NormalizeMode(""), "blank mode is manual");
        Eq("eod", C.NormalizeMode(" EOD "), "eod, any case");
        Eq("eod", C.NormalizeMode("end-of-day"), "eod alias");
        Eq("intraday", C.NormalizeMode("Intraday"), "intraday");
        Eq("static", C.NormalizeMode("static"), "static");
        Eq("off", C.NormalizeMode("off"), "off");
        Eq(null, C.NormalizeMode("trailing"), "ambiguous mode rejected");
    }

    private static void Sessions()
    {
        TimeSpan eod = new TimeSpan(17, 0, 0);
        Eq("2026-10-26", C.SessionKey(new DateTime(2026, 10, 26, 16, 59, 0), eod), "Mon 16:59 belongs to Monday's session");
        Eq("2026-10-27", C.SessionKey(new DateTime(2026, 10, 26, 17, 0, 0), eod), "Mon 17:00 starts Tuesday's session");
        Eq("2026-10-26", C.SessionKey(new DateTime(2026, 10, 25, 18, 0, 0), eod), "Sunday evening open belongs to Monday");
    }

    private static void QuietWindows()
    {
        string err;
        List<C.QuietWindow> cme = C.ParseQuietWindows(C.CmeQuietWindows, out err);
        Check(err == null && cme.Count == 4, "CME schedule parses");
        // 2026-10-26 is a Monday.
        Check(C.InQuietWindow(new DateTime(2026, 10, 26, 17, 30, 0), cme), "Mon 17:30 ET is the daily break");
        Check(!C.InQuietWindow(new DateTime(2026, 10, 26, 16, 59, 0), cme), "Mon 16:59 ET is open");
        Check(!C.InQuietWindow(new DateTime(2026, 10, 26, 18, 0, 0), cme), "Mon 18:00 ET reopens");
        Check(C.InQuietWindow(new DateTime(2026, 10, 30, 17, 0, 0), cme), "Fri 17:00 ET weekend begins");
        Check(C.InQuietWindow(new DateTime(2026, 10, 31, 12, 0, 0), cme), "Saturday closed");
        Check(C.InQuietWindow(new DateTime(2026, 11, 1, 17, 59, 0), cme), "Sun 17:59 still closed");
        Check(!C.InQuietWindow(new DateTime(2026, 11, 1, 18, 0, 0), cme), "Sun 18:00 opens");

        List<C.QuietWindow> custom = C.ParseQuietWindows("Daily 00:00-08:00; Weekends 00:00-24:00; Fri-Mon 22:00-23:00", out err);
        Check(err == null && custom.Count == 3, "custom schedule parses");
        Check(C.InQuietWindow(new DateTime(2026, 10, 28, 7, 59, 0), custom), "Daily window applies on Wednesday");
        Check(C.InQuietWindow(new DateTime(2026, 10, 26, 22, 30, 0), custom), "wrapping day range Fri-Mon includes Monday");
        Check(!C.InQuietWindow(new DateTime(2026, 10, 28, 22, 30, 0), custom), "Fri-Mon excludes Wednesday");

        C.ParseQuietWindows("Mon 18:00-17:00", out err);
        Check(err != null && err.Contains("crosses midnight"), "midnight-crossing window rejected");
        C.ParseQuietWindows("Funday 01:00-02:00", out err);
        Check(err != null && err.Contains("unknown days"), "bad day rejected");
        C.ParseQuietWindows("Mon 25:00-26:00", out err);
        Check(err != null, "bad time rejected");
        Check(C.ParseQuietWindows("", out err).Count == 0 && err == null, "empty schedule = never quiet");
    }

    private static void ConfigKeys()
    {
        List<string> unknown = C.FindUnknownKeys(new[] { "RiskAutoFlaten", "BotToken", "dailylosslimit", "RiskFloor:MFFU1", "StartingBalnce:MFFU1", "Foo", "EnableWatchdog:ACC" });
        Check(unknown.Count == 4, "four unrecognized keys (got " + unknown.Count + ")");
        Check(unknown.Contains("RiskAutoFlaten (did you mean RiskAutoFlatten?)"), "typo gets a suggestion");
        Check(unknown.Contains("StartingBalnce:MFFU1 (did you mean StartingBalance:MFFU1?)"), "per-account typo gets a suggestion");
        Check(unknown.Contains("Foo"), "unrelated key reported plainly");
        Check(unknown.Contains("EnableWatchdog:ACC (EnableWatchdog cannot be set per account)"), "non per-account key with an account");
        Eq(1, C.Levenshtein("flaten", "flatten"), "levenshtein insertion");
        Eq(3, C.Levenshtein("kitten", "sitting"), "levenshtein classic");

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in C.KnownKeys)
            Check(seen.Add(k), "known key listed once: " + k);
        foreach (string k in C.PerAccountKeys)
            Check(C.IsKnownKey(k), "per-account key is also a known key: " + k);
    }

    private static void FillsAndCommands()
    {
        Eq(-4, C.TickDifference(true, 29411.00, 29410.00, 0.25), "buy: follower 1 point cheaper = 4 ticks better");
        Eq(2, C.TickDifference(false, 29411.00, 29410.50, 0.25), "sell: follower 0.5 lower = 2 ticks worse");
        Eq("4 ticks better", C.DescribeTicks(-4), "describe better");
        Eq("1 tick worse", C.DescribeTicks(1), "describe singular");
        Eq("same price", C.DescribeTicks(0.04), "describe same");

        Eq("", C.CleanOrderName("73-007F6E72"), "copier id hidden");
        Eq("B27_Long", C.CleanOrderName(" B27_Long "), "strategy name kept");
        Eq("Entry", C.CleanOrderName("Entry"), "short names kept");

        Eq("/flatten", C.ParseCommand("/Flatten@MyBot 002"), "command lower-cased, bot name stripped");
        Eq("002", C.ParseArgument("/flatten@MyBot 002"), "argument");
        Eq("", C.ParseArgument("/status"), "no argument");
        Eq(null, C.ParseCommand("hello"), "not a command");
    }

    private static void Json()
    {
        object o = C.ParseJson("{\"ok\":true,\"result\":[{\"update_id\":12,\"message\":{\"text\":\"/status \\u00e9\",\"chat\":{\"id\":-100123}}}]}");
        Dictionary<string, object> root = o as Dictionary<string, object>;
        Check(root != null && (bool)root["ok"], "json object and bool");
        List<object> result = root != null ? root["result"] as List<object> : null;
        Check(result != null && result.Count == 1, "json array");
        Dictionary<string, object> msg = ((Dictionary<string, object>)result[0])["message"] as Dictionary<string, object>;
        Eq("/status \u00e9", (string)msg["text"], "json unicode escape");
        Check(Convert.ToInt64(((Dictionary<string, object>)msg["chat"])["id"], CultureInfo.InvariantCulture) == -100123, "json negative number");
    }
}
