// WMAI - AI chat client for Windows Mobile 5 / .NET Compact Framework 3.5.
//
// Talks plain HTTP/1.0 over a raw TcpClient to the relay on the PC
// (169.254.2.2:8080 over the USB RNDIS link). Raw sockets bypass the WM
// Connection Manager, which would otherwise insist on dialing GPRS.
//
// Written in C# 2.0 style and compiled against desktop .NET 2.0 reference
// assemblies, then retargeted to NETCF (see build.cmd); only APIs that also
// exist in NETCF may be used.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WMAI
{
    public class MainForm : Form
    {
        const string Version = "0.7.2";
        static readonly float[] FontSizes = new float[] { 8f, 9f, 10f, 12f };
        const int MaxLogChars = 24000; // keep the edit control responsive

        string host = "169.254.2.2";
        int port = 8080;
        float fontSize = 9f;

        TextBox log = new TextBox();
        TextBox input = new TextBox();
        Tools tools;
        MenuItem sendItem = new MenuItem();
        System.Windows.Forms.Timer sipTimer = new System.Windows.Forms.Timer();
        bool sipWasOn;
        bool sipAvailable = true;

        // Shared between worker threads and the UI thread, guarded by sync.
        readonly object sync = new object();
        StringBuilder pendingOut = new StringBuilder();
        string pendingReplace; // non-null: replace the whole log (history load)
        bool turnDone;
        bool busy;

        public MainForm()
        {
            Text = "WMAI";
            MinimizeBox = false; // Pocket PC: "ok" closes instead of hiding

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.WordWrap = true;
            input.Multiline = true;
            input.WordWrap = true;
            input.KeyPress += new KeyPressEventHandler(OnInputKeyPress);
            input.GotFocus += new EventHandler(OnInputFocus);
            Controls.Add(log);
            Controls.Add(input);

            sendItem.Text = "Send";
            sendItem.Click += new EventHandler(OnSend);
            MenuItem menu = new MenuItem();
            menu.Text = "Menu";
            AddItem(menu, "New chat", new EventHandler(OnNew));
            AddItem(menu, "Reload chat", new EventHandler(OnReload));
            AddItem(menu, "Text size", new EventHandler(OnTextSize));
            AddItem(menu, "Server...", new EventHandler(OnServer));
            AddItem(menu, "Update app", new EventHandler(OnUpdate));
            AddItem(menu, "Exit", new EventHandler(OnExit));
            MainMenu mm = new MainMenu();
            mm.MenuItems.Add(sendItem);
            mm.MenuItems.Add(menu);
            Menu = mm;

            Resize += new EventHandler(OnResizeForm);
            sipTimer.Interval = 500;
            sipTimer.Tick += new EventHandler(OnSipTimer);
            sipTimer.Enabled = true;

            tools = new Tools(new ApproveHandler(Ask));
            tools.Images.SetWmaiVisible = new VisibilityHandler(SetWmaiVisible); // screenshot hides WMAI
            LoadConfig();
            ApplyFont();
            log.Text = "WMAI " + Version + " - loading chat...\r\n";
            StartWorker(new ThreadStart(InitChat));
            StartWorker(new ThreadStart(CleanupUpdate));
        }

        static void AddItem(MenuItem parent, string text, EventHandler h)
        {
            MenuItem mi = new MenuItem();
            mi.Text = text;
            mi.Click += h;
            parent.MenuItems.Add(mi);
        }

        static void StartWorker(ThreadStart fn)
        {
            Thread t = new Thread(fn);
            t.IsBackground = true;
            t.Start();
        }

        // ---- layout and on-screen keyboard (SIP) ----
        [StructLayout(LayoutKind.Sequential)]
        struct SIPINFO
        {
            public int cbSize, fdwFlags;
            public int visLeft, visTop, visRight, visBottom;
            public int sipLeft, sipTop, sipRight, sipBottom;
            public int dwImDataSize;
            public IntPtr pvImData;
        }
        const int SIPF_ON = 1;

        [DllImport("coredll.dll")]
        static extern bool SipShowIM(int dwFlag);
        [DllImport("coredll.dll")]
        static extern bool SipGetInfo(ref SIPINFO info);

        // Height of the SIP when visible, 0 when hidden or unavailable.
        int SipHeight()
        {
            if (!sipAvailable) return 0;
            try
            {
                SIPINFO si = new SIPINFO();
                si.cbSize = Marshal.SizeOf(typeof(SIPINFO));
                if (SipGetInfo(ref si) && (si.fdwFlags & SIPF_ON) != 0)
                    return si.sipBottom - si.sipTop;
            }
            catch (Exception) { sipAvailable = false; } // e.g. Smartphone edition
            return 0;
        }

        void ShowSip(bool on)
        {
            if (!sipAvailable) return;
            try { SipShowIM(on ? 1 : 0); }
            catch (Exception) { sipAvailable = false; }
            DoLayout();
        }

        void OnSipTimer(object sender, EventArgs e)
        {
            bool on = SipHeight() > 0;
            if (on != sipWasOn) DoLayout();
        }

        void OnResizeForm(object sender, EventArgs e) { DoLayout(); }

        void DoLayout()
        {
            int sip = SipHeight();
            sipWasOn = sip > 0;
            int w = ClientSize.Width, h = ClientSize.Height - sip;
            int ih = Math.Max(h / 5, (int)(fontSize * 4));
            if (h - ih < 40) ih = Math.Max(h / 2, 20);
            log.Bounds = new Rectangle(0, 0, w, h - ih - 2);
            input.Bounds = new Rectangle(0, h - ih, w, ih);
            ScrollLogToEnd();
        }

        void OnInputFocus(object sender, EventArgs e) { ShowSip(true); }

        void ApplyFont()
        {
            Font f = new Font("Tahoma", fontSize, FontStyle.Regular);
            log.Font = f;
            input.Font = f;
            DoLayout();
        }

        // ---- config: WMAI.txt next to the exe: line 1 "host:port", line 2 font size ----
        static string ExePath()
        {
            string p = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase;
            if (p.StartsWith("file:///")) p = p.Substring(8).Replace('/', '\\');
            return p;
        }

        static string AppDir() { return Path.GetDirectoryName(ExePath()); }

        static string ConfigPath() { return Path.Combine(AppDir(), "WMAI.txt"); }

        void LoadConfig()
        {
            try
            {
                using (StreamReader r = new StreamReader(ConfigPath(), Encoding.UTF8))
                {
                    ParseServer(r.ReadLine());
                    string fs = r.ReadLine();
                    if (fs != null) fontSize = float.Parse(fs.Trim());
                }
            }
            catch (Exception) { }
        }

        void SaveConfig()
        {
            try
            {
                using (StreamWriter w = new StreamWriter(ConfigPath(), false, Encoding.UTF8))
                {
                    w.WriteLine(host + ":" + port);
                    w.WriteLine(fontSize.ToString());
                }
            }
            catch (Exception) { }
        }

        bool ParseServer(string s)
        {
            if (s == null) return false;
            s = s.Trim();
            int c = s.IndexOf(':');
            try
            {
                int p = port;
                if (c > 0) { p = int.Parse(s.Substring(c + 1)); s = s.Substring(0, c); }
                if (s.Length == 0 || s.IndexOf(' ') >= 0) return false;
                host = s;
                port = p;
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---- UI actions ----
        void OnInputKeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {
                e.Handled = true;
                OnSend(sender, EventArgs.Empty);
            }
        }

        void OnSend(object sender, EventArgs e)
        {
            string q = input.Text.Trim();
            if (q.Length == 0 || busy) return;
            busy = true;
            sendItem.Enabled = false;
            input.Text = "";
            ShowSip(false);
            Append("\r\nYou: " + q + "\r\nAI: ");
            if (agent != null) StartWorker(new ThreadStart(delegate { ConverseDirect(q); }));
            else StartWorker(new ThreadStart(delegate { Converse(q); }));
        }

        void OnNew(object sender, EventArgs e)
        {
            if (busy) return;
            if (agent != null)
            {
                try { agent.NewChat(); Replace("New chat.\r\n"); }
                catch (Exception ex) { Append("\r\n[" + ex.Message + "]\r\n"); }
                return;
            }
            StartWorker(new ThreadStart(delegate
            {
                try
                {
                    HttpText("GET", "/api/new", null);
                    Replace("New chat.\r\n");
                }
                catch (Exception ex) { Post("\r\n[" + ex.Message + "]\r\n"); }
            }));
        }

        void OnReload(object sender, EventArgs e)
        {
            if (busy) return;
            if (agent != null) ShowDirectHistory();
            else StartWorker(new ThreadStart(LoadHistory));
        }

        void OnTextSize(object sender, EventArgs e)
        {
            int next = 0;
            for (int i = 0; i < FontSizes.Length; i++)
                if (FontSizes[i] == fontSize) next = (i + 1) % FontSizes.Length;
            fontSize = FontSizes[next];
            ApplyFont();
            SaveConfig();
        }

        void OnServer(object sender, EventArgs e)
        {
            // Type "host:port" in the input box, then pick Menu > Server...
            if (ParseServer(input.Text))
            {
                SaveConfig();
                input.Text = "";
            }
            Append("\r\nServer: " + host + ":" + port + "\r\n(type host:port in the box, then Menu > Server)\r\n");
        }

        void OnExit(object sender, EventArgs e)
        {
            Close();
        }

        // The chat box is appended to in place (EM_REPLACESEL at the end). Setting
        // log.Text instead re-renders the whole conversation on every streamed
        // chunk, which blinks and slows down as the chat grows.
        const int WM_GETTEXTLENGTH = 0x000E, EM_SETSEL = 0x00B1, EM_REPLACESEL = 0x00C2, EM_SCROLLCARET = 0x00B7;
        [DllImport("coredll.dll", EntryPoint = "SendMessageW")]
        static extern int SendMessageInt(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("coredll.dll", EntryPoint = "SendMessageW")]
        static extern int SendMessageStr(IntPtr hWnd, int msg, int wParam, string lParam);

        void Append(string s)
        {
            if (s.Length == 0) return;
            IntPtr h = log.Handle;
            int len = SendMessageInt(h, WM_GETTEXTLENGTH, 0, 0);
            if (len + s.Length > MaxLogChars)
            {
                // Drop the oldest quarter in one go, so this rarely happens.
                int cut = Math.Min(len, len + s.Length - MaxLogChars + MaxLogChars / 4);
                SendMessageInt(h, EM_SETSEL, 0, cut);
                SendMessageStr(h, EM_REPLACESEL, 0, "...");
                len = SendMessageInt(h, WM_GETTEXTLENGTH, 0, 0);
            }
            SendMessageInt(h, EM_SETSEL, len, len);
            SendMessageStr(h, EM_REPLACESEL, 0, s);
            SendMessageInt(h, EM_SCROLLCARET, 0, 0);
        }

        void ScrollLogToEnd()
        {
            IntPtr h = log.Handle;
            int len = SendMessageInt(h, WM_GETTEXTLENGTH, 0, 0);
            SendMessageInt(h, EM_SETSEL, len, len);
            SendMessageInt(h, EM_SCROLLCARET, 0, 0);
        }

        static string ToCrLf(string s)
        {
            return s.Replace("\r", "").Replace("\n", "\r\n");
        }

        // ---- direct mode: the agent runs on the phone (WMAI.config has a key) ----
        Agent agent;
        int lastFlush;

        void InitChat()
        {
            AgentConfig cfg = null;
            try { cfg = AgentConfig.Load(Path.Combine(AppDir(), "WMAI.config")); }
            catch (Exception ex) { Post("[WMAI.config: " + ex.Message + "]\r\n"); }
            if (cfg == null) // relay mode
            {
                if (!File.Exists(Path.Combine(AppDir(), "WMAI.config")))
                    Post("No WMAI.config yet. To talk to the AI directly, copy WMAI.config.example to WMAI.config " +
                         "(next to WMAI.exe) and put your API key in it, then restart WMAI. Trying the PC relay at " +
                         host + ":" + port + "...\r\n");
                LoadHistory();
                return;
            }
            try
            {
                agent = new Agent(cfg, tools, AppDir());
                ShowDirectHistory();
            }
            catch (Exception ex)
            {
                Post("[direct mode failed, using relay: " + ex.GetType().Name + ": " + ex.Message + "]\r\n");
                LoadHistory();
            }
        }

        void ShowDirectHistory()
        {
            string h = agent.Transcript();
            Replace("WMAI " + Version + " - direct: " + agent.Model + "\r\n" +
                    (h.Length == 0 ? "New chat.\r\n" : ToCrLf(h) + "\r\n"));
        }

        void ConverseDirect(string q)
        {
            try
            {
                lastFlush = Environment.TickCount;
                agent.RunTurn(q, new TextHandler(ShowThrottled));
            }
            catch (Exception ex)
            {
                Post("\r\n[" + ex.GetType().Name + ": " + ex.Message + "]");
            }
            finally
            {
                lock (sync) turnDone = true;
                Invoke(new EventHandler(Flush));
            }
        }

        // Streaming deltas are a few characters each; redrawing the log for each
        // one is slow on this CPU, so the UI is updated at most every 300 ms.
        void ShowThrottled(string text)
        {
            lock (sync) pendingOut.Append(ToCrLf(text));
            if (Environment.TickCount - lastFlush >= 300)
            {
                lastFlush = Environment.TickCount;
                Invoke(new EventHandler(Flush));
            }
        }

        // ---- worker threads ----
        void LoadHistory()
        {
            try
            {
                string h = HttpText("GET", "/api/history", null);
                Replace(h.Length == 0 ? "New chat.\r\n" : ToCrLf(h) + "\r\n");
            }
            catch (Exception ex)
            {
                Post("[cannot reach relay: " + ex.Message + "]\r\n");
            }
        }

        void Converse(string q)
        {
            try
            {
                string r = HttpText("POST", "/api/send", q);
                if (r.Trim() != "OK") { Post("[relay busy]"); return; }
                int got = 0;
                string lastToolId = null;
                while (true)
                {
                    Thread.Sleep(500);
                    string resp = HttpText("GET", "/api/reply?from=" + got, null);
                    int nl = resp.IndexOf('\n');
                    string head = nl < 0 ? resp : resp.Substring(0, nl);
                    string text = nl < 0 ? "" : resp.Substring(nl + 1);
                    if (text.Length > 0) { got += text.Length; Post(ToCrLf(text)); }

                    if (head == "T")
                    {
                        // The model asked for a tool; the relay hands it to us.
                        // Run each id once, post the result, then keep polling.
                        string id, name;
                        Dictionary<string, string> args;
                        if (ParseTool(HttpText("GET", "/api/tool", null), out id, out name, out args))
                        {
                            string result;
                            if (id == lastToolId)
                                result = null; // already ran this one; do not rerun
                            else
                            {
                                lastToolId = id;
                                try { result = tools.Execute(name, args); }
                                catch (Exception tex) { result = "error: " + tex.GetType().Name + ": " + tex.Message; }
                            }
                            if (result != null)
                                HttpText("POST", "/api/tool?id=" + id, result);
                        }
                        continue;
                    }
                    if (head == "B") continue;
                    if (head.StartsWith("E")) Post("\r\n[error: " + head.Substring(1).Trim() + "]");
                    break;
                }
            }
            catch (Exception ex)
            {
                Post("\r\n[" + ex.GetType().Name + ": " + ex.Message + "]");
            }
            finally
            {
                lock (sync) turnDone = true;
                Invoke(new EventHandler(Flush));
            }
        }

        // Parses the relay's tool block:
        //   <id>\n<name>\nkey=value...   (values escaped: \\ \n \r)
        static bool ParseTool(string resp, out string id, out string name, out Dictionary<string, string> args)
        {
            id = "";
            name = "";
            args = new Dictionary<string, string>();
            if (resp == null) return false;
            resp = resp.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = resp.Split('\n');
            if (lines.Length < 2) return false;
            id = lines[0];
            name = lines[1];
            if (id.Length == 0 || name.Length == 0) return false;
            for (int i = 2; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                args[line.Substring(0, eq)] = Unescape(line.Substring(eq + 1));
            }
            return true;
        }

        static string Unescape(string s)
        {
            StringBuilder b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == '\\') b.Append('\\');
                    else if (n == 'n') b.Append('\n');
                    else if (n == 'r') b.Append('\r');
                    else { b.Append('\\'); b.Append(n); }
                }
                else b.Append(c);
            }
            return b.ToString();
        }

        // Yes/No prompt, marshalled to the UI thread from the tool worker.
        // Hide WMAI so a screenshot shows the app underneath, then bring it back.
        void SetWmaiVisible(bool visible)
        {
            if (InvokeRequired)
            {
                Invoke(new VisibilityHandler(SetWmaiVisible), new object[] { visible });
                return;
            }
            if (visible)
            {
                Show();
                BringToFront();
            }
            else Hide();
        }

        bool Ask(string message)
        {
            if (InvokeRequired)
                return (bool)Invoke(new ApproveHandler(Ask), new object[] { message });
            return MessageBox.Show(message, "WMAI", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        void Post(string s)
        {
            lock (sync) pendingOut.Append(s);
            Invoke(new EventHandler(Flush));
        }

        void Replace(string s)
        {
            lock (sync) { pendingReplace = s; pendingOut.Length = 0; }
            Invoke(new EventHandler(Flush));
        }

        void Flush(object sender, EventArgs e)
        {
            string s, rep;
            bool done;
            lock (sync)
            {
                s = pendingOut.ToString();
                pendingOut.Length = 0;
                rep = pendingReplace;
                pendingReplace = null;
                done = turnDone;
                turnDone = false;
            }
            if (rep != null) { log.Text = ""; Append(rep); }
            if (s.Length > 0) Append(s);
            if (done)
            {
                Append("\r\n");
                busy = false;
                sendItem.Enabled = true;
            }
        }

        // ---- self-update ----
        // Download the new build next to us as WMAI_new.exe and start it with
        // "/install <our path>". It waits for us to exit, copies itself over
        // WMAI.exe and relaunches it; the relaunched app deletes WMAI_new.exe.
        void OnUpdate(object sender, EventArgs e)
        {
            if (busy) return;
            Append("\r\n[downloading update...]\r\n");
            byte[] exe;
            try { exe = Http("GET", "/api/app", null); }
            catch (Exception ex) { Append("[update failed: " + ex.Message + "]\r\n"); return; }
            if (exe.Length < 1024 || exe[0] != 'M' || exe[1] != 'Z')
            {
                Append("[update failed: not an exe]\r\n");
                return;
            }
            string newPath = Path.Combine(AppDir(), "WMAI_new.exe");
            try
            {
                using (FileStream fs = new FileStream(newPath, FileMode.Create))
                    fs.Write(exe, 0, exe.Length);
                Process.Start(newPath, "/install \"" + ExePath() + "\"");
            }
            catch (Exception ex) { Append("[update failed: " + ex.Message + "]\r\n"); return; }
            Close();
        }

        static void Install(string target)
        {
            string self = ExePath();
            for (int i = 0; i < 40; i++) // up to ~20s for the old instance to exit
            {
                try
                {
                    File.Copy(self, target, true);
                    Process.Start(target, "");
                    return;
                }
                catch (Exception) { Thread.Sleep(500); }
            }
            MessageBox.Show("WMAI update could not replace " + target, "WMAI");
        }

        void CleanupUpdate()
        {
            string p = Path.Combine(AppDir(), "WMAI_new.exe");
            for (int i = 0; i < 20 && File.Exists(p); i++)
            {
                try { File.Delete(p); }
                catch (Exception) { Thread.Sleep(500); }
            }
        }

        // ---- HTTP ----
        string HttpText(string method, string path, string body)
        {
            byte[] b = Http(method, path, body);
            return Encoding.UTF8.GetString(b, 0, b.Length);
        }

        // Minimal HTTP/1.0 client: one request per connection, body is UTF-8.
        byte[] Http(string method, string path, string body)
        {
            byte[] data = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            TcpClient tc = new TcpClient();
            try
            {
                // Connect by IPEndPoint when we have a numeric address: passing a
                // host string makes WM5 attempt name resolution first (~4-5 s).
                IPAddress ip = null;
                try { ip = IPAddress.Parse(host); }
                catch (Exception) { }
                if (ip != null) tc.Connect(new IPEndPoint(ip, port));
                else tc.Connect(host, port);

                NetworkStream ns = tc.GetStream();
                string head = method + " " + path + " HTTP/1.0\r\nHost: " + host +
                    "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + data.Length + "\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(head);
                ns.Write(hb, 0, hb.Length);
                if (data.Length > 0) ns.Write(data, 0, data.Length);

                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[4096];
                int n;
                while ((n = ns.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                byte[] all = ms.ToArray();

                int sep = -1;
                for (int i = 0; i + 3 < all.Length; i++)
                    if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { sep = i; break; }
                if (sep < 0) throw new IOException("bad HTTP response");
                string headers = Encoding.ASCII.GetString(all, 0, sep);
                if (headers.IndexOf(" 200 ") < 0)
                    throw new IOException(headers.Split('\r')[0]);
                byte[] result = new byte[all.Length - sep - 4];
                Array.Copy(all, sep + 4, result, 0, result.Length);
                return result;
            }
            finally
            {
                tc.Close();
            }
        }

        [MTAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "/install")
            {
                // Rejoin in case the quoted path (it has a space) was split.
                Install(string.Join(" ", args, 1, args.Length - 1).Trim('"'));
                return;
            }
            Application.Run(new MainForm());
        }
    }
}
