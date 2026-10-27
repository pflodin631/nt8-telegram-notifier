#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
#endregion

// ============================================================================================
//  DirectTelegramNotifier - pure logic, no NinjaTrader types.
//
//  Everything here is deterministic and unit-tested outside NinjaTrader (tests/CoreTests.cs):
//  config-key checks, risk levels, drawdown floors, daily loss, floor validation, quiet windows,
//  session keys, fill price comparison, command parsing and the JSON reader.
//  Written in C# 5 so it compiles in every NinjaTrader 8 version and with Windows' built-in csc.
// ============================================================================================

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class DirectTelegramNotifierCore
    {
        #region Config keys

        // Every key the add-on reads. Anything else in the config file is reported as unrecognized.
        public static readonly string[] KnownKeys = new string[]
        {
            "BotToken", "AuthorizedChatID", "AccountName",
            "EnableWatchdog", "EnableExecutions", "EnableRemoteCommands", "EnableOrderRejectAlerts",
            "RunawayMaxOrders", "RunawayWindowSec", "UseGlobalFlattenEverything",
            "EodReportTime", "EodTimeZoneId", "EstimatedCommissionPerSide",
            "HeartbeatUrl", "HeartbeatIntervalSec", "RestartGapThresholdSec",
            "EnableStrategyWatch", "StaleDataSeconds", "StaleDataSchedule", "StaleDataQuietWindows", "StaleDataTimeZone",
            "StaleDataInstruments", "EnableAlertLog",
            "LeaderAccount", "FollowerRatio", "MismatchSeconds",
            "ProtectiveCheckSeconds", "OrphanOrderSeconds", "OrphanOrderNames", "StuckOrderSeconds", "StuckOrderStates",
            "MaxPositionContracts",
            "DrawdownMode", "RiskFloor", "StartingBalance", "DrawdownAmount", "FloorLock",
            "RiskBufferAlerts", "RiskAutoFlatten", "RiskFlattenBuffer", "RiskConfirmSeconds", "FloorJumpConfirm",
            "DailyLossLimit", "DailyLossAlerts", "DailyLossAutoFlatten",
            "FlattenHoldSeconds", "FillGroupMs",
            "CpuAlertPercent", "CpuAlertSeconds", "MemoryAlertMB", "DiskAlertMB",
            "FlattenConfirm", "SilentRoutineAlerts", "EnableFillCsv"
        };

        // Keys that may also be given per account as  Key:AccountName=value
        public static readonly string[] PerAccountKeys = new string[]
        {
            "DrawdownMode", "RiskFloor", "StartingBalance", "DrawdownAmount", "FloorLock", "DailyLossLimit"
        };

        public static bool IsKnownKey(string key)
        {
            foreach (string k in KnownKeys)
            {
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool IsPerAccountKey(string key)
        {
            foreach (string k in PerAccountKeys)
            {
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Returns one line per unrecognized key, with a suggestion when a known key is close: "RiskAutoFlaten (did you mean RiskAutoFlatten?)".
        public static List<string> FindUnknownKeys(IEnumerable<string> keys)
        {
            List<string> unknown = new List<string>();
            foreach (string raw in keys)
            {
                string key = (raw ?? string.Empty).Trim();
                if (key.Length == 0)
                    continue;

                int colon = key.IndexOf(':');
                string baseKey = colon > 0 ? key.Substring(0, colon) : key;
                if (colon > 0 ? IsPerAccountKey(baseKey) : IsKnownKey(baseKey))
                    continue;

                string candidates = colon > 0 ? string.Join("|", PerAccountKeys) : string.Join("|", KnownKeys);
                string best = null;
                int bestDistance = int.MaxValue;
                foreach (string k in candidates.Split('|'))
                {
                    int dist = Levenshtein(baseKey.ToLowerInvariant(), k.ToLowerInvariant());
                    if (dist < bestDistance)
                    {
                        bestDistance = dist;
                        best = k;
                    }
                }
                int allowed = Math.Max(2, baseKey.Length / 4);
                if (best != null && bestDistance <= allowed)
                    unknown.Add(key + " (did you mean " + (colon > 0 ? best + key.Substring(colon) : best) + "?)");
                else if (colon > 0 && IsKnownKey(baseKey))
                    unknown.Add(key + " (" + baseKey + " cannot be set per account)");
                else
                    unknown.Add(key);
            }
            return unknown;
        }

        public static int Levenshtein(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[b.Length];
        }

        #endregion

        #region Numbers

        public static double ParseOptionalDouble(string text)
        {
            double v;
            if (!string.IsNullOrWhiteSpace(text)
                && double.TryParse(text.Trim().TrimStart('$').Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return double.NaN;
        }

        // "1500, 750;300" -> ascending list of positive amounts.
        public static List<double> ParseDollarList(string text)
        {
            List<double> levels = new List<double>();
            if (string.IsNullOrWhiteSpace(text))
                return levels;
            foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v = ParseOptionalDouble(part);
                if (!double.IsNaN(v) && v > 0)
                    levels.Add(v);
            }
            levels.Sort();
            return levels;
        }

        // "1000" -> 1000 dollars;  "2%" -> 2 percent of baseValue.  Blank or invalid -> NaN.
        public static double ResolveAmountOrPercent(string text, double baseValue)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0)
                return double.NaN;
            if (t.EndsWith("%", StringComparison.Ordinal))
            {
                double pct = ParseOptionalDouble(t.Substring(0, t.Length - 1));
                if (double.IsNaN(pct) || pct <= 0 || double.IsNaN(baseValue))
                    return double.NaN;
                return baseValue * pct / 100.0;
            }
            double v = ParseOptionalDouble(t);
            return v > 0 ? v : double.NaN;
        }

        public static string N2(double value)
        {
            return value.ToString("N2", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Risk: floors, levels, daily loss

        public const string ModeOff = "off";
        public const string ModeManual = "manual";
        public const string ModeStatic = "static";
        public const string ModeEod = "eod";
        public const string ModeIntraday = "intraday";

        // Blank means manual. Returns null for anything unrecognized.
        public static string NormalizeMode(string text)
        {
            string t = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (t.Length == 0)
                return ModeManual;
            if (t == ModeOff || t == ModeManual || t == ModeStatic || t == ModeEod || t == ModeIntraday)
                return t;
            if (t == "endofday" || t == "end-of-day" || t == "eod-trailing" || t == "eodtrailing")
                return ModeEod;
            if (t == "intraday-trailing" || t == "intradaytrailing" || t == "realtime")
                return ModeIntraday;
            return null;
        }

        // The tightest alert level the buffer has reached: 0 = at or below the floor, +infinity = no level reached.
        public static double RiskLevel(double buffer, IList<double> ascendingLevels)
        {
            if (double.IsNaN(buffer))
                return double.PositiveInfinity;
            if (buffer <= 0)
                return 0.0;
            if (ascendingLevels != null)
            {
                foreach (double level in ascendingLevels)
                {
                    if (buffer <= level)
                        return level;
                }
            }
            return double.PositiveInfinity;
        }

        public static double NextHighWater(double highWater, double observed)
        {
            if (double.IsNaN(observed))
                return highWater;
            if (double.IsNaN(highWater))
                return observed;
            return Math.Max(highWater, observed);
        }

        // Static drawdown: the floor never moves.
        public static double StaticFloor(double startingBalance, double drawdown)
        {
            if (double.IsNaN(startingBalance) || double.IsNaN(drawdown) || drawdown <= 0)
                return double.NaN;
            return startingBalance - drawdown;
        }

        // Trailing drawdown (end-of-day or intraday, depending on which high-water mark is passed in):
        // floor = high-water - drawdown, capped at the lock level, and it never moves down.
        public static double TrailingFloor(double highWater, double drawdown, double lockLevel, double previousFloor)
        {
            if (double.IsNaN(highWater) || double.IsNaN(drawdown) || drawdown <= 0)
                return previousFloor;
            double candidate = highWater - drawdown;
            if (!double.IsNaN(lockLevel))
                candidate = Math.Min(candidate, lockLevel);
            if (double.IsNaN(previousFloor))
                return candidate;
            return Math.Max(previousFloor, candidate);
        }

        // Highest alert percentage the day's loss has reached: 0 = none, 100 = the limit itself.
        public static int DailyLossLevel(double loss, double limit, IList<double> percents)
        {
            if (double.IsNaN(loss) || double.IsNaN(limit) || limit <= 0 || loss <= 0)
                return 0;
            if (loss >= limit)
                return 100;
            int reached = 0;
            if (percents != null)
            {
                foreach (double p in percents)
                {
                    if (p > 0 && p < 100 && loss >= limit * p / 100.0)
                        reached = Math.Max(reached, (int)Math.Round(p));
                }
            }
            return reached;
        }

        // Why a new floor needs confirmation, or null when it looks sane.
        public static string ValidateFloor(double newFloor, double netLiq, double smallestLevel, double previousFloor, double jumpLimit)
        {
            List<string> reasons = new List<string>();
            if (!double.IsNaN(netLiq))
            {
                if (newFloor >= netLiq)
                    reasons.Add("it is at or above the current net liquidation (" + N2(netLiq) + ")");
                else if (newFloor >= netLiq - Math.Max(0.0, smallestLevel))
                    reasons.Add("it is only " + N2(netLiq - newFloor) + " below the current net liquidation (" + N2(netLiq) + "), inside your alert levels");
            }
            if (jumpLimit > 0 && !double.IsNaN(previousFloor) && Math.Abs(newFloor - previousFloor) > jumpLimit)
                reasons.Add("it moves the floor by " + N2(Math.Abs(newFloor - previousFloor)) + " (from " + N2(previousFloor) + ")");
            return reasons.Count == 0 ? null : string.Join("; ", reasons.ToArray());
        }

        // The session a moment belongs to, named by the date its end-of-day boundary falls on.
        public static string SessionKey(DateTime localInEodZone, TimeSpan eodTimeOfDay)
        {
            DateTime day = localInEodZone.TimeOfDay < eodTimeOfDay ? localInEodZone.Date : localInEodZone.Date.AddDays(1);
            return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Quiet windows (market schedules)

        public sealed class QuietWindow
        {
            public readonly bool[] Days = new bool[7];        // indexed by DayOfWeek
            public TimeSpan Start;
            public TimeSpan End;
        }

        // CME equity-index style week, in US Eastern time.
        public const string CmeQuietWindows = "Mon-Thu 17:00-18:00; Fri 17:00-24:00; Sat 00:00-24:00; Sun 00:00-18:00";

        private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        // "Mon-Thu 17:00-18:00; Fri 17:00-24:00; Daily 09:00-09:05". Windows may not cross midnight: split them.
        public static List<QuietWindow> ParseQuietWindows(string spec, out string error)
        {
            error = null;
            List<QuietWindow> windows = new List<QuietWindow>();
            if (string.IsNullOrWhiteSpace(spec))
                return windows;

            foreach (string rawEntry in spec.Split(';'))
            {
                string entry = rawEntry.Trim();
                if (entry.Length == 0)
                    continue;

                string[] parts = entry.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    error = "'" + entry + "' should look like 'Mon-Thu 17:00-18:00'";
                    return new List<QuietWindow>();
                }

                QuietWindow w = new QuietWindow();
                if (!ParseDays(parts[0], w.Days))
                {
                    error = "unknown days '" + parts[0] + "' in '" + entry + "'";
                    return new List<QuietWindow>();
                }

                string[] times = parts[1].Split('-');
                TimeSpan start, end;
                if (times.Length != 2 || !ParseClock(times[0], out start) || !ParseClock(times[1], out end))
                {
                    error = "bad times '" + parts[1] + "' in '" + entry + "'";
                    return new List<QuietWindow>();
                }
                if (end <= start)
                {
                    error = "'" + entry + "' crosses midnight or is empty: split it into two windows";
                    return new List<QuietWindow>();
                }
                w.Start = start;
                w.End = end;
                windows.Add(w);
            }
            return windows;
        }

        public static bool InQuietWindow(DateTime local, IList<QuietWindow> windows)
        {
            if (windows == null)
                return false;
            int day = (int)local.DayOfWeek;
            TimeSpan t = local.TimeOfDay;
            foreach (QuietWindow w in windows)
            {
                if (w.Days[day] && t >= w.Start && t < w.End)
                    return true;
            }
            return false;
        }

        private static bool ParseDays(string text, bool[] days)
        {
            string t = text.Trim().ToLowerInvariant();
            if (t == "daily" || t == "everyday")
            {
                for (int i = 0; i < 7; i++)
                    days[i] = true;
                return true;
            }
            if (t == "weekdays")
                t = "mon-fri";
            if (t == "weekends")
                t = "sat,sun";

            foreach (string piece in t.Split(','))
            {
                string[] range = piece.Split('-');
                int from = Array.IndexOf(DayNames, range[0].Trim());
                int to = range.Length == 2 ? Array.IndexOf(DayNames, range[1].Trim()) : from;
                if (from < 0 || to < 0 || range.Length > 2)
                    return false;
                int d = from;
                while (true)
                {
                    days[d] = true;
                    if (d == to)
                        break;
                    d = (d + 1) % 7;
                }
            }
            return true;
        }

        private static bool ParseClock(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            string[] hm = text.Trim().Split(':');
            int h, m;
            if (hm.Length != 2 || !int.TryParse(hm[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
                || !int.TryParse(hm[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
                return false;
            if (h < 0 || m < 0 || m > 59 || h > 24 || (h == 24 && m != 0))
                return false;
            value = new TimeSpan(h, m, 0);
            return true;
        }

        #endregion

        #region Fills and commands

        // Positive = the follower did worse than the leader (paid more on a buy, received less on a sell).
        public static double TickDifference(bool isBuy, double leaderAvg, double followerAvg, double tick)
        {
            return (isBuy ? followerAvg - leaderAvg : leaderAvg - followerAvg) / tick;
        }

        public static string DescribeTicks(double ticks)
        {
            double magnitude = Math.Round(Math.Abs(ticks), 1);
            if (magnitude < 0.1)
                return "same price";
            return magnitude.ToString("0.#", CultureInfo.InvariantCulture) + (magnitude == 1.0 ? " tick " : " ticks ") + (ticks > 0 ? "worse" : "better");
        }

        // Hides internal copier order ids such as "73-007F6E72"; keeps readable names like "Entry" or "B27_Long".
        public static string CleanOrderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            string n = name.Trim();
            if (n.Length >= 8)
            {
                bool internalId = true;
                foreach (char c in n)
                {
                    if (!(Uri.IsHexDigit(c) || c == '-'))
                    {
                        internalId = false;
                        break;
                    }
                }
                if (internalId)
                    return string.Empty;
            }
            return n;
        }

        public static string ParseCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            string t = text.Trim();
            if (!t.StartsWith("/", StringComparison.Ordinal))
                return null;
            int space = t.IndexOfAny(new[] { ' ', '\n', '\t', '\r' });
            if (space > 0)
                t = t.Substring(0, space);
            int at = t.IndexOf('@');
            if (at > 0)
                t = t.Substring(0, at);                         // "/status@MyBot" -> "/status"
            return t.ToLowerInvariant();
        }

        public static string ParseArgument(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            string t = text.Trim();
            int space = t.IndexOfAny(new[] { ' ', '\n', '\t', '\r' });
            return space > 0 ? t.Substring(space + 1).Trim() : string.Empty;
        }

        #endregion

        #region JSON reader

        public static object ParseJson(string json)
        {
            return Json.Parse(json);
        }

        private static class Json
        {
            public static object Parse(string json)
            {
                int i = 0;
                object value = ParseValue(json, ref i);
                SkipWhitespace(json, ref i);
                return value;
            }

            private static void SkipWhitespace(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
            }

            private static object ParseValue(string s, ref int i)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                    throw new FormatException("Unexpected end of JSON.");

                switch (s[i])
                {
                    case '{': return ParseObject(s, ref i);
                    case '[': return ParseArray(s, ref i);
                    case '"': return ParseString(s, ref i);
                    case 't': ExpectLiteral(s, ref i, "true");  return true;
                    case 'f': ExpectLiteral(s, ref i, "false"); return false;
                    case 'n': ExpectLiteral(s, ref i, "null");  return null;
                    default:  return ParseNumber(s, ref i);
                }
            }

            private static void ExpectLiteral(string s, ref int i, string literal)
            {
                if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                    throw new FormatException("Invalid JSON literal at position " + i + ".");
                i += literal.Length;
            }

            private static Dictionary<string, object> ParseObject(string s, ref int i)
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return d;
                }

                while (true)
                {
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != '"')
                        throw new FormatException("Expected property name at position " + i + ".");
                    string key = ParseString(s, ref i);

                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != ':')
                        throw new FormatException("Expected ':' at position " + i + ".");
                    i++;

                    d[key] = ParseValue(s, ref i);

                    SkipWhitespace(s, ref i);
                    if (i >= s.Length)
                        throw new FormatException("Unterminated object.");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at position " + i + ".");
                }
            }

            private static List<object> ParseArray(string s, ref int i)
            {
                List<object> list = new List<object>();
                i++;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return list;
                }

                while (true)
                {
                    list.Add(ParseValue(s, ref i));
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length)
                        throw new FormatException("Unterminated array.");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException("Expected ',' or ']' at position " + i + ".");
                }
            }

            private static string ParseString(string s, ref int i)
            {
                StringBuilder sb = new StringBuilder();
                i++;                                             // opening quote
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"')
                        return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (i >= s.Length)
                        break;

                    char e = s[i++];
                    switch (e)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length)
                                throw new FormatException("Bad unicode escape.");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default:
                            sb.Append(e);
                            break;
                    }
                }
                throw new FormatException("Unterminated string.");
            }

            private static object ParseNumber(string s, ref int i)
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
                    i++;
                string token = s.Substring(start, i - start);
                if (token.Length == 0)
                    throw new FormatException("Unexpected character at position " + start + ".");

                long asLong;
                if (token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
                    && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out asLong))
                    return asLong;

                return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        #endregion
    }
}
