// DeviceAgentTest - end-to-end check of direct mode on the phone, no UI.
// Lives in \Storage Card\WMAI\agenttest\ and uses the app's files one level up
// (WMAI.config, WMAI.agent.json, WMAI.roots). Writes agenttest.log. It starts
// a new chat, so its two test turns become the app's chat history.
using System;
using System.IO;
using System.Text;
using WMAI;

class DeviceAgentTest
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
        Log((ok ? "PASS " : "FAIL ") + name + (detail != null ? " - " + detail : ""));
    }

    [MTAThread]
    static void Main()
    {
        string exe = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase;
        string here = Path.GetDirectoryName(exe);
        string app = Path.GetDirectoryName(here);
        log = new StreamWriter(Path.Combine(here, "agenttest.log"), false, Encoding.UTF8);
        t0 = Environment.TickCount;
        try
        {
            Agent agent = new Agent(AgentConfig.Load(Path.Combine(app, "WMAI.config")), new Tools(null), app);
            agent.NewChat();
            Log("agent ready, model " + agent.Model);

            StringBuilder shown = new StringBuilder();
            TextHandler show = delegate(string s) { shown.Append(s); };

            int t = Environment.TickCount;
            agent.RunTurn("Reply with exactly the word pong.", show);
            Check("plain turn", shown.ToString().ToLower().IndexOf("pong") >= 0,
                  "'" + shown.ToString().Trim() + "' in " + (Environment.TickCount - t) + " ms, handshake " + agent.LastHandshakeMs + " ms");

            shown.Length = 0;
            t = Environment.TickCount;
            agent.RunTurn("Use device_info and tell me the battery level and free memory in one short sentence. Write it in Bulgarian.", show);
            string s2 = shown.ToString();
            Check("device tool turn", s2.IndexOf("[tool] device_info(") >= 0 && s2.IndexOf(" - ok") >= 0 && s2.IndexOf("[error:") < 0,
                  (Environment.TickCount - t) + " ms on the open connection");
            Log("     reply: " + s2.Replace("\n", " | "));
        }
        catch (Exception ex)
        {
            failures++;
            Log("FAIL unexpected " + ex.GetType().FullName + ": " + ex.Message);
            Log(ex.StackTrace);
        }
        Log(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Log("done");
        log.Close();
    }
}
