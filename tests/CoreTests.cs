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
        BasisConsistencyInactivity();
        RiskMachine();
        SurvivorsAndReadiness();
        Boundaries();
        BoundariesTwo();
        BackupAndOpenPnl();
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

    private static void BasisConsistencyInactivity()
    {
        Eq("balance", C.NormalizeBasis(""), "blank basis is balance");
        Eq("balance", C.NormalizeBasis(" Closing "), "balance alias");
        Eq("netliq", C.NormalizeBasis("NetLiq"), "netliq");
        Eq("netliq", C.NormalizeBasis("equity"), "netliq alias");
        Eq(null, C.NormalizeBasis("cash"), "unknown basis rejected");
        Eq(50000, C.BalanceFromNetLiq(50350, 350), "balance = net liq - open profit");
        Eq(50000, C.BalanceFromNetLiq(49800, -200), "balance = net liq + open loss");
        Eq(double.NaN, C.BalanceFromNetLiq(double.NaN, 0), "no net liq -> no balance");
        Eq(double.NaN, C.BalanceFromNetLiq(50000, double.NaN), "no open P&L reading -> no balance");

        // Consistency: after 1,000 of profit, a 30% rule allows 428.57 today (428.57 / 1,428.57 = 30%).
        double cap30 = C.ConsistencyCap(1000, 30, 0);
        Eq(1000.0 * 0.3 / 0.7, cap30, "30% rule after 1,000 profit: cap = 428.57");
        Eq(0.3, cap30 / (1000 + cap30), "...which is exactly 30% of the new total");
        Eq(1000, C.ConsistencyCap(1000, 50, 0), "50% rule after 1,000 profit: cap = 1,000");
        Eq(1500, C.ConsistencyCap(0, 50, 3000), "profit-target basis: 50% of a 3,000 target on day one");
        Eq(1500, C.ConsistencyCap(1000, 50, 3000), "target basis wins while profit is below it");
        Eq(2000, C.ConsistencyCap(2000, 50, 3000), "profit basis wins once profit is above the target");
        Eq(double.NaN, C.ConsistencyCap(0, 30, 0), "no profit yet and no target: nothing to cap");
        Eq(double.NaN, C.ConsistencyCap(-200, 30, 0), "a losing account has no cap");
        Eq(double.NaN, C.ConsistencyCap(1000, 0, 0), "0% = rule off");
        Eq(double.NaN, C.ConsistencyCap(1000, 100, 0), "100% is not a rule");
        Eq(double.NaN, C.ConsistencyCap(1000, double.NaN, 0), "blank percent = rule off");
        Eq(0.3, C.ProfitShare(300, 1000), "best day 300 of 1,000 = 30%");
        Eq(double.NaN, C.ProfitShare(300, 0), "no total profit -> no share");
        Eq(double.NaN, C.ProfitShare(300, -50), "negative total -> no share");
        List<double> pct = new List<double> { 75, 90 };
        Eq(0, C.DailyLossLevel(300, cap30, pct), "today +300 of 428.57: below the alert levels");
        Eq(75, C.DailyLossLevel(330, cap30, pct), "today +330 = 77% of the cap -> 75% level");
        Eq(90, C.DailyLossLevel(390, cap30, pct), "today +390 = 91% of the cap -> 90% level");
        Eq(100, C.DailyLossLevel(430, cap30, pct), "today +430 passes the cap -> limit");

        // Inactivity: 7-day limit, last trade Monday 10:00 UTC. 2026-10-26 is a Monday.
        DateTime last = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
        Eq(3.0, C.InactivityRemainingDays(last, last.AddDays(4), 7), "4 days after the last trade: 3 days left");
        Eq(0.0, C.InactivityRemainingDays(last, last.AddDays(7), 7), "exactly at the limit: 0 days left");
        Check(C.InactivityRemainingDays(last, last.AddDays(8), 7) < 0, "past the limit: negative");
        Eq(double.NaN, C.InactivityRemainingDays(last, last.AddDays(4), 0), "0 days = rule off");
        Eq(double.NaN, C.InactivityRemainingDays(last, last.AddDays(4), double.NaN), "blank = rule off");
        List<double> days = C.ParseDollarList("2,1");
        Eq(double.PositiveInfinity, C.RiskLevel(C.InactivityRemainingDays(last, last.AddDays(4), 7), days), "3 days left, alerts at 2 and 1: quiet");
        Eq(2, C.RiskLevel(C.InactivityRemainingDays(last, last.AddDays(5), 7), days), "2 days left: first alert");
        Eq(1, C.RiskLevel(C.InactivityRemainingDays(last, last.AddDays(6.2), 7), days), "0.8 days left: second alert");
        Eq(0, C.RiskLevel(C.InactivityRemainingDays(last, last.AddDays(7.1), 7), days), "past the limit: expired");

        // the new keys are known, and per-account typos are caught
        Check(C.IsKnownKey("HighWaterBasis") && C.IsKnownKey("InactivityDays") && C.IsKnownKey("ConsistencyPercent") && C.IsKnownKey("UnknownFlattenSeconds") && C.IsKnownKey("LockGroup"), "new keys are known");
        Check(C.IsPerAccountKey("InactivityDays") && C.IsPerAccountKey("ConsistencyPercent") && C.IsPerAccountKey("HighWaterBasis"), "new per-account keys");
        List<string> bad = C.FindUnknownKeys(new[] { "InactivityDay:ACC1", "UnknownFlattenSecond", "LockGroup:ACC1" });
        Check(bad.Count == 3 && bad[0].Contains("did you mean InactivityDays:ACC1?") && bad[1].Contains("did you mean UnknownFlattenSeconds?") && bad[2].Contains("cannot be set per account"), "typos in the new keys get suggestions");
    }

    // ---------------------------------------------------------------- the risk state machine
    private static readonly DateTime Fri = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);      // a Friday

    private static DtnRiskSettings Eod()
    {
        return new DtnRiskSettings
        {
            Mode = C.ModeEod, Basis = C.BasisBalance, Drawdown = 2000, FloorLock = 50100, StartingBalance = 50000,
            RiskBuffers = C.ParseDollarList("1500,750,300"), DailyLossAlerts = new List<double> { 50, 75, 90 },
            ConsistencyAlerts = new List<double> { 75, 90 }
        };
    }

    private static DtnRiskResult Tick(DtnRiskState rs, DtnRiskSettings s, DateTime now, string session, double netLiq, double balance, string unknown, string lockedSession)
    {
        return C.EvaluateRisk(rs, new DtnRiskInput { Now = now, Session = session, NetLiq = netLiq, Balance = balance, UnknownWhy = unknown, LockedSession = lockedSession, Settings = s });
    }

    private static DtnRiskResult Tick(DtnRiskState rs, DtnRiskSettings s, DateTime now, string session, double netLiq)
    {
        return Tick(rs, s, now, session, netLiq, netLiq, null, null);
    }

    private static int Count(DtnRiskResult r, DtnRiskEventKind kind)
    {
        int n = 0;
        foreach (DtnRiskEvent e in r.Events)
            if (e.Kind == kind) n++;
        return n;
    }

    private static DtnRiskEvent First(DtnRiskResult r, DtnRiskEventKind kind)
    {
        foreach (DtnRiskEvent e in r.Events)
            if (e.Kind == kind) return e;
        return null;
    }

    private static void RiskMachine()
    {
        // ---- end-of-day trailing across a weekend (50k, 2,000 drawdown, locks at 50,100, balance basis). Session ends 21:00 UTC.
        DtnRiskSettings s = Eod();
        DtnRiskState rs = new DtnRiskState();
        DtnRiskResult r = Tick(rs, s, Fri.AddHours(20), "2026-10-30", 50500);
        Check(Count(r, DtnRiskEventKind.HighWaterStarted) == 1 && First(r, DtnRiskEventKind.HighWaterStarted).Loud && First(r, DtnRiskEventKind.HighWaterStarted).Flag,
              "first tick on an account already 500 above StartingBalance warns loudly that the mark started low");
        Eq(50000, rs.HighWater, "...and the mark starts at StartingBalance");
        Eq(48000, rs.TrailFloor, "...so the floor is 50,000 - 2,000");
        r = Tick(rs, s, Fri.AddHours(20).AddMinutes(58), "2026-10-30", 51000);
        Check(r.Events.Count == 0, "intraday profit does not move an end-of-day floor");
        Eq(48000, rs.TrailFloor, "floor still 48,000 before the close");
        r = Tick(rs, s, Fri.AddHours(21).AddMinutes(1), "2026-10-31", 51000);
        Eq(51000, rs.HighWater, "Friday close: high-water = the 51,000 closing balance");
        Eq(49000, rs.TrailFloor, "...floor 49,000");
        Check(Count(r, DtnRiskEventKind.FloorTrailed) == 1 && First(r, DtnRiskEventKind.FloorTrailed).A == 48000 && First(r, DtnRiskEventKind.FloorTrailed).B == 49000 && !First(r, DtnRiskEventKind.FloorTrailed).Loud,
              "a quiet FloorTrailed 48,000 -> 49,000");
        Eq(500, rs.BestDay, "best day = 51,000 - 50,500 = 500");
        Eq(51000, rs.SessionStartBalance, "new session baseline");
        r = Tick(rs, s, Fri.AddHours(36), "2026-10-31", 51000);
        Check(r.Events.Count == 0, "Saturday noon: nothing");
        r = Tick(rs, s, Fri.AddHours(45).AddMinutes(1), "2026-11-01", 51000);
        Check(r.Events.Count == 0 && rs.HighWater == 51000 && rs.TrailFloor == 49000 && rs.BestDay == 500,
              "Saturday 21:01 (9 h unwatched, balance unchanged): trusted, nothing moves, best day not inflated");
        r = Tick(rs, s, Fri.AddHours(69).AddMinutes(1), "2026-11-02", 51000);
        Check(r.Events.Count == 0 && rs.HighWater == 51000 && rs.TrailFloor == 49000 && rs.BestDay == 500, "Sunday 21:01: same");
        r = Tick(rs, s, Fri.AddHours(81), "2026-11-02", 51200);
        Check(r.Events.Count == 0, "Monday: +200 realized, buffer 2,200: quiet");
        r = Tick(rs, s, Fri.AddHours(92).AddMinutes(58), "2026-11-02", 51200);
        r = Tick(rs, s, Fri.AddHours(93).AddMinutes(1), "2026-11-03", 51200);
        Eq(51200, rs.HighWater, "Monday close: high-water 51,200");
        Eq(49200, rs.TrailFloor, "...floor 49,200");
        Eq(500, rs.BestDay, "Monday's 200 is not the best day");

        // ---- the lock: the floor trails up to the lock level and stops
        r = Tick(rs, s, Fri.AddHours(105), "2026-11-03", 52600);
        r = Tick(rs, s, Fri.AddHours(116).AddMinutes(58), "2026-11-03", 52600);
        r = Tick(rs, s, Fri.AddHours(117).AddMinutes(1), "2026-11-04", 52600);
        Eq(50100, rs.TrailFloor, "52,600 close would give 50,600: capped at the 50,100 lock");
        r = Tick(rs, s, Fri.AddHours(130), "2026-11-04", 49000);
        r = Tick(rs, s, Fri.AddHours(140).AddMinutes(58), "2026-11-04", 49000);
        r = Tick(rs, s, Fri.AddHours(141).AddMinutes(1), "2026-11-05", 49000);
        Eq(50100, rs.TrailFloor, "a losing day never moves the floor down");

        // ---- an overnight restart: NinjaTrader closed for 14 hours across the boundary
        DtnRiskState before = new DtnRiskState
        {
            Session = "2026-10-30", HighWater = 51000, TrailFloor = 49000, LastBalance = 51000, LastNetLiq = 51000,
            SessionStartBalance = 50500, LastSeenUtc = Fri.AddHours(20)
        };
        r = Tick(before, s, Fri.AddHours(34), "2026-10-31", 51400, 51400, null, null);
        Check(Count(r, DtnRiskEventKind.ClosingUncertain) == 1 && First(r, DtnRiskEventKind.ClosingUncertain).Loud
              && First(r, DtnRiskEventKind.ClosingUncertain).A == 51000 && First(r, DtnRiskEventKind.ClosingUncertain).B == 51400,
              "restart after the account changed (51,000 -> 51,400): CLOSING UNCERTAIN with both numbers");
        Check(before.HighWater == 51000 && before.TrailFloor == 49000 && double.IsNaN(before.BestDay), "...high-water, floor and best day are left alone, not guessed");
        Eq(51400, before.SessionStartBalance, "...and the new session starts from what is there now");
        DtnRiskState same = new DtnRiskState
        {
            Session = "2026-10-30", HighWater = 51000, TrailFloor = 49000, LastBalance = 51000, LastNetLiq = 51000,
            SessionStartBalance = 50500, LastSeenUtc = Fri.AddHours(20)
        };
        r = Tick(same, s, Fri.AddHours(34), "2026-10-31", 51000.5, 51000.5, null, null);
        Check(Count(r, DtnRiskEventKind.ClosingUncertain) == 0 && same.BestDay == 500 && same.HighWater == 51000, "restart with the balance unchanged (within 1.00): the last reading is the close");
        DtnRiskState oldFile = new DtnRiskState { Session = "2026-10-30", HighWater = 51000, TrailFloor = 49000, LastBalance = 51000, SessionStartBalance = 50500 };
        r = Tick(oldFile, s, Fri.AddHours(34), "2026-10-31", 51000, 51000, null, null);
        Check(Count(r, DtnRiskEventKind.ClosingUncertain) == 0, "a state file with no 'last seen' time is treated as a gap, and trusted only because the balance matches");

        // ---- net liquidation basis follows net liquidation, not the balance
        DtnRiskSettings nl = Eod(); nl.Basis = C.BasisNetLiq;
        DtnRiskState nrs = new DtnRiskState { Session = "2026-10-30", HighWater = 50000, TrailFloor = 48000, LastBalance = 50200, LastNetLiq = 50600, SessionStartBalance = 50000, LastSeenUtc = Fri.AddHours(20).AddMinutes(58) };
        Tick(nrs, nl, Fri.AddHours(21).AddMinutes(1), "2026-10-31", 50600, 50200, null, null);
        Eq(50600, nrs.HighWater, "basis netliq: the 50,600 net liquidation sets the mark");
        DtnRiskState brs = new DtnRiskState { Session = "2026-10-30", HighWater = 50000, TrailFloor = 48000, LastBalance = 50200, LastNetLiq = 50600, SessionStartBalance = 50000, LastSeenUtc = Fri.AddHours(20).AddMinutes(58) };
        Tick(brs, s, Fri.AddHours(21).AddMinutes(1), "2026-10-31", 50600, 50200, null, null);
        Eq(50200, brs.HighWater, "basis balance: the 50,200 closing balance does (open profit of 400 is ignored)");

        // ---- a data outage while holding a position (manual floor 48,000, flatten after 120 s)
        DtnRiskSettings man = new DtnRiskSettings { Mode = C.ModeManual, ManualFloor = 48000, RiskBuffers = C.ParseDollarList("1500,750,300"), UnknownFlattenSeconds = 120 };
        DtnRiskState o = new DtnRiskState();
        DateTime t0 = Fri.AddHours(15);
        r = Tick(o, man, t0, "2026-10-30", 50000);
        Check(r.Events.Count == 0 && r.FireReason == null, "healthy: nothing");
        r = Tick(o, man, t0.AddSeconds(10), "2026-10-30", 50000, 50000, "market data for MNQ is stale", null);
        Check(Count(r, DtnRiskEventKind.Unknown) == 1 && First(r, DtnRiskEventKind.Unknown).Loud && First(r, DtnRiskEventKind.Unknown).A == 0 && First(r, DtnRiskEventKind.Unknown).N == 120, "outage starts: one loud UNKNOWN");
        r = Tick(o, man, t0.AddSeconds(20), "2026-10-30", 40000, 40000, "market data for MNQ is stale", null);
        Check(r.Events.Count == 0 && r.FireReason == null && o.LastNetLiq == 50000, "frozen net liquidation (40,000) is ignored: no floor alert, no flatten, no reading stored");
        r = Tick(o, man, t0.AddSeconds(40), "2026-10-30", 40000, 40000, "market data for MNQ is stale", null);
        Check(r.Events.Count == 0, "30 s in: no repeat");
        r = Tick(o, man, t0.AddSeconds(71), "2026-10-30", 40000, 40000, "market data for MNQ is stale", null);
        Check(Count(r, DtnRiskEventKind.Unknown) == 1 && First(r, DtnRiskEventKind.Unknown).A == 61, "after a minute: a reminder saying it has lasted 61 s");
        r = Tick(o, man, t0.AddSeconds(129), "2026-10-30", 40000, 40000, "market data for MNQ is stale", null);
        Check(r.FireReason == null, "119 s in: not yet");
        r = Tick(o, man, t0.AddSeconds(130), "2026-10-30", 40000, 40000, "market data for MNQ is stale", null);
        Check(r.FireReason != null && r.FireReason.Contains("120s") && r.FireReason.Contains("stale"), "120 s in: flatten and lock, with the reason");
        DtnRiskState o2 = new DtnRiskState();
        Tick(o2, man, t0, "2026-10-30", 50000);
        Tick(o2, man, t0.AddSeconds(10), "2026-10-30", 50000, 50000, "x", null);
        r = Tick(o2, man, t0.AddSeconds(300), "2026-10-30", 50000, 50000, "x", "2026-10-30");
        Check(r.FireReason == null, "an account that is already locked is not flattened again");
        DtnRiskSettings noFlatten = new DtnRiskSettings { Mode = C.ModeManual, ManualFloor = 48000, RiskBuffers = C.ParseDollarList("1500,750,300") };
        DtnRiskState o3 = new DtnRiskState();
        Tick(o3, noFlatten, t0, "2026-10-30", 50000);
        Tick(o3, noFlatten, t0.AddSeconds(10), "2026-10-30", 50000, 50000, "x", null);
        r = Tick(o3, noFlatten, t0.AddSeconds(3600), "2026-10-30", 50000, 50000, "x", null);
        Check(r.FireReason == null && Count(r, DtnRiskEventKind.Unknown) == 1, "UnknownFlattenSeconds=0: an hour of outage still only alerts");
        r = Tick(o, man, t0.AddSeconds(200), "2026-10-30", 47900);
        Check(Count(r, DtnRiskEventKind.DataBack) == 1 && !First(r, DtnRiskEventKind.DataBack).Loud, "data back: a quiet RISK DATA BACK");
        DtnRiskEvent buf = First(r, DtnRiskEventKind.RiskBuffer);
        Check(buf != null && buf.Loud && buf.A == 47900 && buf.C == -100 && buf.D == 0 && buf.Text == "config",
              "...and the floor check resumes at once on the real 47,900: BELOW the 48,000 floor, level 0");

        // ---- floor levels, recovery and the time-based auto-flatten
        DtnRiskSettings af = new DtnRiskSettings { Mode = C.ModeManual, ManualFloor = 48000, RiskBuffers = C.ParseDollarList("1500,750,300"), RiskAutoFlatten = true, FlattenBuffer = 150, ConfirmSeconds = 2 };
        DtnRiskState f = new DtnRiskState();
        Tick(f, af, t0, "s", 50296.10);
        r = Tick(f, af, t0.AddSeconds(1), "s", 49000);
        Check(Count(r, DtnRiskEventKind.RiskBuffer) == 1 && First(r, DtnRiskEventKind.RiskBuffer).D == 1500, "buffer 1,000 -> the 1,500 level");
        Check(Tick(f, af, t0.AddSeconds(2), "s", 49000).Events.Count == 0, "same level: not repeated");
        Check(First(Tick(f, af, t0.AddSeconds(3), "s", 48700), DtnRiskEventKind.RiskBuffer).D == 750, "buffer 700 -> 750");
        Check(First(Tick(f, af, t0.AddSeconds(4), "s", 48200), DtnRiskEventKind.RiskBuffer).D == 300, "buffer 200 -> 300");
        r = Tick(f, af, t0.AddSeconds(5), "s", 48100);
        Check(r.FireReason == null && f.FloorBreachSince == t0.AddSeconds(5), "buffer 100 is inside the flatten buffer: the clock starts, nothing fires");
        Check(Tick(f, af, t0.AddSeconds(6.9), "s", 48100).FireReason == null, "1.9 s later: not yet");
        r = Tick(f, af, t0.AddSeconds(7), "s", 48100);
        Check(r.FireReason != null && r.FireReason.Contains("only 100.00 above the floor 48,000.00"), "2 s inside the buffer: fires, with the numbers");
        Check(Tick(f, af, t0.AddSeconds(8), "s", 48100, 48100, null, "s").FireReason == null, "...but not for an account that is already locked");
        Check(Tick(f, af, t0.AddSeconds(9), "s", 48500).FireReason == null && f.FloorBreachSince == DateTime.MinValue, "stepping back outside the buffer resets the clock");
        r = Tick(f, af, t0.AddSeconds(10), "s", 50400);
        Check(Count(r, DtnRiskEventKind.RiskRecovered) == 1 && !First(r, DtnRiskEventKind.RiskRecovered).Loud, "recovery is quiet");

        // ---- daily loss
        DtnRiskSettings dl = new DtnRiskSettings { Mode = C.ModeOff, DailyLimitText = "2%", DailyLossAlerts = new List<double> { 50, 75, 90 }, DailyLossAutoFlatten = true, ConfirmSeconds = 2 };
        DtnRiskState d = new DtnRiskState();
        Tick(d, dl, t0, "s", 51000);
        r = Tick(d, dl, t0.AddSeconds(1), "s", 50400);
        Check(Count(r, DtnRiskEventKind.DailyLoss) == 1 && First(r, DtnRiskEventKind.DailyLoss).N == 50 && First(r, DtnRiskEventKind.DailyLoss).B == 1020,
              "2% of 51,000 = 1,020; down 600 (59%) -> the 50% alert");
        r = Tick(d, dl, t0.AddSeconds(2), "s", 49950);
        Check(First(r, DtnRiskEventKind.DailyLoss).N == 100 && r.FireReason == null, "down 1,050: limit reached, clock starts");
        Check(Tick(d, dl, t0.AddSeconds(4.1), "s", 49950).FireReason.Contains("daily loss limit"), "2 s later: fires");

        // ---- consistency (30%, 1,000 banked: cap 428.57)
        DtnRiskSettings cs = new DtnRiskSettings { Mode = C.ModeOff, StartingBalance = 50000, ConsistencyPercent = 30, ConsistencyAlerts = new List<double> { 75, 90 }, ConsistencyAutoFlatten = true, ConfirmSeconds = 2 };
        DtnRiskState c = new DtnRiskState();
        Tick(c, cs, t0, "s", 51000);
        Check(Tick(c, cs, t0.AddSeconds(1), "s", 51300).Events.Count == 0, "+300 of 428.57: quiet");
        r = Tick(c, cs, t0.AddSeconds(2), "s", 51330);
        Check(Count(r, DtnRiskEventKind.Consistency) == 1 && First(r, DtnRiskEventKind.Consistency).N == 75 && Math.Abs(First(r, DtnRiskEventKind.Consistency).B - 428.5714285714) < 1e-6, "+330: the 75% alert, cap 428.57");
        Check(First(Tick(c, cs, t0.AddSeconds(3), "s", 51395), DtnRiskEventKind.Consistency).N == 90, "+395: 90%");
        r = Tick(c, cs, t0.AddSeconds(4), "s", 51430);
        Check(First(r, DtnRiskEventKind.Consistency).N == 100 && r.FireReason == null, "+430: the cap, clock starts");
        Check(Tick(c, cs, t0.AddSeconds(6.1), "s", 51430).FireReason.Contains("consistency rule"), "2 s later: fires");
        r = Tick(c, cs, t0.AddSeconds(7), "s", 51100);
        Check(Count(r, DtnRiskEventKind.ConsistencyBack) == 1 && !First(r, DtnRiskEventKind.ConsistencyBack).Loud, "back under the levels: quiet");
        Eq(0, C.ConsistencyCure(450, 1500, 30, double.NaN), "best day 450 of 1,500 total is exactly 30%: nothing to cure");
        Eq(50, C.ConsistencyCure(450, 1450, 30, double.NaN), "best day 450 of 1,450 total: 50.00 more profit needed (450 / 0.3 = 1,500)");
        Eq(0, C.ConsistencyCure(450, 900, 30, 3000), "profit-target basis: 450 <= 30% of a 3,000 target, so nothing to cure");
        Eq(double.NaN, C.ConsistencyCure(450, 900, 0, 0), "rule off -> NaN");

        // ---- session locks end with the session
        DtnRiskState k = new DtnRiskState();
        r = Tick(k, man, t0, "2026-10-31", 50000, 50000, null, "2026-10-30");
        Check(r.ReleaseSessionLock && Count(r, DtnRiskEventKind.LockReleased) == 1, "a lock from the previous session is released at the new session");
        r = Tick(new DtnRiskState(), man, t0, "2026-10-31", 50000, 50000, null, "2026-10-31");
        Check(!r.ReleaseSessionLock, "a lock from this session stays");

        // ---- intraday trailing follows the live peak and ignores readings it cannot trust
        DtnRiskSettings intra = new DtnRiskSettings { Mode = C.ModeIntraday, Drawdown = 2500, StartingBalance = 50000, RiskBuffers = new List<double>() };
        DtnRiskState i = new DtnRiskState();
        foreach (double v in new[] { 50000.0, 50400.0, 50250.0, 50900.0, 50100.0 })
            Tick(i, intra, t0.AddSeconds(i.LastSeenUtc == DateTime.MinValue ? 0 : 1), "s", v);
        Check(i.HighWater == 50900 && i.TrailFloor == 48400, "intraday: peak 50,900 -> floor 48,400, and it stays when net liq falls back");
        Tick(i, intra, t0.AddSeconds(30), "s", 60000, 60000, "stale", null);
        Eq(50900, i.HighWater, "a frozen 60,000 reading does not raise the high-water mark");

        // ---- how the mark starts
        DtnRiskSettings near = Eod();
        r = Tick(new DtnRiskState(), near, t0, "s", 50040, 50040, null, null);
        Check(Count(r, DtnRiskEventKind.HighWaterStarted) == 0, "account 40 above StartingBalance: no warning (below max(50, 0.1%))");
        DtnRiskSettings noStart = Eod(); noStart.StartingBalance = double.NaN;
        DtnRiskState ns = new DtnRiskState();
        r = Tick(ns, noStart, t0, "s", 50800, 50300, null, null);
        Check(ns.HighWater == 50300 && Count(r, DtnRiskEventKind.HighWaterStarted) == 1 && !First(r, DtnRiskEventKind.HighWaterStarted).Loud && !First(r, DtnRiskEventKind.HighWaterStarted).Flag,
              "no StartingBalance: starts at the current closing balance (50,300, not net liq 50,800) with a quiet note");

        // ---- which floor applies
        string src;
        DtnRiskSettings pick = Eod(); pick.OverrideFloor = 49500;
        Eq(49500, C.EffectiveFloor(new DtnRiskState { TrailFloor = 48000 }, pick, out src), "a /floor setting beats the computed floor");
        Eq("/floor", src, "...and is labeled");
        Eq(48000, C.EffectiveFloor(new DtnRiskState { TrailFloor = 48000 }, Eod(), out src), "otherwise the trailing floor");
        Eq(48000, C.EffectiveFloor(null, new DtnRiskSettings { Mode = C.ModeStatic, StartingBalance = 50000, Drawdown = 2000 }, out src), "static: 50,000 - 2,000");
        Eq("config", (C.EffectiveFloor(null, new DtnRiskSettings { Mode = C.ModeManual, ManualFloor = 47000 }, out src) == 47000 ? src : "?"), "manual: RiskFloor, labeled config");
        Eq(double.NaN, C.EffectiveFloor(null, new DtnRiskSettings { Mode = C.ModeOff, OverrideFloor = 49000 }, out src), "off means off, even with a /floor setting");
    }

    // ---------------------------------------------------------------- mutation survivors, arming, risk log and replay
    private static void SurvivorsAndReadiness()
    {
        DateTime t0 = Fri.AddHours(15);

        // daily loss is measured from net liquidation at the session start, open profit included
        DtnRiskSettings dl = new DtnRiskSettings { Mode = C.ModeOff, DailyLimitText = "2%", DailyLossAlerts = new List<double> { 50, 75, 90 } };
        DtnRiskState d = new DtnRiskState();
        Tick(d, dl, t0, "s", 50400, 50000, null, null);
        Eq(50400, d.SessionStart, "daily-loss baseline = net liquidation 50,400 (it includes 400 of open profit), not the 50,000 balance");
        DtnRiskResult r = Tick(d, dl, t0.AddSeconds(1), "s", 49800, 49400, null, null);
        Check(Count(r, DtnRiskEventKind.DailyLoss) == 1 && First(r, DtnRiskEventKind.DailyLoss).A == 600 && Math.Abs(First(r, DtnRiskEventKind.DailyLoss).B - 1008) < 1e-9 && First(r, DtnRiskEventKind.DailyLoss).N == 50,
              "down 600 of a 2% limit measured on 50,400 (1,008) = 59%: the 50% alert");

        // an account that is already locked is not flattened again by the daily-loss or consistency rule
        DtnRiskSettings dlAuto = new DtnRiskSettings { Mode = C.ModeOff, DailyLimitText = "1000", DailyLossAlerts = new List<double> { 50 }, DailyLossAutoFlatten = true, ConfirmSeconds = 2 };
        DtnRiskState dLocked = new DtnRiskState();
        Tick(dLocked, dlAuto, t0, "s", 51000, 51000, null, "s");
        Tick(dLocked, dlAuto, t0.AddSeconds(1), "s", 49900, 49900, null, "s");
        Check(Tick(dLocked, dlAuto, t0.AddSeconds(5), "s", 49900, 49900, null, "s").FireReason == null && dLocked.DailyBreachSince == DateTime.MinValue,
              "daily loss at the limit on a locked account: no second flatten, and no clock started");
        DtnRiskState dOpen = new DtnRiskState();
        Tick(dOpen, dlAuto, t0, "s", 51000);
        Tick(dOpen, dlAuto, t0.AddSeconds(1), "s", 49900);
        Check(Tick(dOpen, dlAuto, t0.AddSeconds(3.1), "s", 49900).FireReason != null, "...while the same account unlocked does fire (so the guard is what stopped it)");
        DtnRiskSettings csAuto = new DtnRiskSettings { Mode = C.ModeOff, StartingBalance = 50000, ConsistencyPercent = 30, ConsistencyAlerts = new List<double> { 75 }, ConsistencyAutoFlatten = true, ConfirmSeconds = 2 };
        DtnRiskState cLocked = new DtnRiskState();
        Tick(cLocked, csAuto, t0, "s", 51000, 51000, null, "s");
        Tick(cLocked, csAuto, t0.AddSeconds(1), "s", 51440, 51440, null, "s");
        Check(Tick(cLocked, csAuto, t0.AddSeconds(5), "s", 51440, 51440, null, "s").FireReason == null, "consistency cap on a locked account: no second flatten");

        // a trailing floor never moves down, even when the rules change under it
        Eq(49000, C.TrailingFloor(50000, 2000, double.NaN, 49000), "TrailingFloor: a lower candidate (48,000) does not pull the floor below 49,000");
        Eq(48000, C.TrailingFloor(50000, 2000, double.NaN, double.NaN), "...but with no previous floor it is 48,000");
        DtnRiskSettings wider = Eod(); wider.Drawdown = 3000; wider.FloorLock = double.NaN;
        DtnRiskState keep = new DtnRiskState { Session = "s", HighWater = 51000, TrailFloor = 49000, LastBalance = 51000, LastNetLiq = 51000, SessionStartBalance = 51000, LastSeenUtc = t0 };
        Tick(keep, wider, t0.AddSeconds(1), "s", 51000);
        Eq(49000, keep.TrailFloor, "a bigger DrawdownAmount in the config does not lower a floor that already trailed up");

        // the flatten buffer is inclusive: exactly 150 above the floor counts, 150.01 does not
        DtnRiskSettings af = new DtnRiskSettings { Mode = C.ModeManual, ManualFloor = 48000, RiskBuffers = new List<double>(), RiskAutoFlatten = true, FlattenBuffer = 150, ConfirmSeconds = 2 };
        DtnRiskState edge = new DtnRiskState();
        Tick(edge, af, t0, "s", 50000);
        Tick(edge, af, t0.AddSeconds(1), "s", 48150);
        Check(edge.FloorBreachSince == t0.AddSeconds(1), "buffer exactly 150.00: the clock starts");
        DtnRiskState edge2 = new DtnRiskState();
        Tick(edge2, af, t0, "s", 50000);
        Tick(edge2, af, t0.AddSeconds(1), "s", 48150.01);
        Check(edge2.FloorBreachSince == DateTime.MinValue, "buffer 150.01: it does not");

        // arming: what would stop automatic protection from acting
        DtnRiskSettings ok = Eod(); ok.RiskAutoFlatten = true;
        Check(C.ArmingProblems(ok, true, true, 48000).Count == 0, "a complete setup has no arming problems");
        Check(C.ArmingProblems(ok, false, true, 48000).Contains("net liquidation cannot be read"), "unreadable net liquidation");
        Check(C.ArmingProblems(ok, true, false, 48000).Count == 1, "unreadable open P&L matters for the eod closing balance...");
        DtnRiskSettings netliq = Eod(); netliq.Basis = C.BasisNetLiq;
        Check(C.ArmingProblems(netliq, true, false, 48000).Count == 0, "...but not on the net-liquidation basis");
        DtnRiskSettings noDd = Eod(); noDd.Drawdown = double.NaN; noDd.RiskAutoFlatten = true;
        List<string> pr = C.ArmingProblems(noDd, true, true, double.NaN);
        Check(pr.Count == 2 && pr[0].Contains("DrawdownAmount") && pr[1].Contains("no floor"), "a trailing mode without DrawdownAmount: no floor, and auto-flatten has nothing to act on");
        DtnRiskSettings noDaily = new DtnRiskSettings { Mode = C.ModeOff, DailyLossAutoFlatten = true };
        Check(C.ArmingProblems(noDaily, true, true, double.NaN)[0].Contains("DailyLossLimit"), "DailyLossAutoFlatten without a limit");
        DtnRiskSettings noStart = new DtnRiskSettings { Mode = C.ModeOff, ConsistencyPercent = 30, ConsistencyAutoFlatten = true };
        Check(C.ArmingProblems(noStart, true, true, double.NaN)[0].Contains("StartingBalance"), "consistency without StartingBalance");
        Check(C.ArmingProblems(new DtnRiskSettings { Mode = C.ModeOff, ConsistencyAutoFlatten = true }, true, true, double.NaN)[0].Contains("ConsistencyPercent"), "ConsistencyAutoFlatten without a percentage");

        // which send errors are permanent
        Check(C.IsFatalSendError(401, "Unauthorized") && C.IsFatalSendError(403, "Forbidden: bot was blocked by the user") && C.IsFatalSendError(404, "Not Found"), "401, 403 and 404 are permanent");
        Check(C.IsFatalSendError(400, "Bad Request: chat not found") && C.IsFatalSendError(400, "Bad Request: PEER_ID_INVALID") && C.IsFatalSendError(400, "Bad Request: have no rights to send a message"), "400s about the chat are permanent");
        Check(!C.IsFatalSendError(400, "Bad Request: message is too long") && !C.IsFatalSendError(400, "Bad Request: can't parse entities") && !C.IsFatalSendError(429, "Too Many Requests") && !C.IsFatalSendError(500, "Internal"), "message-specific errors, rate limits and server errors are not");

        // the risk log round-trips, and a recorded session replays to the same decisions
        DtnRiskSettings full = new DtnRiskSettings
        {
            Mode = C.ModeEod, Basis = C.BasisBalance, Drawdown = 2000, FloorLock = 50100, StartingBalance = 50000, OverrideFloor = double.NaN, ManualFloor = 47500.5,
            ConsistencyPercent = 30, ConsistencyTarget = 3000, DailyLimitText = "2%", RiskBuffers = new List<double> { 300, 750, 1500 },
            DailyLossAlerts = new List<double> { 50, 75 }, ConsistencyAlerts = new List<double> { 90 }, RiskAutoFlatten = true, FlattenBuffer = 125.5,
            DailyLossAutoFlatten = true, ConsistencyAutoFlatten = false, ConfirmSeconds = 3, UnknownFlattenSeconds = 90, UnknownRemindSeconds = 45, UnmonitoredGapMinutes = 7, ClosingTolerance = 0.5
        };
        string text = C.SettingsToText(full);
        Eq(text, C.SettingsToText(C.SettingsFromText(text)), "settings survive a round trip through text");
        DtnRiskSettings back = C.SettingsFromText(text);
        Check(back.Mode == "eod" && back.Drawdown == 2000 && double.IsNaN(back.OverrideFloor) && back.ManualFloor == 47500.5 && back.RiskBuffers.Count == 3 && back.RiskAutoFlatten && !back.ConsistencyAutoFlatten && back.UnknownFlattenSeconds == 90 && back.DailyLimitText == "2%", "...and keep their values, including 'not set'");
        string row = C.FormatReadingRow(t0, "F1", new DtnRiskInput { Session = "2026-10-30", NetLiq = 50123.45, Balance = double.NaN, UnknownWhy = "feed, stale", LockedSession = null });
        DtnLogRow parsed = C.ParseLogRow(row);
        Check(parsed != null && !parsed.IsSettings && parsed.Account == "F1" && parsed.Utc == t0 && parsed.Input.NetLiq == 50123.45 && double.IsNaN(parsed.Input.Balance)
              && parsed.Input.UnknownWhy == "feed  stale" && parsed.Input.LockedSession == null && parsed.Input.Session == "2026-10-30", "a reading row round-trips (a comma in free text is replaced, NaN stays NaN)");
        Check(C.ParseLogRow("# comment") == null && C.ParseLogRow("") == null && C.ParseLogRow("R,not-a-time,F1,s,1,1,,") == null && C.ParseLogRow("X,2026-10-30T15:00:00.000,F1,a") == null, "comments and malformed rows are skipped");

        List<string> log = new List<string>
        {
            C.FormatSettingsRow(t0, "F1", new DtnRiskSettings { Mode = C.ModeManual, ManualFloor = 48000, RiskBuffers = C.ParseDollarList("1500,750,300"), UnknownFlattenSeconds = 120 }),
            "R,2026-10-30T15:00:00.000,F1,2026-10-30,50000,50000,,",
            "R,2026-10-30T15:00:10.000,F1,2026-10-30,50000,50000,market data stale,",
            "R,2026-10-30T15:00:40.000,F1,2026-10-30,50000,50000,market data stale,",
            "R,2026-10-30T15:01:10.000,F1,2026-10-30,50000,50000,market data stale,",
            "R,2026-10-30T15:02:10.000,F1,2026-10-30,50000,50000,market data stale,"
        };
        List<string> replay = C.ReplayRiskLog(log);
        int unknownEvents = 0;
        foreach (string line in replay) if (line.Contains(" Unknown loud")) unknownEvents++;
        Check(unknownEvents == 3 && replay.Contains("2026-10-30 15:00:10 F1 Unknown loud A=0.00 B=0.00 C=0.00 D=0.00 N=120 market data stale")
              && replay.Contains("2026-10-30 15:01:10 F1 Unknown loud A=60.00 B=0.00 C=0.00 D=0.00 N=120 market data stale")
              && replay.Contains("2026-10-30 15:02:10 F1 Unknown loud A=120.00 B=0.00 C=0.00 D=0.00 N=120 market data stale"),
              "replay of a recorded outage: first alert at 15:00:10, reminders at 15:01:10 (60 s) and 15:02:10 (120 s)");
        Check(replay.Contains("2026-10-30 15:02:10 F1 FIRE risk data unavailable for 120s while holding a position (market data stale)"), "...and the flatten fires at 15:02:10, 120 s after it began");
        Check(!replay.Exists(delegate(string line) { return line.Contains("15:02:09") || line.Contains("FIRE") && !line.StartsWith("2026-10-30 15:02:10"); }), "...and not a second earlier");
        List<string> gapLog = new List<string>
        {
            C.FormatSettingsRow(t0, "F1", Eod()),
            "R,2026-10-30T20:58:00.000,F1,2026-10-30,51000,51000,,",
            "R,2026-10-31T10:00:00.000,F1,2026-10-31,51400,51400,,"
        };
        List<string> gapReplay = C.ReplayRiskLog(gapLog);
        Check(gapReplay.Exists(delegate(string line) { return line.Contains("ClosingUncertain loud A=51,000.00 B=51,400.00"); }) && gapReplay[gapReplay.Count - 1].Contains("highWater=50,000.00"),
              "replay of a restart after the account changed: CLOSING UNCERTAIN, mark not moved (no ticks are invented across the gap)");
    }

    // ---------------------------------------------------------------- boundaries and degenerate inputs (found by the mutation sweep)
    private static void Boundaries()
    {
        DateTime t0 = Fri.AddHours(15);
        List<double> pct = new List<double> { 50, 75, 90 };

        // pure functions: missing, zero and infinite inputs
        Eq(49000, C.TrailingFloor(50000, double.NaN, double.NaN, 49000), "TrailingFloor: no drawdown keeps the previous floor");
        Eq(49000, C.TrailingFloor(50000, 0, double.NaN, 49000), "TrailingFloor: a drawdown of 0 is 'not set', not 'no room'");
        Eq(0, C.DailyLossLevel(100, 0, pct), "a limit of 0 is off");
        Eq(0, C.DailyLossLevel(500, double.NaN, pct), "no limit is off");
        Eq(0, C.DailyLossLevel(double.NaN, 1000, pct), "no loss reading is nothing");
        Eq(0, C.DailyLossLevel(0, 1000, pct), "no loss is nothing");
        Check(C.ValidateFloor(50000, 50000, 300, double.NaN, 0).Contains("at or above"), "a floor exactly at net liquidation is 'at or above'");
        Check(C.ValidateFloor(49700, 50000, 300, double.NaN, 0).Contains("only 300.00 below"), "a floor exactly at the smallest alert level is inside it");
        Eq(null, C.ValidateFloor(49699.99, 50000, 300, double.NaN, 0), "a cent outside it is fine");
        Eq(null, C.ValidateFloor(49000, 60000, 300, 48000, 1000), "a jump of exactly the limit needs no confirmation");
        Check(C.ValidateFloor(49000, 60000, 300, 47999, 1000).Contains("moves the floor by 1,001.00"), "a jump of 1,001 does");
        Eq(0, C.ConsistencyCure(0, -100, 30, double.NaN), "no best day: nothing to cure, even when total profit is negative");
        Eq(0, C.ConsistencyCure(500, 900, 50, 1000), "best day exactly at the share of the target: nothing to cure");
        Eq(600, C.ConsistencyCure(450, 900, 30, 0), "a profit target of 0 means no target");
        Eq(600, C.ConsistencyCure(450, 900, 30, double.NaN), "...and so does a blank one");
        Eq(double.NaN, C.ConsistencyCure(450, 900, 100, double.NaN), "100% is not a rule");
        Eq(double.NaN, C.ProfitShare(double.NaN, 1000), "no best day: no share");
        Eq(double.NaN, C.BalanceFromNetLiq(double.PositiveInfinity, 0), "an infinite net liquidation is not a balance");
        Eq(double.NaN, C.BalanceFromNetLiq(50000, double.NegativeInfinity), "an infinite open P&L is not a balance");
        Eq(double.NaN, C.ResolveAmountOrPercent("0%", 50000), "0% is off");
        Eq(double.NaN, C.ResolveAmountOrPercent("-5%", 50000), "a negative percent is off");
        Eq(double.NaN, C.ResolveAmountOrPercent("0", 50000), "a limit of 0 is off");
        Eq(50, C.ResolveAmountOrPercent("5%", 1000), "5% of 1,000");
        Eq(double.NaN, C.StaticFloor(50000, double.NaN), "static floor needs a drawdown");
        Eq(double.NaN, C.StaticFloor(50000, -5), "...a positive one");
        Check(C.IsFatalSendError(400, "Bad Request: user is deactivated") && C.IsFatalSendError(400, "Forbidden: bot was blocked by the user")
              && C.IsFatalSendError(400, "Bad Request: group chat was upgraded to a supergroup chat") && C.IsFatalSendError(400, "Bad Request: bot is not a member of the channel chat"), "each permanent 400 reason is recognized on its own");

        // arming: the consistency percentage is a rule only strictly between 0 and 100
        DtnRiskSettings c100 = new DtnRiskSettings { Mode = C.ModeOff, ConsistencyPercent = 100 };
        DtnRiskSettings c0 = new DtnRiskSettings { Mode = C.ModeOff, ConsistencyPercent = 0 };
        DtnRiskSettings c99 = new DtnRiskSettings { Mode = C.ModeOff, ConsistencyPercent = 99.9 };
        Check(C.ArmingProblems(c100, true, true, double.NaN).Count == 0 && C.ArmingProblems(c0, true, true, double.NaN).Count == 0, "100% and 0% are not consistency rules: no StartingBalance complaint");
        Check(C.ArmingProblems(c99, true, true, double.NaN).Count == 1, "99.9% is");
        DtnRiskSettings zeroDd = Eod(); zeroDd.Drawdown = 0;
        Check(C.ArmingProblems(zeroDd, true, true, 48000)[0].Contains("DrawdownAmount"), "a drawdown of 0 is reported as not set");
        DtnRiskSettings statNoStart = new DtnRiskSettings { Mode = C.ModeStatic, Drawdown = 2000 };
        Check(C.ArmingProblems(statNoStart, true, true, double.NaN)[0].Contains("StartingBalance"), "static without StartingBalance");
        Check(C.ArmingProblems(new DtnRiskSettings { Mode = C.ModeOff }, true, true, double.NaN).Count == 0, "mode off needs no drawdown");

        // the state machine: timing boundaries are inclusive, alerts are not repeated, recoveries are not invented
        DtnRiskSettings dlAuto = new DtnRiskSettings { Mode = C.ModeOff, DailyLimitText = "1000", DailyLossAlerts = new List<double> { 50 }, DailyLossAutoFlatten = true, ConfirmSeconds = 2 };
        DtnRiskState d1 = new DtnRiskState(); Tick(d1, dlAuto, t0, "s", 51000); Tick(d1, dlAuto, t0.AddSeconds(1), "s", 49900);
        Check(Tick(d1, dlAuto, t0.AddSeconds(2.999), "s", 49900).FireReason == null, "daily loss: 1.999 s at the limit does not fire");
        DtnRiskState d2 = new DtnRiskState(); Tick(d2, dlAuto, t0, "s", 51000); Tick(d2, dlAuto, t0.AddSeconds(1), "s", 49900);
        Check(Tick(d2, dlAuto, t0.AddSeconds(3), "s", 49900).FireReason != null, "...exactly 2.000 s does");
        DtnRiskSettings csAuto = new DtnRiskSettings { Mode = C.ModeOff, StartingBalance = 50000, ConsistencyPercent = 30, ConsistencyAlerts = new List<double> { 75 }, ConsistencyAutoFlatten = true, ConfirmSeconds = 2 };
        DtnRiskState c1 = new DtnRiskState(); Tick(c1, csAuto, t0, "s", 51000); Tick(c1, csAuto, t0.AddSeconds(1), "s", 51440);
        Check(Tick(c1, csAuto, t0.AddSeconds(2.999), "s", 51440).FireReason == null, "consistency: 1.999 s at the cap does not fire");
        DtnRiskState c2 = new DtnRiskState(); Tick(c2, csAuto, t0, "s", 51000); Tick(c2, csAuto, t0.AddSeconds(1), "s", 51440);
        Check(Tick(c2, csAuto, t0.AddSeconds(3), "s", 51440).FireReason != null, "...exactly 2.000 s does");

        DtnRiskSettings dl = new DtnRiskSettings { Mode = C.ModeOff, DailyLimitText = "1000", DailyLossAlerts = new List<double> { 50, 75 } };
        DtnRiskState d3 = new DtnRiskState(); Tick(d3, dl, t0, "s", 51000);
        Check(Tick(d3, dl, t0.AddSeconds(1), "s", 50400).Events.Count == 1, "daily loss 600: one alert");
        Check(Tick(d3, dl, t0.AddSeconds(2), "s", 50400).Events.Count == 0, "...not repeated while it stays there");
        Check(Tick(new DtnRiskState(), dl, t0, "s", 51000).Events.Count == 0 && Tick(d3, dl, t0.AddSeconds(3), "s", 50400).Events.Count == 0, "no loss and nothing to recover from: no recovery message");
        DtnRiskState d4 = new DtnRiskState(); Tick(d4, dl, t0, "s", 51000);
        Check(Tick(d4, dl, t0.AddSeconds(1), "s", 51000).Events.Count == 0 && Tick(d4, dl, t0.AddSeconds(2), "s", 51200).Events.Count == 0, "a flat or winning day never sends 'back under the levels'");
        DtnRiskSettings cs = new DtnRiskSettings { Mode = C.ModeOff, StartingBalance = 50000, ConsistencyPercent = 30, ConsistencyAlerts = new List<double> { 75 } };
        DtnRiskState c3 = new DtnRiskState(); Tick(c3, cs, t0, "s", 51000);
        Check(Tick(c3, cs, t0.AddSeconds(1), "s", 51330).Events.Count == 1 && Tick(c3, cs, t0.AddSeconds(2), "s", 51330).Events.Count == 0, "consistency 75% is alerted once");
        DtnRiskState c4 = new DtnRiskState(); Tick(c4, cs, t0, "s", 51000);
        Check(Tick(c4, cs, t0.AddSeconds(1), "s", 51000).Events.Count == 0 && Tick(c4, cs, t0.AddSeconds(2), "s", 50900).Events.Count == 0, "no profit today: no 'back under the levels'");
        foreach (double pc in new[] { 0.0, 100.0 })
        {
            DtnRiskSettings off = new DtnRiskSettings { Mode = C.ModeOff, StartingBalance = 50000, ConsistencyPercent = pc, ConsistencyAlerts = new List<double> { 1 } };
            DtnRiskState o = new DtnRiskState(); Tick(o, off, t0, "s", 51000);
            Check(Tick(o, off, t0.AddSeconds(1), "s", 55000).Events.Count == 0, "ConsistencyPercent " + pc + " is not a rule: no alerts");
        }

        // closing balance: the 10-minute gap and the 1.00 tolerance are exact
        DtnRiskSettings eod = Eod();
        DateTime close = Fri.AddHours(21).AddMinutes(1);
        Func<TimeSpan, double, DtnRiskResult> restart = delegate(TimeSpan unwatched, double balanceNow)
        {
            DtnRiskState s = new DtnRiskState { Session = "2026-10-30", HighWater = 51000, TrailFloor = 49000, LastBalance = 51000, LastNetLiq = 51000, SessionStartBalance = 50500, LastSeenUtc = close - unwatched };
            return Tick(s, eod, close, "2026-10-31", balanceNow, balanceNow, null, null);
        };
        Check(Count(restart(TimeSpan.FromMinutes(10), 51400), DtnRiskEventKind.ClosingUncertain) == 0, "unwatched for exactly 10 minutes is not yet a gap: the last reading is the close");
        Check(Count(restart(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)), 51400), DtnRiskEventKind.ClosingUncertain) == 1, "10 minutes 1 second is");
        Check(Count(restart(TimeSpan.FromHours(14), 51001), DtnRiskEventKind.ClosingUncertain) == 0, "a gap with the balance 1.00 off is within tolerance");
        Check(Count(restart(TimeSpan.FromHours(14), 51001.01), DtnRiskEventKind.ClosingUncertain) == 1, "...1.01 off is not");
        DtnRiskSettings nl = Eod(); nl.Basis = C.BasisNetLiq;
        Func<double, DtnRiskResult> restartNl = delegate(double netLiqNow)
        {
            DtnRiskState s = new DtnRiskState { Session = "2026-10-30", HighWater = 51000, TrailFloor = 49000, LastBalance = 50800, LastNetLiq = 51000, SessionStartBalance = 50500, LastSeenUtc = close - TimeSpan.FromHours(14) };
            return Tick(s, nl, close, "2026-10-31", netLiqNow, 50800, null, null);
        };
        Check(Count(restartNl(51000.5), DtnRiskEventKind.ClosingUncertain) == 0 && Count(restartNl(51400), DtnRiskEventKind.ClosingUncertain) == 1 && Count(restartNl(50600), DtnRiskEventKind.ClosingUncertain) == 1,
              "net liquidation basis: judged on net liquidation (50,800 balance unchanged does not matter), in both directions");
        DtnRiskState zeroDay = new DtnRiskState { Session = "2026-10-30", HighWater = 51000, TrailFloor = 49000, LastBalance = 51000, LastNetLiq = 51000, SessionStartBalance = 51000, LastSeenUtc = close.AddMinutes(-1) };
        Tick(zeroDay, eod, close, "2026-10-31", 51000);
        Check(double.IsNaN(zeroDay.BestDay), "a day with zero profit does not become a best day of 0");
        DtnRiskSettings consistencyOnly = new DtnRiskSettings { Mode = C.ModeOff, StartingBalance = 50000, ConsistencyPercent = 30 };
        DtnRiskState co = new DtnRiskState { Session = "2026-10-30", LastBalance = 51000, LastNetLiq = 51000, SessionStartBalance = 50500, LastSeenUtc = close - TimeSpan.FromHours(14) };
        Check(Count(Tick(co, consistencyOnly, close, "2026-10-31", 51400), DtnRiskEventKind.ClosingUncertain) == 1, "consistency alone also needs a trustworthy close: uncertain close is reported");
        DtnRiskSettings neither = new DtnRiskSettings { Mode = C.ModeOff };
        DtnRiskState no = new DtnRiskState { Session = "2026-10-30", LastBalance = 51000, LastNetLiq = 51000, SessionStartBalance = 50500, LastSeenUtc = close - TimeSpan.FromHours(14) };
        Check(Count(Tick(no, neither, close, "2026-10-31", 51400), DtnRiskEventKind.ClosingUncertain) == 0, "...and nothing is reported when no rule needs it");

        // how a trailing mark starts
        DtnRiskSettings big = Eod(); big.StartingBalance = 100000; big.FloorLock = double.NaN;
        Check(Count(Tick(new DtnRiskState(), big, t0, "s", 100060, 100060, null, null), DtnRiskEventKind.HighWaterStarted) == 0, "60 above a 100,000 start is within 0.1%: no warning");
        Check(Count(Tick(new DtnRiskState(), big, t0, "s", 100100, 100100, null, null), DtnRiskEventKind.HighWaterStarted) == 0, "exactly 0.1% (100): still no warning");
        Check(Count(Tick(new DtnRiskState(), big, t0, "s", 100101, 100101, null, null), DtnRiskEventKind.HighWaterStarted) == 1, "100.01 above: warning");
        DtnRiskSettings eodNl = Eod(); eodNl.Basis = C.BasisNetLiq; eodNl.StartingBalance = double.NaN;
        DtnRiskState startNl = new DtnRiskState(); Tick(startNl, eodNl, t0, "s", 50800, 50300, null, null);
        Eq(50800, startNl.HighWater, "no StartingBalance on the net-liquidation basis: starts at net liquidation, not the balance");
        DtnRiskSettings intraNoStart = new DtnRiskSettings { Mode = C.ModeIntraday, Drawdown = 2500 };
        DtnRiskState startIntra = new DtnRiskState(); Tick(startIntra, intraNoStart, t0, "s", 50800, 50300, null, null);
        Eq(50800, startIntra.HighWater, "intraday starts at net liquidation too");
        DtnRiskState steady = new DtnRiskState();
        Check(Tick(steady, eod, t0, "s", 50000).Dirty && !Tick(steady, eod, t0.AddSeconds(1), "s", 50000).Dirty, "state is marked changed once, then not again while nothing changes");
        DtnRiskSettings atFloor = new DtnRiskSettings { Mode = C.ModeManual, ManualFloor = 48000, RiskBuffers = new List<double>(), RiskAutoFlatten = true, FlattenBuffer = 150, ConfirmSeconds = 2 };
        DtnRiskState exact = new DtnRiskState(); Tick(exact, atFloor, t0, "s", 48000);
        Check(Tick(exact, atFloor, t0.AddSeconds(2), "s", 48000).FireReason == "net liq 48,000.00 is 0.00 below the floor 48,000.00", "net liquidation exactly at the floor is reported as 0.00 below it");
    }

    private static void BoundariesTwo()
    {
        DateTime t0 = Fri.AddHours(15);
        DateTime close = Fri.AddHours(21).AddMinutes(1);

        Eq(double.NaN, C.ConsistencyCure(double.NaN, 900, 30, double.NaN), "no best day: the cure is unknown, not zero");
        Eq(double.NaN, C.ConsistencyCure(450, double.NaN, 30, double.NaN), "no total profit: the cure is unknown, not zero");

        // a percentage of 0 or 100 is not a rule, and must not make an uncertain close look like it matters
        foreach (double pc in new[] { 0.0, 100.0 })
        {
            DtnRiskSettings off = new DtnRiskSettings { Mode = C.ModeOff, StartingBalance = 50000, ConsistencyPercent = pc };
            DtnRiskState st = new DtnRiskState { Session = "2026-10-30", LastBalance = 51000, LastNetLiq = 51000, SessionStartBalance = 50500, LastSeenUtc = close - TimeSpan.FromHours(14) };
            Check(Count(Tick(st, off, close, "2026-10-31", 51400), DtnRiskEventKind.ClosingUncertain) == 0, "ConsistencyPercent " + pc + ": an account change across a gap is not reported (no rule needs the close)");
        }

        // consistency needs StartingBalance, even with a profit target: the prior profit is unknown without it
        DtnRiskSettings noStart = new DtnRiskSettings { Mode = C.ModeOff, ConsistencyPercent = 50, ConsistencyTarget = 3000, ConsistencyAlerts = new List<double> { 75 } };
        DtnRiskState ns = new DtnRiskState(); Tick(ns, noStart, t0, "s", 51000);
        Check(Tick(ns, noStart, t0.AddSeconds(1), "s", 53000).Events.Count == 0, "no StartingBalance: no consistency alerts, even though the target alone would give a cap");

        // net liquidation basis: the tolerance is exactly 1.00 here too
        DtnRiskSettings nl = Eod(); nl.Basis = C.BasisNetLiq;
        Func<double, int> uncertain = delegate(double netLiqNow)
        {
            DtnRiskState s = new DtnRiskState { Session = "2026-10-30", HighWater = 51000, TrailFloor = 49000, LastBalance = 51000, LastNetLiq = 51000, SessionStartBalance = 50500, LastSeenUtc = close - TimeSpan.FromHours(14) };
            return Count(Tick(s, nl, close, "2026-10-31", netLiqNow, 51000, null, null), DtnRiskEventKind.ClosingUncertain);
        };
        Check(uncertain(51001) == 0 && uncertain(50999) == 0 && uncertain(51001.01) == 1 && uncertain(50998.99) == 1, "net liquidation basis: 1.00 off either way is tolerated, 1.01 is not");

        // the intraday mark moves only on a strictly higher reading
        DtnRiskSettings intra = new DtnRiskSettings { Mode = C.ModeIntraday, Drawdown = 2500 };
        DtnRiskState i = new DtnRiskState();
        Tick(i, intra, t0, "s", 50000);
        Check(!Tick(i, intra, t0.AddSeconds(1), "s", 50000).Dirty, "intraday: the same reading is not a change");
        Check(Tick(i, intra, t0.AddSeconds(2), "s", 50000.01).Dirty && i.HighWater == 50000.01, "...a higher one is");

        // which events are worth a second channel
        DtnRiskSettings withBuffers = new DtnRiskSettings { RiskBuffers = C.ParseDollarList("1500,750,300") };
        Func<DtnRiskEventKind, double, int, bool> critical = delegate(DtnRiskEventKind kind, double a, int n)
        {
            return C.IsCriticalEvent(new DtnRiskEvent { Kind = kind, A = a, N = n, D = a }, withBuffers);
        };
        Check(critical(DtnRiskEventKind.Unknown, 0, 0) && !critical(DtnRiskEventKind.Unknown, 60, 0), "Risk UNKNOWN: the first alert is critical, the reminders are not");
        Check(!critical(DtnRiskEventKind.RiskBuffer, 1500, 0) && !critical(DtnRiskEventKind.RiskBuffer, 750, 0) && critical(DtnRiskEventKind.RiskBuffer, 300, 0) && critical(DtnRiskEventKind.RiskBuffer, 0, 0),
              "floor buffer: only the tightest configured level (300) and below it");
        Check(C.IsCriticalEvent(new DtnRiskEvent { Kind = DtnRiskEventKind.RiskBuffer, D = 0 }, new DtnRiskSettings()), "with no levels configured, below the floor is critical");
        Check(!C.IsCriticalEvent(new DtnRiskEvent { Kind = DtnRiskEventKind.RiskBuffer, D = 100 }, new DtnRiskSettings()), "...and nothing above the floor is, when no levels are configured");
        Check(C.IsCriticalEvent(new DtnRiskEvent { Kind = DtnRiskEventKind.RiskBuffer, D = 750 }, new DtnRiskSettings { RiskBuffers = C.ParseDollarList("750") }), "a single configured level is the tightest");
        Check(!critical(DtnRiskEventKind.DailyLoss, 0, 90) && critical(DtnRiskEventKind.DailyLoss, 0, 100), "daily loss: only the limit itself");
        Check(!critical(DtnRiskEventKind.Consistency, 0, 100) && !critical(DtnRiskEventKind.ClosingUncertain, 0, 0) && !critical(DtnRiskEventKind.DataBack, 0, 0)
              && !critical(DtnRiskEventKind.FloorTrailed, 0, 0) && !critical(DtnRiskEventKind.LockReleased, 0, 0) && !critical(DtnRiskEventKind.HighWaterStarted, 0, 0), "everything else is routine");
    }

    // ---------------------------------------------------------------- what the second channel carries, and the open P&L watch
    private static void BackupAndOpenPnl()
    {
        Eq("...001", C.MaskAccount("MFFUEVREOD729960001"), "an account is shown by its last three characters");
        Eq("...003", C.MaskAccount(" MFFUEVREOD729960003 "), "...ignoring spaces");
        Eq("F1", C.MaskAccount("F1"), "a short name is shown as is");
        Eq("abc", C.MaskAccount("abc"), "three characters: as is");
        Eq("...bcd", C.MaskAccount("abcd"), "four: the last three");
        Eq("", C.MaskAccount(null), "no name, nothing");

        string a = C.MaskAccount("MFFUEVREOD729960001");
        string unknown = C.BackupMinimalText(new DtnRiskEvent { Kind = DtnRiskEventKind.Unknown, A = 0 }, a);
        string near = C.BackupMinimalText(new DtnRiskEvent { Kind = DtnRiskEventKind.RiskBuffer, D = 300, A = 48200.55, B = 48000, C = 200.55 }, a);
        string below = C.BackupMinimalText(new DtnRiskEvent { Kind = DtnRiskEventKind.RiskBuffer, D = 0, A = 47900.12, B = 48000, C = -99.88 }, a);
        string daily = C.BackupMinimalText(new DtnRiskEvent { Kind = DtnRiskEventKind.DailyLoss, A = 1012.5, B = 1000, N = 100 }, a);
        string other = C.BackupMinimalText(new DtnRiskEvent { Kind = DtnRiskEventKind.Consistency }, a);
        Eq("RISK UNKNOWN: account ...001 holds a position and its risk data is unavailable. Open NinjaTrader now.", unknown, "minimal text: risk unknown");
        Eq("NEAR FLOOR: account ...001. Open NinjaTrader now.", near, "minimal text: near the floor");
        Eq("BELOW FLOOR: account ...001. Open NinjaTrader now.", below, "minimal text: below the floor");
        Eq("DAILY LOSS LIMIT reached: account ...001. Open NinjaTrader now.", daily, "minimal text: daily loss limit");
        Eq("ALERT: account ...001. Open NinjaTrader now.", other, "anything else is a bare alert");
        foreach (string text in new[] { unknown, near, below, daily, other })
            Check(!System.Text.RegularExpressions.Regex.IsMatch(text.Replace("...001", string.Empty), "[0-9]"), "no amount or other number in: " + text);

        // open P&L reading exactly 0.00 while a position is held and net liquidation moves
        DateTime t = Fri.AddHours(15);
        DtnOpenPnlWatch w = new DtnOpenPnlWatch();
        Check(!w.Observe(t, true, 0.0, 50000), "t=0: starts watching");
        Check(!w.Observe(t.AddSeconds(10), true, 0.0, 50002), "10 s, net liq +2: not yet");
        Check(!w.Observe(t.AddSeconds(20), true, 0.0, 50006), "20 s: not yet");
        Check(!w.Observe(t.AddSeconds(29.9), true, 0.0, 50010), "29.9 s: the window is not over");
        Check(w.Observe(t.AddSeconds(30), true, 0.0, 50010), "30 s, net liq moved 10 in three steps with P&L still exactly 0.00: suspect");
        Check(w.Suspect && w.Move == 10, "...and the move is known");
        Check(!w.Observe(t.AddSeconds(40), true, 0.0, 50020), "it warns once per episode");
        Check(!w.Observe(t.AddSeconds(50), true, 12.5, 50020) && !w.Suspect, "a real open P&L ends the episode");
        Check(!w.Observe(t.AddSeconds(60), true, 0.0, 50000) && !w.Observe(t.AddSeconds(70), true, 0.0, 50003) && !w.Observe(t.AddSeconds(80), true, 0.0, 50006), "a new episode starts its window again");
        Check(w.Observe(t.AddSeconds(90), true, 0.0, 50009), "...and can warn again");

        DtnOpenPnlWatch fee = new DtnOpenPnlWatch();
        fee.Observe(t, true, 0.0, 50000);
        fee.Observe(t.AddSeconds(5), true, 0.0, 49997.5);
        Check(!fee.Observe(t.AddSeconds(60), true, 0.0, 49997.5), "one commission step and then nothing: not suspect");
        DtnOpenPnlWatch small = new DtnOpenPnlWatch();
        small.Observe(t, true, 0.0, 50000); small.Observe(t.AddSeconds(10), true, 0.0, 50000.5); small.Observe(t.AddSeconds(20), true, 0.0, 50001); small.Observe(t.AddSeconds(25), true, 0.0, 50002.5);
        Check(!small.Observe(t.AddSeconds(40), true, 0.0, 50002.5), "four changes but a range of only 2.50: not suspect");
        DtnOpenPnlWatch flat = new DtnOpenPnlWatch();
        Check(!flat.Observe(t, false, 0.0, 50000) && !flat.Observe(t.AddSeconds(60), false, 0.0, 50100), "no position: nothing to check");
        DtnOpenPnlWatch unread = new DtnOpenPnlWatch();
        unread.Observe(t, true, 0.0, 50000);
        Check(!unread.Observe(t.AddSeconds(40), true, double.NaN, 50020), "an unreadable P&L is a different problem and resets this one");
        DtnOpenPnlWatch exact = new DtnOpenPnlWatch();
        exact.Observe(t, true, 0.0, 50000); exact.Observe(t.AddSeconds(10), true, 0.0, 50001); exact.Observe(t.AddSeconds(20), true, 0.0, 50003);
        Check(exact.Observe(t.AddSeconds(30), true, 0.0, 50005), "a range of exactly 5.00 over exactly three changes is enough");
        DtnOpenPnlWatch jump = new DtnOpenPnlWatch();
        jump.Observe(t, true, 0.0, 50000); jump.Observe(t.AddSeconds(5), true, 0.0, 50010);
        bool warned = false;
        for (int s = 10; s <= 100; s += 5) warned |= jump.Observe(t.AddSeconds(s), true, 0.0, 50010);
        Check(!warned, "one jump of 10 and then a steady reading is a single event, not a market moving");
        DtnOpenPnlWatch still = new DtnOpenPnlWatch();
        still.Observe(t, true, 0.0, 50000);
        Check(!still.Observe(t.AddSeconds(120), true, 0.0, 50000), "a quiet market (net liq not moving) with P&L 0.00 is normal");
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
