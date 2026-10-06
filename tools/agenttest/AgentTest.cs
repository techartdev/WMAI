// AgentTest - runs app/Agent.cs against the real API on the PC (desktop .NET).
//   AgentTest.exe <relay .env path> <app dir with WMAI.agent.json + WMAI.roots>
// Makes a temp working dir with WMAI.config (key read from .env, never printed),
// then: a plain turn, a turn that needs a tool, history reload, new chat.
// Tools run on the PC here, so device-only tools return "error:" - that still
// exercises the full tool round trip.
using System;
using System.IO;
using System.Text;
using WMAI;

class AgentTest
{
    static int failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null ? " - " + detail : ""));
    }

    static int Main(string[] args)
    {
        string key = null;
        foreach (string line in File.ReadAllLines(args[0]))
            if (line.StartsWith("DEEPSEEK_API_KEY=")) key = line.Substring(17).Trim();
        if (key == null || key.Length == 0) { Console.WriteLine("no DEEPSEEK_API_KEY in " + args[0]); return 2; }

        string dir = Path.Combine(Path.GetTempPath(), "wmai_agenttest");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(args[1], "WMAI.agent.json"), Path.Combine(dir, "WMAI.agent.json"), true);
        File.Copy(Path.Combine(args[1], "WMAI.roots"), Path.Combine(dir, "WMAI.roots"), true);
        File.WriteAllText(Path.Combine(dir, "WMAI.config"), "model=deepseek-chat\nkey=" + key + "\n");
        if (File.Exists(Path.Combine(dir, "WMAI.chat.json"))) File.Delete(Path.Combine(dir, "WMAI.chat.json"));

        try
        {
            Tools tools = new Tools(null); // no UI: anything needing approval is denied
            Agent agent = new Agent(AgentConfig.Load(Path.Combine(dir, "WMAI.config")), tools, dir);

            StringBuilder shown = new StringBuilder();
            TextHandler show = delegate(string s) { shown.Append(s); Console.Write(s); };

            Console.Write("You: Reply with exactly the word pong.\nAI: ");
            int t = Environment.TickCount;
            agent.RunTurn("Reply with exactly the word pong.", show);
            Console.WriteLine();
            Check("plain streamed turn", shown.ToString().ToLower().IndexOf("pong") >= 0,
                  (Environment.TickCount - t) + " ms, handshake " + agent.LastHandshakeMs + " ms");

            shown.Length = 0;
            string q = "Use the fs_list tool on the path \\Storage Card and tell me the result in one sentence.";
            Console.Write("You: " + q + "\nAI: ");
            t = Environment.TickCount;
            agent.RunTurn(q, show);
            Console.WriteLine();
            string s1 = shown.ToString();
            Check("tool round trip", s1.IndexOf("[tool] fs_list(") >= 0 && s1.IndexOf(" - error") >= 0 && s1.IndexOf("[error:") < 0,
                  (Environment.TickCount - t) + " ms (fs_list fails on the PC, as expected)");

            Agent reloaded = new Agent(AgentConfig.Load(Path.Combine(dir, "WMAI.config")), tools, dir);
            string tr = reloaded.Transcript();
            Check("history saved and reloaded", tr.IndexOf("You: Reply with exactly") == 0 && tr.IndexOf("[tool] fs_list(") > 0,
                  tr.Length + " chars of transcript");
            Console.WriteLine("--- transcript ---\n" + tr + "\n------------------");

            reloaded.NewChat();
            Check("new chat clears history", new Agent(AgentConfig.Load(Path.Combine(dir, "WMAI.config")), tools, dir).Transcript().Length == 0, null);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("FAIL unexpected " + ex);
        }
        finally
        {
            File.Delete(Path.Combine(dir, "WMAI.config")); // don't leave the key around
        }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
