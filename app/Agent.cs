// Agent.cs - the chat/tool loop running on the phone itself (direct mode).
// Mirrors relay/relay.py run_turn(): stream an OpenAI-compatible chat
// completion, run requested tools locally (Tools.cs), feed results back, up to
// max_steps model calls per user message. The system prompt and tool schemas
// come from WMAI.agent.json (exported from relay/tools.py, so both stay in sync).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WMAI
{
    public delegate void TextHandler(string text);

    // WMAI.config next to the exe: "name=value" lines, '#' comments.
    public class AgentConfig
    {
        public string Host = "api.deepseek.com";
        public string Path = "/chat/completions";
        public string Model = "deepseek-chat";
        public string Key;

        public static AgentConfig Load(string path)
        {
            if (!File.Exists(path)) return null;
            AgentConfig c = new AgentConfig();
            using (StreamReader r = new StreamReader(path, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    line = line.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLower(), v = line.Substring(eq + 1).Trim();
                    if (k == "host") c.Host = v;
                    else if (k == "path") c.Path = v;
                    else if (k == "model") c.Model = v;
                    else if (k == "key") c.Key = v;
                }
            }
            return c.Key != null && c.Key.Length > 0 ? c : null;
        }
    }

    public class Agent
    {
        // History budget in characters (sent with every request, so it costs
        // bandwidth on slow links and memory on the phone); see TrimHistory.
        const int MaxHistoryChars = 150000;
        const int KeepToolResults = 8; // newest tool results are never shortened

        readonly AgentConfig cfg;
        readonly Tools tools;
        readonly string dir;
        readonly string systemPrompt;
        readonly ArrayList toolSchemas;
        readonly int maxSteps;
        ArrayList history = new ArrayList();
        HttpsConnection conn;
        TextHandler show;
        bool atLineStart = true;

        public Agent(AgentConfig cfg, Tools tools, string dir)
        {
            this.cfg = cfg;
            this.tools = tools;
            this.dir = dir;
            string def;
            using (StreamReader r = new StreamReader(System.IO.Path.Combine(dir, "WMAI.agent.json"), Encoding.UTF8))
                def = r.ReadToEnd();
            Hashtable d = (Hashtable)Json.Parse(def);
            systemPrompt = Json.GetString(d, "system");
            toolSchemas = Json.GetArray(d, "tools");
            maxSteps = d["max_steps"] is double ? (int)(double)d["max_steps"] : 12;
            LoadHistory();
        }

        public string Model { get { return cfg.Model; } }
        public int LastHandshakeMs { get { return conn == null ? 0 : conn.HandshakeMs; } }

        string HistoryPath { get { return System.IO.Path.Combine(dir, "WMAI.chat.json"); } }

        // ---- one user message ----
        public void RunTurn(string userText, TextHandler showText)
        {
            show = showText;
            atLineStart = true; // like the relay: a turn starting with a tool call stays on the "AI: " line
            history.Add(Msg("user", userText));
            try
            {
                int step;
                for (step = 0; step < maxSteps; step++)
                {
                    string content, reasoning;
                    ArrayList calls;
                    Complete(out content, out reasoning, out calls);
                    Hashtable am = Msg("assistant", content);
                    if (calls.Count > 0) am["tool_calls"] = calls;
                    // Thinking models (deepseek-flash) require their reasoning to be
                    // sent back with tool calls for the rest of the turn.
                    if (reasoning.Length > 0) am["reasoning_content"] = reasoning;
                    history.Add(am);
                    if (calls.Count == 0) break;

                    ArrayList images = new ArrayList();
                    foreach (Hashtable call in calls)
                    {
                        Hashtable fn = (Hashtable)call["function"];
                        string name = (string)fn["name"];
                        Hashtable args = null;
                        try { args = Json.Parse((string)fn["arguments"] == "" ? "{}" : (string)fn["arguments"]) as Hashtable; }
                        catch (FormatException) { }
                        Show((atLineStart ? "" : "\n") + Describe(name, args));
                        string result = args == null ? "error: arguments were not valid JSON"
                                                     : tools.Execute(name, ToStringArgs(args));
                        string outcome = result.StartsWith("denied") ? "denied" : result.StartsWith("error") ? "error" : "ok";
                        Show(" - " + outcome + "\n");
                        Hashtable tm = Msg("tool", result);
                        tm["tool_call_id"] = call["id"];
                        history.Add(tm);
                        ImageAttachment img = tools.Images.TakePending();
                        if (img != null) images.Add(img);
                    }
                    // Tool results must be text, so images follow as a user message
                    // (verified with DeepSeek: tool -> user[image] -> assistant works).
                    if (images.Count > 0) history.Add(ImageMessage(images));
                }
                if (step == maxSteps)
                    Show("\n[paused after " + maxSteps + " steps - say \"continue\" to let it go on; it keeps all context]");
            }
            catch (Exception ex)
            {
                if (conn != null) conn.Close();
                Show("\n[error: " + Tools.Describe(ex) + "]");
            }
            finally
            {
                DropUnansweredToolCalls();
                StripTurnExtras();
                Trim();
                SaveHistory();
                show = null;
            }
        }

        // An OpenAI-style user message carrying images (data URLs).
        static Hashtable ImageMessage(ArrayList images)
        {
            ArrayList parts = new ArrayList();
            StringBuilder label = new StringBuilder("[attached by the tool: ");
            foreach (ImageAttachment img in images)
            {
                Hashtable url = new Hashtable();
                url["url"] = "data:" + img.Mime + ";base64," + Convert.ToBase64String(img.Data);
                Hashtable part = new Hashtable();
                part["type"] = "image_url";
                part["image_url"] = url;
                parts.Add(part);
                label.Append(img.Label).Append("; ");
            }
            Hashtable text = new Hashtable();
            text["type"] = "text";
            text["text"] = label.ToString().TrimEnd(' ', ';') + "]";
            parts.Insert(0, text);
            Hashtable m = new Hashtable();
            m["role"] = "user";
            m["content"] = parts;
            return m;
        }

        // After a turn: drop reasoning text and image messages. They are only
        // needed while the turn runs, and would bloat every later request and
        // WMAI.chat.json (a screenshot is ~100 KB of base64).
        void StripTurnExtras()
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                Hashtable m = (Hashtable)history[i];
                m.Remove("reasoning_content");
                if ((string)m["role"] == "user" && m["content"] is ArrayList) history.RemoveAt(i);
            }
        }

        void Show(string s)
        {
            if (s.Length == 0) return;
            atLineStart = s[s.Length - 1] == '\n';
            if (show != null) show(s);
        }

        static Hashtable Msg(string role, string content)
        {
            Hashtable m = new Hashtable();
            m["role"] = role;
            m["content"] = content;
            return m;
        }

        // ---- one streamed model call ----
        void Complete(out string content, out string reasoningText, out ArrayList calls)
        {
            ArrayList messages = new ArrayList();
            messages.Add(Msg("system", systemPrompt));
            messages.AddRange(history);
            Hashtable req = new Hashtable();
            req["model"] = cfg.Model;
            req["messages"] = messages;
            req["tools"] = toolSchemas;
            req["stream"] = true;
            byte[] body = Encoding.UTF8.GetBytes(Json.Write(req));

            if (conn == null)
                conn = new HttpsConnection(cfg.Host, 443, TrustStore.LoadDefault(dir));
            HttpResponse r = conn.Send("POST", cfg.Path, new string[] {
                "Authorization: Bearer " + cfg.Key,
                "Content-Type: application/json",
                "Accept: text/event-stream" }, body);
            if (r.Status != 200) throw new IOException("HTTP " + r.Status + ": " + ErrorText(r.ReadBodyText()));

            StringBuilder text = new StringBuilder();
            StringBuilder reasoning = new StringBuilder();
            SortedList byIndex = new SortedList(); // tool call index -> Hashtable
            LineReader lines = new LineReader(r.Body);
            string line;
            while ((line = lines.ReadLine()) != null)
            {
                if (!line.StartsWith("data:")) continue; // blank separators, ": keep-alive"
                string data = line.Substring(5).Trim();
                if (data == "[DONE]") break;
                Hashtable chunk;
                try { chunk = Json.Parse(data) as Hashtable; }
                catch (FormatException) { continue; }
                ArrayList choices = Json.GetArray(chunk, "choices");
                if (choices == null || choices.Count == 0) continue;
                Hashtable delta = Json.GetObject((Hashtable)choices[0], "delta");
                string piece = Json.GetString(delta, "content");
                if (piece != null && piece.Length > 0)
                {
                    text.Append(piece);
                    Show(piece);
                }
                string thought = Json.GetString(delta, "reasoning_content");
                if (thought != null) reasoning.Append(thought); // not shown; sent back within the turn
                ArrayList tcs = Json.GetArray(delta, "tool_calls");
                if (tcs == null) continue;
                foreach (Hashtable tc in tcs)
                {
                    int index = tc["index"] is double ? (int)(double)tc["index"] : 0;
                    Hashtable call = (Hashtable)byIndex[index];
                    if (call == null)
                    {
                        call = new Hashtable();
                        call["id"] = "";
                        call["type"] = "function";
                        Hashtable f = new Hashtable();
                        f["name"] = "";
                        f["arguments"] = "";
                        call["function"] = f;
                        byIndex[index] = call;
                    }
                    if (Json.GetString(tc, "id") != null) call["id"] = tc["id"];
                    Hashtable fd = Json.GetObject(tc, "function");
                    Hashtable cf = (Hashtable)call["function"];
                    if (Json.GetString(fd, "name") != null) cf["name"] = (string)cf["name"] + (string)fd["name"];
                    if (Json.GetString(fd, "arguments") != null) cf["arguments"] = (string)cf["arguments"] + (string)fd["arguments"];
                }
            }
            lines.Drain(); // read to the end so the connection can be reused

            content = text.ToString();
            reasoningText = reasoning.ToString();
            calls = new ArrayList(byIndex.Values);
        }

        static string ErrorText(string body)
        {
            try
            {
                Hashtable err = Json.GetObject(Json.Parse(body) as Hashtable, "error");
                string msg = Json.GetString(err, "message");
                if (msg != null) return msg;
            }
            catch (FormatException) { }
            return body.Length > 200 ? body.Substring(0, 200) : body;
        }

        // Tools.cs takes string arguments, like the relay's wire format.
        static Dictionary<string, string> ToStringArgs(Hashtable args)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (DictionaryEntry de in args)
            {
                object v = de.Value;
                string s = v as string;
                if (s == null) s = v is bool ? ((bool)v ? "true" : "false") : Json.Write(v);
                d[(string)de.Key] = s;
            }
            return d;
        }

        static string Describe(string name, Hashtable args)
        {
            StringBuilder b = new StringBuilder("[tool] ").Append(name).Append('(');
            if (args != null)
            {
                bool first = true;
                foreach (DictionaryEntry de in args)
                {
                    string v = de.Value as string;
                    if (v == null) v = Json.Write(de.Value);
                    v = v.Replace("\n", " ");
                    if (v.Length > 40) v = v.Substring(0, 37) + "...";
                    if (!first) b.Append(", ");
                    first = false;
                    b.Append((string)de.Key).Append('=').Append(v);
                }
            }
            return b.Append(')').ToString();
        }

        // ---- history ----
        // The API rejects tool_calls without results; a failed turn can leave
        // such a tail, so cut history back to before it.
        void DropUnansweredToolCalls()
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                Hashtable m = (Hashtable)history[i];
                ArrayList calls = m["tool_calls"] as ArrayList;
                if ((string)m["role"] != "assistant" || calls == null) continue;
                Hashtable answered = new Hashtable();
                for (int j = i + 1; j < history.Count; j++)
                {
                    Hashtable x = (Hashtable)history[j];
                    if ((string)x["role"] == "tool" && x["tool_call_id"] != null) answered[x["tool_call_id"]] = true;
                }
                foreach (Hashtable c in calls)
                    if (!answered.ContainsKey(c["id"])) { history.RemoveRange(i, history.Count - i); break; }
                return;
            }
        }

        // Keep the newest messages, starting at a user message.
        void Trim()
        {
            TrimHistory(history, MaxHistoryChars, KeepToolResults);
        }

        // Keeps the conversation within budget without ever losing the current
        // turn (a long coding turn is one user message followed by dozens of
        // assistant/tool messages - the old count-based cut, which looked for a
        // later user message to start at, deleted everything in that case).
        //  1. Old tool results are shortened to a stub first: the model keeps
        //     the record of what it did, just not every byte of output.
        //  2. Then whole turns are dropped, oldest first; never the last one.
        internal static void TrimHistory(ArrayList h, int maxChars, int keepRecentTools)
        {
            if (Size(h) <= maxChars) return;

            int toolsSeen = 0;
            for (int i = h.Count - 1; i >= 0 && Size(h) > maxChars; i--)
            {
                Hashtable m = (Hashtable)h[i];
                if ((string)m["role"] != "tool") continue;
                if (++toolsSeen <= keepRecentTools) continue;
                string c = m["content"] as string;
                if (c != null && c.Length > 300)
                    m["content"] = c.Substring(0, 200) + "\n...[" + (c.Length - 200) + " chars trimmed from history]";
            }

            while (Size(h) > maxChars)
            {
                int next = -1; // start of the second turn
                for (int i = 1; i < h.Count && next < 0; i++)
                    if (IsTurnStart((Hashtable)h[i])) next = i;
                if (next < 0) break; // only the current turn is left: keep it
                h.RemoveRange(0, next);
            }
        }

        static bool IsTurnStart(Hashtable m)
        {
            return (string)m["role"] == "user" && m["content"] is string; // not a tool's image message
        }

        static int Size(ArrayList h)
        {
            int n = 0;
            foreach (Hashtable m in h)
            {
                string c = m["content"] as string;
                if (c != null) n += c.Length;
                ArrayList calls = m["tool_calls"] as ArrayList;
                if (calls != null)
                    foreach (Hashtable call in calls)
                        n += ((string)((Hashtable)call["function"])["arguments"]).Length + 50;
            }
            return n;
        }

        public void NewChat()
        {
            history.Clear();
            SaveHistory();
        }

        void LoadHistory()
        {
            try
            {
                using (StreamReader r = new StreamReader(HistoryPath, Encoding.UTF8))
                {
                    ArrayList h = Json.Parse(r.ReadToEnd()) as ArrayList;
                    if (h != null) history = h;
                }
            }
            catch (Exception) { history = new ArrayList(); } // missing or unreadable: start fresh
        }

        void SaveHistory()
        {
            string tmp = HistoryPath + ".tmp";
            using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                w.Write(Json.Write(history));
            if (File.Exists(HistoryPath)) File.Delete(HistoryPath);
            File.Move(tmp, HistoryPath);
        }

        // Display form, same as the relay's: "You: ..." / "AI: ..." blocks with
        // [tool] lines; one AI block per turn.
        public string Transcript()
        {
            StringBuilder b = new StringBuilder();
            string lastWho = null;
            foreach (Hashtable m in history)
            {
                string role = (string)m["role"];
                if (role == "user")
                {
                    string said = m["content"] as string;
                    if (said == null) continue; // image message attached by a tool
                    if (b.Length > 0) b.Append("\n\n");
                    b.Append("You: ").Append(said);
                    lastWho = "You";
                }
                else if (role == "assistant")
                {
                    StringBuilder part = new StringBuilder();
                    string c = m["content"] as string;
                    if (c != null && c.Length > 0) part.Append(c);
                    ArrayList calls = m["tool_calls"] as ArrayList;
                    if (calls != null)
                        foreach (Hashtable call in calls)
                        {
                            Hashtable fn = (Hashtable)call["function"];
                            Hashtable args = null;
                            try { args = Json.Parse((string)fn["arguments"] == "" ? "{}" : (string)fn["arguments"]) as Hashtable; }
                            catch (FormatException) { }
                            if (part.Length > 0) part.Append('\n');
                            part.Append(Describe((string)fn["name"], args));
                        }
                    if (part.Length == 0) continue;
                    if (lastWho == "AI") b.Append('\n');
                    else b.Append("\n\nAI: ");
                    b.Append(part.ToString());
                    lastWho = "AI";
                }
            }
            return b.ToString().TrimStart('\n');
        }
    }

    // UTF-8 lines from a byte stream; multi-byte characters may span reads.
    class LineReader
    {
        readonly Stream s;
        readonly byte[] buf = new byte[2048];
        int pos, len;
        readonly MemoryStream line = new MemoryStream();

        public LineReader(Stream s) { this.s = s; }

        public string ReadLine()
        {
            line.SetLength(0);
            while (true)
            {
                if (pos >= len)
                {
                    len = s.Read(buf, 0, buf.Length);
                    pos = 0;
                    if (len <= 0) return line.Length > 0 ? Decode() : null;
                }
                byte b = buf[pos++];
                if (b == (byte)'\n') return Decode();
                line.WriteByte(b);
            }
        }

        string Decode()
        {
            byte[] a = line.ToArray();
            int n = a.Length;
            if (n > 0 && a[n - 1] == (byte)'\r') n--;
            return Encoding.UTF8.GetString(a, 0, n);
        }

        public void Drain()
        {
            while (s.Read(buf, 0, buf.Length) > 0) { }
        }
    }
}
