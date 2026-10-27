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

// DirectTelegramNotifier: firm rules you can track: the inactivity clock and the trade record behind it.
// One part of a partial class: see DirectTelegramNotifier.cs for the overview.

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class DirectTelegramNotifier
    {
        #region Inactivity rule

        // Some firms close an account that has not traded for N days. This watches the clock and warns you; it never places a trade.
        private readonly Dictionary<string, DateTime> lastActivity = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // UTC, guarded by riskLock
        private readonly HashSet<string> activityAssumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);                         // clock started by the notifier, not by a real trade
        private readonly Dictionary<string, double> inactivityLevel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private List<double> inactivityAlerts = new List<double>();
        private double cfgInactivityDays = double.NaN;
        private DateTime lastInactivityCheckUtc = DateTime.MinValue;

        // An execution's time is in NinjaTrader's clock: shift it to UTC.
        private static DateTime ExecutionUtc(CbiExecution ex)
        {
            DateTime utc = ex.Time - (NtNow() - DateTime.UtcNow);
            return utc > DateTime.UtcNow ? DateTime.UtcNow : utc;
        }

        // Any fill on the account counts as a trade. A real trade always replaces an assumed starting point.
        private void NoteActivity(string account, DateTime utc)
        {
            if (string.IsNullOrEmpty(account))
                return;
            lock (riskLock)
            {
                DateTime previous;
                bool assumed = activityAssumed.Contains(account);
                if (lastActivity.TryGetValue(account, out previous) && utc <= previous && !assumed)
                    return;
                lastActivity[account] = utc;
                activityAssumed.Remove(account);
                riskStateDirty = true;
            }
        }

        private double InactivityLimitFor(string account)
        {
            double days = AccountNumber("InactivityDays", account, cfgInactivityDays);
            return double.IsNaN(days) || days <= 0 ? double.NaN : days;
        }

        private void CheckInactivity(DateTime nowUtc)
        {
            if ((nowUtc - lastInactivityCheckUtc).TotalSeconds < 30)
                return;
            lastInactivityCheckUtc = nowUtc;
            try
            {
                List<string> loud = new List<string>();
                List<string> quiet = new List<string>();

                foreach (CbiAccount a in SnapshotTrackedAccounts())
                {
                    double limit = InactivityLimitFor(a.Name);
                    if (double.IsNaN(limit))
                    {
                        lock (riskLock)
                            inactivityLevel.Remove(a.Name);
                        continue;
                    }

                    DateTime last;
                    bool assumed;
                    bool created = false;
                    lock (riskLock)
                    {
                        if (!lastActivity.TryGetValue(a.Name, out last))
                        {
                            last = nowUtc;
                            lastActivity[a.Name] = last;
                            activityAssumed.Add(a.Name);
                            riskStateDirty = true;
                            created = true;
                        }
                        assumed = activityAssumed.Contains(a.Name);
                    }
                    if (created)
                        quiet.Add(MsgActivityStarted(a.Name, limit));

                    double remaining = Dtn.InactivityRemainingDays(last, nowUtc, limit);
                    double level = Dtn.RiskLevel(remaining, inactivityAlerts);
                    double previous;
                    lock (riskLock)
                    {
                        if (!inactivityLevel.TryGetValue(a.Name, out previous))
                            previous = double.PositiveInfinity;
                        inactivityLevel[a.Name] = level;
                    }

                    if (level < previous)
                        loud.Add(MsgInactivity(a.Name, remaining, limit, nowUtc - last, level, assumed));
                    else if (double.IsPositiveInfinity(level) && !double.IsPositiveInfinity(previous))
                        quiet.Add(MsgInactivityReset(a.Name, limit));
                }

                foreach (string text in loud)
                    EnqueueText(text);
                foreach (string text in quiet)
                    EnqueueQuiet(text);
            }
            catch (Exception ex)
            {
                WriteError("Inactivity check", ex);
            }
        }

        // /activity            last trade and days left for every tracked account
        // /activity 002 3      the last trade on that account was 3 days ago (0 = just now)
        private string HandleActivityCommand(string argument)
        {
            string arg = (argument ?? string.Empty).Trim();
            if (arg.Length == 0)
                return BuildActivityReport();

            string[] parts = arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            const string usage = " Usage: /activity  \u00B7  /activity 002 3  (the last trade on that account was 3 days ago)";
            if (parts.Length != 2)
                return EmInfo + usage;

            double daysAgo = ParseOptionalDouble(parts[1]);
            if (double.IsNaN(daysAgo) || daysAgo < 0 || daysAgo > 365)
                return EmInfo + usage;

            string problem;
            List<CbiAccount> targets = ResolveAccounts(parts[0], out problem);
            if (targets == null)
                return EmInfo + " " + Html(problem);

            string name = targets[0].Name;
            DateTime now = DateTime.UtcNow;
            string was;
            lock (riskLock)
            {
                DateTime previous;
                was = lastActivity.TryGetValue(name, out previous) ? FormatDuration(now - previous) + " ago" : "not recorded";
                lastActivity[name] = now.AddDays(-daysAgo);
                activityAssumed.Remove(name);
                riskStateDirty = true;
            }
            lastInactivityCheckUtc = DateTime.MinValue;          // re-check at once
            return EmOk + " " + Html(name) + ": last trade set to " + (daysAgo == 0 ? "just now" : FormatDuration(TimeSpan.FromDays(daysAgo)) + " ago")
                + " (it was " + Html(was) + ").\n\n" + BuildActivityReport();
        }

        private string BuildActivityReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(EmClock).Append(" <b>ACTIVITY</b>\n");
            DateTime now = DateTime.UtcNow;
            bool any = false;
            foreach (CbiAccount a in SnapshotTrackedAccounts())
            {
                any = true;
                DateTime last;
                bool known;
                bool assumed;
                lock (riskLock)
                {
                    known = lastActivity.TryGetValue(a.Name, out last);
                    assumed = activityAssumed.Contains(a.Name);
                }
                sb.Append("<b>").Append(Html(a.Name)).Append("</b>").Append(Sep);
                if (!known)
                    sb.Append("no trade seen yet");
                else
                    sb.Append("last trade ").Append(FormatDuration(now - last)).Append(" ago (")
                      .Append((NtNow() - (now - last)).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)).Append(")").Append(assumed ? " " + EmWarn + " assumed" : string.Empty);

                double limit = InactivityLimitFor(a.Name);
                if (double.IsNaN(limit))
                    sb.Append(Sep).Append("no inactivity rule set");
                else if (known)
                {
                    double remaining = Dtn.InactivityRemainingDays(last, now, limit);
                    sb.Append(Sep).Append("<b>").Append(remaining > 0 ? DaysText(remaining) + " left" : DaysText(-remaining) + " past the limit").Append("</b> of ").Append(DaysText(limit));
                }
                sb.Append("\n");
            }
            if (!any)
                sb.Append("No tracked accounts.\n");
            sb.Append("<i>Any fill counts as a trade. Set the rule with InactivityDays (per account: InactivityDays:Account=7). \"Assumed\" means no earlier trade was found: fix it with /activity 002 3.</i>");
            return sb.ToString();
        }

        private static string DaysText(double days)
        {
            double rounded = Math.Round(days, 1);
            return rounded.ToString("0.#", CultureInfo.InvariantCulture) + (rounded == 1.0 ? " day" : " days");
        }

        private static string MsgInactivity(string account, double remaining, double limit, TimeSpan since, double level, bool assumed)
        {
            return (level <= 0 ? EmSiren + " <b>INACTIVITY LIMIT REACHED</b>" : EmWarn + " <b>INACTIVITY CLOCK</b>") + "\n"
                + Html(account) + Sep + "last trade " + FormatDuration(since) + " ago\n"
                + (remaining > 0 ? "<b>" + DaysText(remaining) + " left</b> of " + DaysText(limit)
                                 : "<b>" + DaysText(-remaining) + " past</b> the " + DaysText(limit) + " limit") + "\n"
                + (assumed ? "<i>No earlier trade was found, so this clock started when the notifier first saw the account. If your last trade was earlier, send /activity &lt;account&gt; &lt;days ago&gt;.</i>\n" : string.Empty)
                + "<i>Trade in time, or tell your firm in advance about a break. The notifier never places trades.</i>";
        }

        private static string MsgInactivityReset(string account, double limit)
        {
            return EmOk + " <b>INACTIVITY CLOCK RESET</b>\n" + Html(account) + Sep + "trade recorded, " + DaysText(limit) + " again";
        }

        private static string MsgActivityStarted(string account, double limit)
        {
            return EmInfo + " <b>INACTIVITY CLOCK STARTED</b>\n" + Html(account) + Sep + DaysText(limit) + " limit\n"
                + "<i>No earlier trade was found, so the clock starts now. If your last trade was earlier, send /activity &lt;account&gt; &lt;days ago&gt;.</i>";
        }

        #endregion
    }
}
