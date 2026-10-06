// DeviceWebVisionTest - on the phone, no UI: screenshot + PNG, web_fetch,
// web_search, and an agent turn where deepseek-flash looks at a screenshot.
// Works in its own folder (copies of the app's config/roots/agent files) so
// the user's chat history is untouched; deletes its config copy at the end.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WMAI;

class DeviceWebVisionTest
{
    static StreamWriter log;
    static int t0, failures;

    static void Log(string s)
    {
        log.WriteLine(string.Format("{0,7} ms  {1}", Environment.TickCount - t0, s));
        log.Flush();
    }

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        Log((ok ? "PASS " : "FAIL ") + name);
        if (detail != null) Log("     " + (detail.Length > 400 ? detail.Substring(0, 400) + "..." : detail).Replace("\n", " | "));
    }

    static Dictionary<string, string> Args(params string[] kv)
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    [MTAThread]
    static void Main()
    {
        string here = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase);
        string app = Path.GetDirectoryName(here);
        log = new StreamWriter(Path.Combine(here, "webvision.log"), false, Encoding.UTF8);
        t0 = Environment.TickCount;
        try
        {
            foreach (string f in new string[] { "WMAI.config", "WMAI.agent.json", "WMAI.roots", "WMAI.roots.idx" })
                File.Copy(Path.Combine(app, f), Path.Combine(here, f), true);
            if (File.Exists(Path.Combine(here, "WMAI.chat.json"))) File.Delete(Path.Combine(here, "WMAI.chat.json"));
            Tools tools = new Tools(null);

            int t = Environment.TickCount;
            string r = tools.Execute("screenshot", Args());
            Check("screenshot + PNG", r.StartsWith("screenshot "), r + "  [" + (Environment.TickCount - t) + " ms]");
            tools.Images.TakePending();

            t = Environment.TickCount;
            r = tools.Execute("web_fetch", Args("url", "https://example.com"));
            Check("web_fetch https", r.IndexOf("Example Domain") >= 0, "[" + (Environment.TickCount - t) + " ms] " + r);

            t = Environment.TickCount;
            r = tools.Execute("web_fetch", Args("url", "https://en.wikipedia.org/wiki/Windows_Mobile", "max_chars", "500"));
            Check("web_fetch Wikipedia (other CA, big page)", r.StartsWith("URL:") && r.IndexOf("Windows Mobile") >= 0, "[" + (Environment.TickCount - t) + " ms] " + r);

            t = Environment.TickCount;
            r = tools.Execute("web_search", Args("query", "Windows Mobile 5 release date", "count", "3"));
            Check("web_search", r.StartsWith("1. "), "[" + (Environment.TickCount - t) + " ms] " + r);

            Agent agent = new Agent(AgentConfig.Load(Path.Combine(here, "WMAI.config")), tools, here);
            StringBuilder shown = new StringBuilder();
            t = Environment.TickCount;
            agent.RunTurn("Take a screenshot (hide_wmai=false) and describe in one sentence what is on the screen.",
                          delegate(string s) { shown.Append(s); });
            string s2 = shown.ToString();
            Check("agent looks at a screenshot (" + agent.Model + ")", s2.IndexOf("[tool] screenshot(") >= 0 && s2.IndexOf("[error:") < 0,
                  "[" + (Environment.TickCount - t) + " ms] " + s2);
        }
        catch (Exception ex)
        {
            failures++;
            Log("FAIL unexpected " + ex.GetType().FullName + ": " + ex.Message);
            Log(ex.StackTrace);
        }
        finally
        {
            try { File.Delete(Path.Combine(here, "WMAI.config")); } catch (Exception) { } // don't leave a key copy around
        }
        Log(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Log("done");
        log.Close();
    }
}
