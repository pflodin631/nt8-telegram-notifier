using System; using System.Collections.Generic; using System.Reflection;
class H6 {
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dtn-h6");
 static void Main() {
  if (System.IO.Directory.Exists(Dir)) System.IO.Directory.Delete(Dir, true);
          System.IO.Directory.CreateDirectory(Dir);
          NinjaTrader.Core.Globals.UserDataDir = Dir;
  var T = Type.GetType("NinjaTrader.NinjaScript.AddOns.DirectTelegramNotifier, AddOn"); var O = Activator.CreateInstance(T, true);
  var B = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
  string text = (string)T.GetMethod("DefaultConfigText", B).Invoke(null, null);
  text = text.Replace("BotToken=\r\n", "BotToken=123456:ABCdefGHIjklMNOpqrSTUvwxYZ123456789\r\n").Replace("AuthorizedChatID=\r\n", "AuthorizedChatID=42\r\n");
  System.IO.File.WriteAllText(Dir + "/DirectTelegramNotifier.config", text);
  object ok = T.GetMethod("LoadConfiguration", B).Invoke(O, null);
  var w = (List<string>)T.GetField("configWarnings", B).GetValue(O);
  Console.WriteLine("LoadConfiguration returned " + ok + "; warnings: " + w.Count);
  foreach (var x in w) Console.WriteLine("  " + x);
  Console.WriteLine("template lines: " + text.Split('\n').Length + ", mode=" + T.GetField("cfgDrawdownMode", B).GetValue(O) + ", hold=" + T.GetField("cfgFlattenHoldSec", B).GetValue(O) + ", stuck states=" + ((HashSet<string>)T.GetField("stuckStates", B).GetValue(O)).Count + ", quiet windows=" + ((System.Collections.IList)T.GetField("staleQuietWindows", B).GetValue(O)).Count);
  Environment.Exit(w.Count == 0 && ok.ToString() == "True" ? 0 : 1);
}}
