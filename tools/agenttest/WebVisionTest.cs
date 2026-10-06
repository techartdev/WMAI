// WebVisionTest - PC check of app/Web.cs, app/ImageTools.cs and the
// deepseek-flash agent loop (reasoning pass-back, image attachments).
//   WebVisionTest.exe <relay .env> <app dir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WMAI;

class WebVisionTest
{
    static int failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (detail != null) Console.WriteLine("     " + detail.Replace("\n", "\n     "));
    }

    static Dictionary<string, string> Args(params string[] kv)
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    static string Head(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "..."; }

    static int Main(string[] args)
    {
        string appDir = args[1];
        string dir = Path.Combine(Path.GetTempPath(), "wmai_webvision");
        Directory.CreateDirectory(dir);
        foreach (string f in new string[] { "WMAI.agent.json", "WMAI.roots", "WMAI.roots.idx" })
            File.Copy(Path.Combine(appDir, f), Path.Combine(dir, f), true);
        try
        {
            WebTools web = new WebTools(dir);
            int t = Environment.TickCount;
            string r = web.Fetch(Args("url", "https://example.com"));
            Check("web_fetch https (example.com)", r.IndexOf("Example Domain") >= 0, Head(r, 200) + "  [" + (Environment.TickCount - t) + " ms]");
            r = web.Fetch(Args("url", "http://example.com"));
            Check("web_fetch plain http (http://example.com)", r.IndexOf("(HTTP 200)") >= 0, Head(r, 160));
            r = web.Fetch(Args("url", "https://en.wikipedia.org/wiki/Windows_Mobile_5.0", "max_chars", "600", "links", "true"));
            Check("web_fetch other CA + redirects + links (Wikipedia)", r.IndexOf("Windows Mobile") >= 0 && r.IndexOf("Links:") >= 0, Head(r, 400));
            r = web.Search(Args("query", "PocketGCC Windows CE compiler", "count", "3"));
            Check("web_search", r.StartsWith("1. ") && r.IndexOf("http") >= 0, r);

            // A test image the model must describe: left half red, right half blue.
            byte[] rgb = new byte[64 * 32 * 3];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 64; x++)
                {
                    int o = (y * 64 + x) * 3;
                    if (x < 32) rgb[o] = 220; else rgb[o + 2] = 220;
                }
            string png = Path.Combine(dir, "halves.png");
            File.WriteAllBytes(png, ImageTools.EncodePng(rgb, 64, 32));

            string key = null;
            foreach (string line in File.ReadAllLines(args[0]))
                if (line.StartsWith("DEEPSEEK_API_KEY=")) key = line.Substring(17).Trim();
            File.WriteAllText(Path.Combine(dir, "WMAI.config"), "model=deepseek-flash\nkey=" + key + "\n");
            if (File.Exists(Path.Combine(dir, "WMAI.chat.json"))) File.Delete(Path.Combine(dir, "WMAI.chat.json"));
            Agent agent = new Agent(AgentConfig.Load(Path.Combine(dir, "WMAI.config")), new Tools(null), dir);
            StringBuilder shown = new StringBuilder();
            TextHandler show = delegate(string s) { shown.Append(s); };

            t = Environment.TickCount;
            agent.RunTurn("Use fs_list on the path C:\\ and tell me in one sentence whether it worked.", show);
            Check("deepseek-flash tool round trip (reasoning passed back)", shown.ToString().IndexOf("[tool] fs_list(") >= 0 && shown.ToString().IndexOf("[error:") < 0,
                  Head(shown.ToString(), 300) + "  [" + (Environment.TickCount - t) + " ms]");

            shown.Length = 0;
            t = Environment.TickCount;
            agent.RunTurn("Use view_image on " + png + " and tell me which colors the left and right halves have.", show);
            string s2 = shown.ToString().ToLower();
            Check("vision: model sees the attached image", s2.IndexOf("view_image(") >= 0 && s2.IndexOf("red") >= 0 && s2.IndexOf("blue") >= 0,
                  Head(shown.ToString(), 300) + "  [" + (Environment.TickCount - t) + " ms]");

            string chat = File.ReadAllText(Path.Combine(dir, "WMAI.chat.json"));
            Check("history stripped of images and reasoning", chat.IndexOf("base64") < 0 && chat.IndexOf("reasoning_content") < 0,
                  chat.Length + " chars in WMAI.chat.json");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("FAIL unexpected " + ex);
        }
        finally
        {
            File.Delete(Path.Combine(dir, "WMAI.config"));
        }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
