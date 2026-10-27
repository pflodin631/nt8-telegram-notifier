// Replays a recorded risk log (DirectTelegramNotifier-risk.csv) through the same risk logic the add-on uses.
//   Replay <log.csv> [--account NAME] [--expect expected.txt]
// Prints the timeline of alerts and flattens the log would have produced. With --expect it compares and exits non-zero on any difference.
// Compiled together with DirectTelegramNotifier.Core.cs (see tests/run-replay.sh): no NinjaTrader needed.
using System;
using System.Collections.Generic;
using System.IO;
using NinjaTrader.NinjaScript.AddOns;

internal static class Replay
{
    private static int Main(string[] args)
    {
        string path = null, account = null, expect = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--account" && i + 1 < args.Length) account = args[++i];
            else if (args[i] == "--expect" && i + 1 < args.Length) expect = args[++i];
            else path = args[i];
        }
        if (path == null || !File.Exists(path))
        {
            Console.WriteLine("usage: Replay <risk-log.csv> [--account NAME] [--expect expected.txt]");
            return 2;
        }

        List<string> lines = new List<string>();
        foreach (string line in File.ReadAllLines(path))
            if (account == null || line.Contains("," + account + ",") || line.StartsWith("#", StringComparison.Ordinal))
                lines.Add(line);
        List<string> timeline = DirectTelegramNotifierCore.ReplayRiskLog(lines);

        if (expect == null)
        {
            foreach (string line in timeline)
                Console.WriteLine(line);
            return 0;
        }

        string[] wanted = File.ReadAllLines(expect);
        int differences = 0;
        for (int i = 0; i < Math.Max(wanted.Length, timeline.Count); i++)
        {
            string w = i < wanted.Length ? wanted[i] : "(nothing)";
            string g = i < timeline.Count ? timeline[i] : "(nothing)";
            if (w != g)
            {
                if (differences++ < 5)
                {
                    Console.WriteLine("line " + (i + 1) + " differs:");
                    Console.WriteLine("  expected: " + w);
                    Console.WriteLine("  replayed: " + g);
                }
            }
        }
        if (differences > 0)
            Console.WriteLine(differences + " line(s) differ.");
        return differences == 0 ? 0 : 1;
    }
}
