// Tools.cs - the phone-side tool implementations for the WMAI relay.
//
// relay/tools.py offers these tools to the model; the phone runs them and posts
// the result back (see Converse() in WMAI.cs). Everything here compiles against
// desktop .NET 2.0 and must exist in NETCF 3.5 (see docs/DEVELOPMENT.md). Native calls
// go to coredll.dll / sms.dll and are wrapped so a missing API returns
// "error: ..." instead of killing the app.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WMAI
{
    // Shows a Yes/No prompt on the UI thread; true if the user agreed.
    public delegate bool ApproveHandler(string message);

    public class Tools
    {
        readonly ApproveHandler approve;
        readonly CodeTools code;  // project_* and build (on-device compiler)
        readonly WebTools web;    // web_fetch, web_search
        public readonly ImageTools Images = new ImageTools(); // screenshot, view_image

        public Tools(ApproveHandler approve)
        {
            this.approve = approve;
            code = new CodeTools(approve);
            web = new WebTools(AppDir());
        }

        // "Type: message" for errors shown to the model/user. NETCF's exception
        // texts are an optional package that is not installed on the phone; its
        // placeholder text says nothing, so only the type is kept then.
        public static string Describe(Exception ex)
        {
            string msg = ex.Message ?? "";
            if (msg.IndexOf("NETCFv35.Messages") >= 0 || msg.IndexOf("optional and are not currently installed") >= 0) msg = "";
            Exception inner = ex.InnerException;
            return ex.GetType().Name + (msg.Length > 0 ? ": " + msg : "") + (inner != null ? " (" + Describe(inner) + ")" : "");
        }

        // Folder of the running exe (templates live next to it).
        static string AppDir()
        {
            string p = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase;
            if (p.StartsWith("file:///")) p = p.Substring(8).Replace('/', '\\');
            return Path.GetDirectoryName(p);
        }

        bool Ask(string message)
        {
            return approve != null && approve(message);
        }

        public string Execute(string name, Dictionary<string, string> args)
        {
            if (args == null) args = new Dictionary<string, string>();
            try
            {
                switch (name)
                {
                    case "device_info": return DeviceInfo();
                    case "fs_list": return FsList(args);
                    case "fs_read": return FsRead(args);
                    case "fs_write": return FsWrite(args);
                    case "fs_delete": return FsDelete(args);
                    case "fs_move": return FsMove(args);
                    case "fs_copy": return FsCopy(args);
                    case "fs_mkdir": return FsMkdir(args);
                    case "registry_read": return RegistryRead(args);
                    case "process_list": return ProcessList();
                    case "run_program": return RunProgram(args);
                    case "kill_process": return KillProcess(args);
                    case "contacts_search": return ContactsSearch(args);
                    case "sms_list": return SmsList(args);
                    case "sms_send": return SmsSend(args);
                    case "phone_call": return PhoneCall(args);
                    case "project_new": return code.New(args, AppDir());
                    case "project_list": return code.List(args);
                    case "project_read": return code.Read(args);
                    case "project_write": return code.Write(args);
                    case "project_edit": return code.Edit(args);
                    case "build": return code.Build(args);
                    case "web_fetch": return web.Fetch(args);
                    case "web_search": return web.Search(args);
                    case "screenshot": return Images.Screenshot(args);
                    case "view_image": return Images.ViewImage(args);
                    default: return "error: unknown tool " + name;
                }
            }
            catch (Exception ex)
            {
                return "error: " + Describe(ex);
            }
        }

        // ---- argument helpers ----
        static string Str(Dictionary<string, string> a, string k, string def)
        {
            string v;
            if (a.TryGetValue(k, out v) && v != null) return v;
            return def;
        }

        static int Int(Dictionary<string, string> a, string k, int def)
        {
            string v;
            if (a.TryGetValue(k, out v) && v != null)
            {
                // NETCF 3.5 has no Int32.TryParse (MissingMethodException on the device).
                try { return int.Parse(v.Trim()); }
                catch (Exception) { }
            }
            return def;
        }

        static bool Bool(Dictionary<string, string> a, string k, bool def)
        {
            string v;
            if (a.TryGetValue(k, out v) && v != null)
            {
                v = v.Trim().ToLower();
                if (v == "true" || v == "1") return true;
                if (v == "false" || v == "0") return false;
            }
            return def;
        }

        static string Clip(string s, int max)
        {
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\n...(truncated)";
        }

        // ---- device_info ----
        string DeviceInfo()
        {
            StringBuilder b = new StringBuilder();

            string os = null;
            try
            {
                OSVERSIONINFO vi = new OSVERSIONINFO();
                vi.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFO));
                if (GetVersionEx(ref vi))
                    os = "Windows CE " + vi.dwMajorVersion + "." + vi.dwMinorVersion + " build " + vi.dwBuildNumber;
            }
            catch (Exception) { }
            if (os == null) os = "Windows CE (version query failed)";
            b.Append("OS: ").Append(os).Append('\n');
            b.Append("Time: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');

            try
            {
                MEMORYSTATUS mem = new MEMORYSTATUS();
                mem.dwLength = Marshal.SizeOf(typeof(MEMORYSTATUS));
                GlobalMemoryStatus(ref mem);
                b.Append("Memory: ").Append(mem.dwAvailPhys / 1024).Append(" KB free / ")
                 .Append(mem.dwTotalPhys / 1024).Append(" KB\n");
            }
            catch (Exception) { }

            AppendStorage(b, "\\", "\\");
            AppendStorage(b, "\\Storage Card", "Storage Card");
            AppendStorage(b, "\\SD Card", "SD Card");

            try
            {
                SYSTEM_POWER_STATUS_EX ps = new SYSTEM_POWER_STATUS_EX();
                if (GetSystemPowerStatusEx(ref ps, true))
                {
                    b.Append("Battery: ");
                    if (ps.BatteryLifePercent == 255) b.Append("unknown");
                    else b.Append(ps.BatteryLifePercent).Append('%');
                    b.Append(ps.ACLineStatus == 1 ? ", on AC" : ", discharging").Append('\n');
                }
            }
            catch (Exception) { }

            object sig = RegValue("HKLM\\System\\State\\Phone", "Signal Strength");
            object op = RegValue("HKLM\\System\\State\\Phone", "Current Operator Name");
            if (sig != null || op != null)
            {
                b.Append("Phone: ");
                if (sig != null) b.Append("signal ").Append(FmtReg(sig));
                if (op != null)
                {
                    if (sig != null) b.Append(", ");
                    b.Append("operator ").Append(FmtReg(op));
                }
                b.Append('\n');
            }

            object unread = RegValue("HKCU\\System\\State\\Messages\\sms\\Unread", "Count");
            if (unread == null) unread = RegValue("HKLM\\System\\State\\Messages\\sms\\Unread", "Count");
            if (unread != null) b.Append("Unread SMS: ").Append(FmtReg(unread)).Append('\n');

            return b.ToString();
        }

        static void AppendStorage(StringBuilder b, string path, string label)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                long freeToCaller, total, free;
                if (!GetDiskFreeSpaceEx(path, out freeToCaller, out total, out free)) return;
                b.Append("Storage ").Append(label).Append(": ").Append(free / 1024).Append(" KB free / ")
                 .Append(total / 1024).Append(" KB\n");
            }
            catch (Exception) { }
        }

        // ---- filesystem ----
        string FsList(Dictionary<string, string> a)
        {
            string path = Str(a, "path", "\\");
            if (path.Length == 0) path = "\\";
            if (!Directory.Exists(path))
            {
                if (File.Exists(path)) return "error: " + path + " is a file, not a directory";
                return "error: no such directory: " + path;
            }
            string pattern = Str(a, "pattern", "*");
            if (pattern.Length == 0) pattern = "*";

            StringBuilder b = new StringBuilder();
            b.Append(path).Append(":\n");
            string[] dirs;
            try { dirs = Directory.GetDirectories(path); }
            catch (Exception e) { return "error: " + e.Message; }
            Array.Sort(dirs);
            for (int i = 0; i < dirs.Length; i++)
                b.Append("<dir>  ").Append(NameOf(dirs[i])).Append('\n');

            string[] files;
            try { files = Directory.GetFiles(path, pattern); }
            catch (Exception) { try { files = Directory.GetFiles(path); } catch (Exception e) { return "error: " + e.Message; } }
            Array.Sort(files);
            for (int i = 0; i < files.Length; i++)
            {
                long len = -1;
                try { len = new FileInfo(files[i]).Length; } catch (Exception) { }
                b.Append(len >= 0 ? len.ToString() : "?").Append("  ").Append(NameOf(files[i])).Append('\n');
            }
            if (dirs.Length == 0 && files.Length == 0) b.Append("(empty)\n");
            return Clip(b.ToString(), 8000);
        }

        static string NameOf(string p)
        {
            int i = p.LastIndexOf('\\');
            return i >= 0 && i + 1 < p.Length ? p.Substring(i + 1) : p;
        }

        string FsRead(Dictionary<string, string> a)
        {
            string path = Str(a, "path", null);
            if (path == null || path.Length == 0) return "error: 'path' required";
            if (!File.Exists(path)) return "error: no such file: " + path;
            int offset = Int(a, "offset", 0);
            if (offset < 0) offset = 0;
            int max = Int(a, "max_chars", 4000);
            if (max < 1) max = 1;
            if (max > 12000) max = 12000;

            const int cap = 262144; // never load more than 256 KB into 44 MB of RAM
            long total = 0;
            byte[] bytes;
            try { total = new FileInfo(path).Length; } catch (Exception) { }
            try { bytes = ReadCapped(path, cap); }
            catch (Exception e) { return "error: " + e.Message; }

            bool binary = false;
            int scan = bytes.Length < 512 ? bytes.Length : 512;
            for (int i = 0; i < scan; i++) if (bytes[i] == 0) { binary = true; break; }
            if (binary) return HexDump(bytes, offset, Math.Min(max, 1024));

            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            else
                text = Encoding.UTF8.GetString(bytes, 0, bytes.Length);

            if (offset >= text.Length) return "(end of file; " + text.Length + " chars read)";
            int len = text.Length - offset;
            if (len > max) len = max;
            string chunk = text.Substring(offset, len);
            string more = offset + len < text.Length ? "\n...(" + (text.Length - offset - len) + " more chars)" : "";
            if (total > bytes.Length) more += "\n...(file is " + total + " bytes; only the first " + bytes.Length + " were read)";
            return chunk + more;
        }

        static byte[] ReadCapped(string path, int cap)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int len = (int)Math.Min(fs.Length, cap);
                byte[] buf = new byte[len];
                int off = 0;
                while (off < len)
                {
                    int n = fs.Read(buf, off, len - off);
                    if (n <= 0) break;
                    off += n;
                }
                if (off == len) return buf;
                byte[] t = new byte[off];
                Array.Copy(buf, t, off);
                return t;
            }
        }

        static string HexDump(byte[] data, int offset, int max)
        {
            if (offset > data.Length) offset = data.Length;
            int end = offset + max;
            if (end > data.Length) end = data.Length;
            StringBuilder b = new StringBuilder();
            for (int i = offset; i < end; i += 16)
            {
                b.Append(i.ToString("x8")).Append("  ");
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < end) b.Append(data[i + j].ToString("x2")).Append(' ');
                    else b.Append("   ");
                }
                b.Append(' ');
                for (int j = 0; j < 16 && i + j < end; j++)
                {
                    byte c = data[i + j];
                    b.Append(c >= 32 && c < 127 ? (char)c : '.');
                }
                b.Append('\n');
            }
            if (end < data.Length) b.Append("...(").Append(data.Length - end).Append(" more bytes)");
            return b.ToString();
        }

        string FsWrite(Dictionary<string, string> a)
        {
            string path = Str(a, "path", null);
            if (path == null || path.Length == 0) return "error: 'path' required";
            string content = Str(a, "content", "");
            bool append = Bool(a, "append", false);
            if (!Ask((append ? "Append " : "Write ") + content.Length + " chars to\n" + path + "?")) return "denied";
            try
            {
                using (StreamWriter w = new StreamWriter(path, append, Encoding.UTF8))
                    w.Write(content);
            }
            catch (Exception e) { return "error: " + e.Message; }
            return (append ? "appended " : "wrote ") + content.Length + " chars to " + path;
        }

        string FsDelete(Dictionary<string, string> a)
        {
            string path = Str(a, "path", null);
            if (path == null || path.Length == 0) return "error: 'path' required";
            try
            {
                if (File.Exists(path))
                {
                    if (!Ask("Delete file\n" + path + "?")) return "denied";
                    File.Delete(path);
                    return "deleted " + path;
                }
                if (Directory.Exists(path))
                {
                    if (Directory.GetFiles(path).Length > 0 || Directory.GetDirectories(path).Length > 0)
                        return "error: directory not empty: " + path;
                    if (!Ask("Delete empty directory\n" + path + "?")) return "denied";
                    Directory.Delete(path);
                    return "deleted directory " + path;
                }
            }
            catch (Exception e) { return "error: " + e.Message; }
            return "error: not found: " + path;
        }

        string FsMove(Dictionary<string, string> a)
        {
            string src = Str(a, "src", null);
            string dst = Str(a, "dst", null);
            if (src == null || src.Length == 0 || dst == null || dst.Length == 0)
                return "error: 'src' and 'dst' required";
            if (!Ask("Move/rename\n" + src + "\nto\n" + dst + "?")) return "denied";
            try
            {
                if (File.Exists(src)) { File.Move(src, dst); return "moved file to " + dst; }
                if (Directory.Exists(src)) { Directory.Move(src, dst); return "moved directory to " + dst; }
            }
            catch (Exception e) { return "error: " + e.Message; }
            return "error: not found: " + src;
        }

        string FsCopy(Dictionary<string, string> a)
        {
            string src = Str(a, "src", null);
            string dst = Str(a, "dst", null);
            if (src == null || src.Length == 0 || dst == null || dst.Length == 0)
                return "error: 'src' and 'dst' required";
            if (!File.Exists(src)) return "error: no such file: " + src;
            if (File.Exists(dst) || Directory.Exists(dst)) return "error: destination exists: " + dst;
            try { File.Copy(src, dst); }
            catch (Exception e) { return "error: " + e.Message; }
            return "copied to " + dst;
        }

        string FsMkdir(Dictionary<string, string> a)
        {
            string path = Str(a, "path", null);
            if (path == null || path.Length == 0) return "error: 'path' required";
            try { Directory.CreateDirectory(path); }
            catch (Exception e) { return "error: " + e.Message; }
            return "created " + path;
        }

        // ---- registry ----
        string RegistryRead(Dictionary<string, string> a)
        {
            string key = Str(a, "key", null);
            if (key == null || key.Trim().Length == 0) return "error: 'key' required";
            bool owned;
            RegistryKey k = OpenKey(key, out owned);
            if (k == null) return "error: cannot open " + key;
            try
            {
                string val = Str(a, "value", null);
                if (val != null && val.Length > 0)
                {
                    object v = k.GetValue(val);
                    if (v == null) return "error: no value '" + val + "'";
                    return val + " = " + FmtReg(v);
                }
                StringBuilder b = new StringBuilder();
                string[] names = k.GetValueNames();
                for (int i = 0; i < names.Length; i++)
                    b.Append(names[i]).Append(" = ").Append(FmtReg(k.GetValue(names[i]))).Append('\n');
                string[] subs = k.GetSubKeyNames();
                if (subs.Length > 0)
                {
                    b.Append("subkeys: ");
                    for (int i = 0; i < subs.Length; i++)
                    {
                        if (i > 0) b.Append(", ");
                        b.Append(subs[i]);
                    }
                    b.Append('\n');
                }
                return b.Length == 0 ? "(empty key)" : Clip(b.ToString(), 6000);
            }
            finally
            {
                if (owned) k.Close();
            }
        }

        static RegistryKey OpenKey(string path, out bool owned)
        {
            owned = false;
            path = path.Trim().Replace('/', '\\');
            while (path.Length > 0 && path[0] == '\\') path = path.Substring(1);
            string[] parts = path.Split('\\');
            RegistryKey root;
            switch (parts[0].ToUpper())
            {
                case "HKLM":
                case "HKEY_LOCAL_MACHINE": root = Registry.LocalMachine; break;
                case "HKCU":
                case "HKEY_CURRENT_USER": root = Registry.CurrentUser; break;
                case "HKCR":
                case "HKEY_CLASSES_ROOT": root = Registry.ClassesRoot; break;
                case "HKU":
                case "HKEY_USERS": root = Registry.Users; break;
                default: return null;
            }
            StringBuilder sub = new StringBuilder();
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                if (sub.Length > 0) sub.Append('\\');
                sub.Append(parts[i]);
            }
            if (sub.Length == 0) return root;
            RegistryKey k = root.OpenSubKey(sub.ToString());
            owned = true;
            return k;
        }

        static string FmtReg(object v)
        {
            if (v == null) return "(null)";
            byte[] b = v as byte[];
            if (b != null)
            {
                StringBuilder s = new StringBuilder("hex:");
                for (int i = 0; i < b.Length && i < 64; i++)
                    s.Append(' ').Append(b[i].ToString("x2"));
                if (b.Length > 64) s.Append(" ...");
                return s.ToString();
            }
            return v.ToString();
        }

        static object RegValue(string keyPath, string valueName)
        {
            bool owned;
            RegistryKey k = OpenKey(keyPath, out owned);
            if (k == null) return null;
            try { return k.GetValue(valueName); }
            catch (Exception) { return null; }
            finally { if (owned) k.Close(); }
        }

        // ---- processes ----
        string ProcessList()
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS | TH32CS_SNAPNOHEAPS, 0);
            if (snap == INVALID_HANDLE_VALUE) return "error: cannot snapshot processes";
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                StringBuilder b = new StringBuilder();
                if (Process32First(snap, ref pe))
                {
                    do
                    {
                        b.Append(pe.th32ProcessID.ToString()).Append("\t").Append(pe.cntThreads.ToString()).Append("\t")
                         .Append(pe.szExeFile).Append('\n');
                        pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                    } while (Process32Next(snap, ref pe));
                }
                return b.Length == 0 ? "(no processes)" : Clip(b.ToString(), 8000);
            }
            finally { CloseToolhelp32Snapshot(snap); }
        }

        string RunProgram(Dictionary<string, string> a)
        {
            string path = Str(a, "path", null);
            string args = Str(a, "args", "");
            if (path == null || path.Length == 0) return "error: 'path' required";
            string what = args.Length > 0 ? path + " " + args : path;
            if (!Ask("Launch\n" + what + "?")) return "denied";
            try
            {
                // NETCF has no Process.Start(string); the one-arg call made the whole
                // method fail to JIT (found by tools/cfcheck.ps1).
                Process.Start(path, args);
            }
            catch (Exception e) { return "error: " + e.Message; }
            return "launched " + what;
        }

        string KillProcess(Dictionary<string, string> a)
        {
            // CE process ids are 32-bit unsigned and often above int.MaxValue
            // (e.g. 3336040334), so parse as long.
            long pid = -1;
            try { pid = long.Parse(Str(a, "pid", "-1").Trim()); }
            catch (Exception) { }
            if (pid <= 0 || pid > uint.MaxValue) return "error: 'pid' required (a number from process_list)";
            if (!Ask("Terminate process pid " + pid + "?")) return "denied";
            IntPtr h = OpenProcess(PROCESS_TERMINATE, false, (uint)pid);
            if (h == IntPtr.Zero) return "error: cannot open pid " + pid;
            try
            {
                if (!TerminateProcess(h, 0)) return "error: terminate failed for pid " + pid;
            }
            finally { CloseHandle(h); }
            return "terminated pid " + pid;
        }

        // ---- contacts (POOM via pimstore.dll) ----
        // Windows Mobile ships pimstore.dll; its flat helper exports are exactly what
        // the managed PocketOutlook wrapper calls (see the WM5 SDK's pimstore.lib).
        // Calling them directly avoids deploying any managed assembly.
        const int PIMPR_FILEAS = (0x0080 << 16) | 0x1F;
        const int PIMPR_FIRST_NAME = (0x0082 << 16) | 0x1F;
        const int PIMPR_LAST_NAME = (0x0084 << 16) | 0x1F;
        const int PIMPR_COMPANY_NAME = (0x008A << 16) | 0x1F;
        const int PIMPR_JOB_TITLE = (0x008C << 16) | 0x1F;
        const int PIMPR_MOBILE_TELEPHONE_NUMBER = (0x0096 << 16) | 0x1F;
        const int PIMPR_BUSINESS_TELEPHONE_NUMBER = (0x0097 << 16) | 0x1F;
        const int PIMPR_HOME_TELEPHONE_NUMBER = (0x0099 << 16) | 0x1F;
        const int PIMPR_EMAIL1_ADDRESS = (0x0090 << 16) | 0x1F;
        const int PIMPR_DISPLAY_NAME = (0x10A4 << 16) | 0x1F;
        const int OL_FOLDER_CONTACTS = 10;

        string ContactsSearch(Dictionary<string, string> a)
        {
            string query = Str(a, "query", "");
            int max = Int(a, "max", 20);
            if (max < 1) max = 1;
            if (max > 200) max = 200;
            string q = query.Trim().ToLower();

            IntPtr app = IntPtr.Zero, folder = IntPtr.Zero, items = IntPtr.Zero;
            StringBuilder b = new StringBuilder();
            int shown = 0;
            try
            {
                int hr = PkCreate(ref app);
                if (hr != 0 || app == IntPtr.Zero) return "error: POOM create 0x" + Hex(hr);
                hr = PkLogon(app, 0);
                if (hr != 0) return "error: POOM logon 0x" + Hex(hr);
                hr = PkGetDefaultFolder(app, OL_FOLDER_CONTACTS, ref folder);
                if (hr != 0 || folder == IntPtr.Zero) return "error: contacts folder 0x" + Hex(hr);
                hr = PkGetItems(folder, ref items);
                if (hr != 0 || items == IntPtr.Zero) return "error: contacts items 0x" + Hex(hr);

                int count = 0;
                PkGetCount(items, ref count);
                for (int i = 0; i < count && shown < max; i++)
                {
                    IntPtr item = IntPtr.Zero;
                    if (PkItem(items, i, ref item) != 0 || item == IntPtr.Zero) continue;
                    try
                    {
                        string first = PkStr(app, item, PIMPR_FIRST_NAME);
                        string last = PkStr(app, item, PIMPR_LAST_NAME);
                        string fileAs = PkStr(app, item, PIMPR_FILEAS);
                        string company = PkStr(app, item, PIMPR_COMPANY_NAME);
                        string job = PkStr(app, item, PIMPR_JOB_TITLE);
                        string mobile = PkStr(app, item, PIMPR_MOBILE_TELEPHONE_NUMBER);
                        string biz = PkStr(app, item, PIMPR_BUSINESS_TELEPHONE_NUMBER);
                        string home = PkStr(app, item, PIMPR_HOME_TELEPHONE_NUMBER);
                        string email = PkStr(app, item, PIMPR_EMAIL1_ADDRESS);
                        if (fileAs.Length == 0 && first.Length == 0 && last.Length == 0 && company.Length == 0)
                            fileAs = PkStr(app, item, PIMPR_DISPLAY_NAME);

                        string name = (first + " " + last).Trim();
                        if (name.Length == 0) name = fileAs;
                        if (name.Length == 0) name = company;
                        if (name.Length == 0) name = "(unnamed)";

                        if (q.Length > 0)
                        {
                            string hay = (name + " " + company + " " + job + " " + mobile + " " +
                                          biz + " " + home + " " + email).ToLower();
                            if (hay.IndexOf(q) < 0) continue;
                        }
                        b.Append(name);
                        if (company.Length > 0) b.Append(" | ").Append(company);
                        if (job.Length > 0) b.Append(" (").Append(job).Append(')');
                        if (mobile.Length > 0) b.Append("\nmobile ").Append(mobile);
                        if (biz.Length > 0) b.Append("\nwork ").Append(biz);
                        if (home.Length > 0) b.Append("\nhome ").Append(home);
                        if (email.Length > 0) b.Append("\nemail ").Append(email);
                        b.Append('\n');
                        shown++;
                    }
                    finally { PkRelease(item); }
                }
                return b.Length == 0 ? "(no matching contacts)" : Clip(b.ToString(), 8000);
            }
            finally
            {
                if (items != IntPtr.Zero) PkRelease(items);
                if (folder != IntPtr.Zero) PkRelease(folder);
                if (app != IntPtr.Zero) { try { PkLogoff(app); } catch (Exception) { } PkRelease(app); }
            }
        }

        string PkStr(IntPtr app, IntPtr item, int propId)
        {
            IntPtr cePropVal = IntPtr.Zero, txt = IntPtr.Zero;
            try
            {
                if (PkGetStringProp(app, item, propId, ref cePropVal, ref txt) != 0 || txt == IntPtr.Zero) return "";
                return Marshal.PtrToStringUni(txt) ?? "";
            }
            catch (Exception) { return ""; }
            finally { if (cePropVal != IntPtr.Zero) { try { PkHeapFree(cePropVal); } catch (Exception) { } } }
        }

        // ---- SMS (CEMAPI via cemapi.dll) ----
        const uint MAPI_FOLDER_DRAFTS = 2164523266u;
        const uint MAPI_FOLDER_INBOX = 2164654338u;
        const uint MAPI_FOLDER_OUTBOX = 904003842u;
        const uint MAPI_FOLDER_WASTE = 904069378u;
        const uint MAPI_FOLDER_SENT = 904134914u;
        const uint MAPI_STORE_NAME = 2165768223u;
        const uint PR_SUBJECT_W = 3604511u;
        const uint PR_BODY_W = 268435487u;
        const uint PR_SENDER_NAME_W = 203030559u;
        const uint PR_SENDER_EMAIL_W = 203358239u;
        const uint PR_DELIVERY_TIME = 235274304u;

        string SmsList(Dictionary<string, string> a)
        {
            string folderName = Str(a, "folder", "Inbox").Trim().ToLower();
            uint folderTag = MAPI_FOLDER_INBOX;
            if (folderName.StartsWith("sent")) folderTag = MAPI_FOLDER_SENT;
            else if (folderName.StartsWith("draft")) folderTag = MAPI_FOLDER_DRAFTS;
            else if (folderName.StartsWith("outbox")) folderTag = MAPI_FOLDER_OUTBOX;
            else if (folderName.StartsWith("delet") || folderName.StartsWith("trash")) folderTag = MAPI_FOLDER_WASTE;

            int max = Int(a, "count", 10);
            if (max < 1) max = 1;
            if (max > 50) max = 50;

            IntPtr session = IntPtr.Zero, stores = IntPtr.Zero, store = IntPtr.Zero;
            IntPtr folder = IntPtr.Zero, contents = IntPtr.Zero;
            bool init = false;
            try
            {
                if (MInitialize(IntPtr.Zero) != 0) return "error: MAPIInitialize failed";
                init = true;
                int hr = MLogon(ref session);
                if (hr != 0 || session == IntPtr.Zero) return "error: MAPI logon 0x" + Hex(hr);
                hr = MGetStoresTable(session, ref stores);
                if (hr != 0 || stores == IntPtr.Zero) return "error: message stores 0x" + Hex(hr);

                while (true)
                {
                    IntPtr id = IntPtr.Zero;
                    uint size = 0;
                    if (MNextId(stores, ref id, ref size) != 0 || id == IntPtr.Zero) break;
                    IntPtr s = IntPtr.Zero;
                    if (MOpenStore(session, id, 20u, ref s) != 0 || s == IntPtr.Zero) continue;
                    string name = MStr(s, MAPI_STORE_NAME);
                    if (name.ToLower() == "sms") { store = s; break; }
                    PkRelease(s);
                }
                if (store == IntPtr.Zero) return "error: SMS account not found";

                hr = MGetFolder(store, folderTag, ref folder);
                if (hr != 0 || folder == IntPtr.Zero) return "error: open SMS folder 0x" + Hex(hr);
                hr = MGetContentsTable(folder, ref contents);
                if (hr != 0 || contents == IntPtr.Zero) return "error: SMS folder contents 0x" + Hex(hr);

                StringBuilder b = new StringBuilder();
                int shown = 0;
                while (shown < max)
                {
                    IntPtr id = IntPtr.Zero;
                    uint size = 0;
                    if (MNextId(contents, ref id, ref size) != 0 || id == IntPtr.Zero) break;
                    IntPtr msg = IntPtr.Zero;
                    uint objType = 0;
                    if (MGetMessage(session, id, size, ref objType, ref msg) != 0 || msg == IntPtr.Zero) continue;
                    try
                    {
                        string text = MStr(msg, PR_SUBJECT_W);
                        if (text.Length == 0) text = MStr(msg, PR_BODY_W);
                        string from = MStr(msg, PR_SENDER_NAME_W);
                        string addr = MStr(msg, PR_SENDER_EMAIL_W);
                        DateTime dt = MDate(msg, PR_DELIVERY_TIME);
                        if (dt != DateTime.MinValue) b.Append(dt.ToString("dd-MM HH:mm")).Append(' ');
                        if (from.Length > 0) b.Append(from);
                        else if (addr.Length > 0) b.Append(addr);
                        if (from.Length > 0 && addr.Length > 0 && !addr.Equals(from))
                            b.Append(" <").Append(addr).Append('>');
                        if (from.Length > 0 || addr.Length > 0) b.Append(": ");
                        b.Append(text).Append('\n');
                        shown++;
                    }
                    finally { PkRelease(msg); }
                }
                return b.Length == 0 ? "(no SMS messages)" : Clip(b.ToString(), 8000);
            }
            catch (Exception e) { return "error: " + e.GetType().Name + ": " + e.Message; }
            finally
            {
                if (contents != IntPtr.Zero) PkRelease(contents);
                if (folder != IntPtr.Zero) PkRelease(folder);
                if (store != IntPtr.Zero) PkRelease(store);
                if (stores != IntPtr.Zero) PkRelease(stores);
                if (session != IntPtr.Zero) { try { MLogoff(ref session); } catch (Exception) { } }
                if (init) { try { MUninitialize(); } catch (Exception) { } }
            }
        }

        string MStr(IntPtr owner, uint tag)
        {
            IntPtr lpszW = IntPtr.Zero, toFree = IntPtr.Zero;
            try
            {
                if (MGetStringProp(owner, tag, ref lpszW, ref toFree) != 0 || lpszW == IntPtr.Zero) return "";
                return Marshal.PtrToStringUni(lpszW) ?? "";
            }
            catch (Exception) { return ""; }
            finally { if (toFree != IntPtr.Zero) { try { MFreeBuffer(ref toFree); } catch (Exception) { } } }
        }

        static DateTime MDate(IntPtr owner, uint tag)
        {
            uint year = 0, month = 0, dow = 0, day = 0, hour = 0, minute = 0, second = 0, ms = 0;
            try
            {
                if (MGetDateProp(owner, tag, ref year, ref month, ref dow, ref day, ref hour, ref minute, ref second, ref ms) != 0)
                    return DateTime.MinValue;
            }
            catch (Exception) { return DateTime.MinValue; }
            try { return new DateTime((int)year, (int)month, (int)day, (int)hour, (int)minute, (int)second, (int)ms); }
            catch (Exception) { return DateTime.MinValue; }
        }

        static string Hex(int hr) { return ((uint)hr).ToString("X8"); }

        string SmsSend(Dictionary<string, string> a)
        {
            string to = Str(a, "to", null);
            string text = Str(a, "text", "");
            if (to == null || to.Length == 0) return "error: 'to' required";
            to = StripNumber(to);
            if (!Ask("Send SMS to " + to + "?\n\n" + text)) return "denied";
            int hr;
            try { hr = SendSmsHelper(to, text, false); }
            catch (Exception e) { return "error: SMS API unavailable (" + e.GetType().Name + ")"; }
            if (hr == -2147467230) return "error: cannot load sms.dll";
            if (hr != 0) return "error: SendSMSMessage 0x" + Hex(hr);
            return "sent SMS to " + to;
        }

        static string StripNumber(string s)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '+' || c == '*' || c == '#') b.Append(c);
            }
            return b.Length > 0 ? b.ToString() : s.Trim();
        }

        string PhoneCall(Dictionary<string, string> a)
        {
            string number = Str(a, "number", null);
            if (number == null || number.Length == 0) return "error: 'number' required";
            number = StripNumber(number);
            if (!Ask("Dial " + number + "?")) return "denied";

            PHONEMAKECALLINFO info = new PHONEMAKECALLINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(PHONEMAKECALLINFO));
            info.pszDestAddress = number;
            info.pszCalledParty = number;
            int rc;
            try { rc = PhoneMakeCallCore(ref info); }
            catch (Exception)
            {
                try { rc = PhoneMakeCallDll(ref info); }
                catch (Exception e) { return "error: phone API unavailable (" + e.GetType().Name + ")"; }
            }
            return rc == 0 ? "dialing " + number : "error: PhoneMakeCall " + rc;
        }

        // ---- native structures and imports ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OSVERSIONINFO
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUS
        {
            public int dwLength;
            public int dwMemoryLoad;
            public int dwTotalPhys;
            public int dwAvailPhys;
            public int dwTotalPageFile;
            public int dwAvailPageFile;
            public int dwTotalVirtual;
            public int dwAvailVirtual;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct SYSTEM_POWER_STATUS_EX
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
            public byte Reserved2;
            public byte BackupBatteryFlag;
            public byte BackupBatteryLifePercent;
            public byte Reserved3;
            public int BackupBatteryLifeTime;
            public int BackupBatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public uint th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
            public uint th32MemoryBase; // CE-only trailing fields; dwSize must include them
            public uint th32AccessKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHONEMAKECALLINFO
        {
            public uint cbSize;
            public uint dwFlags;
            public string pszDestAddress;
            public string pszAppName;
            public string pszCalledParty;
            public string pszComment;
        }

        const uint TH32CS_SNAPPROCESS = 0x00000002;
        const uint TH32CS_SNAPNOHEAPS = 0x40000000; // CE: skip heap data (slow, memory hungry)
        const uint PROCESS_TERMINATE = 0x0001;
        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("coredll.dll", CharSet = CharSet.Unicode)]
        static extern bool GetVersionEx(ref OSVERSIONINFO lpVersionInformation);
        [DllImport("coredll.dll", ExactSpelling = true)]
        static extern void GlobalMemoryStatus(ref MEMORYSTATUS lpBuffer);
        [DllImport("coredll.dll", CharSet = CharSet.Unicode)]
        static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out long lpFreeBytesAvailableToCaller,
                                              out long lpTotalNumberOfBytes, out long lpTotalNumberOfFreeBytes);
        [DllImport("coredll.dll", ExactSpelling = true)]
        static extern bool GetSystemPowerStatusEx(ref SYSTEM_POWER_STATUS_EX pSystemPowerStatus, bool fUpdate);

        // On CE 5 / WM5 the ToolHelp API lives in toolhelp.dll, not coredll.dll.
        [DllImport("toolhelp.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
        [DllImport("toolhelp.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [DllImport("toolhelp.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [DllImport("toolhelp.dll", ExactSpelling = true)]
        static extern bool CloseToolhelp32Snapshot(IntPtr hSnapshot);
        [DllImport("coredll.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("coredll.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
        [DllImport("coredll.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("coredll.dll", EntryPoint = "PhoneMakeCall", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int PhoneMakeCallCore(ref PHONEMAKECALLINFO ppci);
        [DllImport("phone.dll", EntryPoint = "PhoneMakeCall", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int PhoneMakeCallDll(ref PHONEMAKECALLINFO ppci);

        // POOM (pimstore.dll) flat helpers. Ordinals are from the WM5 SDK's pimstore.lib
        // and match what Microsoft.WindowsMobile.PocketOutlook.dll imports.
        [DllImport("pimstore.dll", EntryPoint = "#78", SetLastError = true)]
        static extern int PkCreate(ref IntPtr application);
        [DllImport("pimstore.dll", EntryPoint = "#85", SetLastError = true)]
        static extern int PkLogon(IntPtr self, int windowHandle);
        [DllImport("pimstore.dll", EntryPoint = "#84", SetLastError = true)]
        static extern int PkLogoff(IntPtr self);
        [DllImport("pimstore.dll", EntryPoint = "#81", SetLastError = true)]
        static extern int PkGetDefaultFolder(IntPtr self, int folderType, ref IntPtr folder);
        [DllImport("pimstore.dll", EntryPoint = "#60", SetLastError = true)]
        static extern int PkGetItems(IntPtr folder, ref IntPtr collection);
        [DllImport("pimstore.dll", EntryPoint = "#70", SetLastError = true)]
        static extern int PkGetCount(IntPtr self, ref int count);
        [DllImport("pimstore.dll", EntryPoint = "#66", SetLastError = true)]
        static extern int PkItem(IntPtr self, int index, ref IntPtr item);
        [DllImport("pimstore.dll", EntryPoint = "#39", SetLastError = true)]
        static extern int PkGetStringProp(IntPtr poomStore, IntPtr item, int propId, ref IntPtr cePropVal, ref IntPtr text);
        [DllImport("pimstore.dll", EntryPoint = "#83", SetLastError = true)]
        static extern int PkHeapFree(IntPtr p);
        [DllImport("pimstore.dll", EntryPoint = "#38", SetLastError = true)]
        static extern int PkRelease(IntPtr p);

        // CEMAPI (cemapi.dll) flat helpers. Ordinals are from the WM5 SDK's cemapi.lib
        // and match what Microsoft.WindowsMobile.PocketOutlook.dll imports.
        [DllImport("cemapi.dll", EntryPoint = "MAPIInitialize", SetLastError = true)]
        static extern int MInitialize(IntPtr pNotUsed);
        [DllImport("cemapi.dll", EntryPoint = "MAPIUninitialize", SetLastError = true)]
        static extern void MUninitialize();
        [DllImport("cemapi.dll", EntryPoint = "#20", SetLastError = true)]
        static extern int MLogon(ref IntPtr session);
        [DllImport("cemapi.dll", EntryPoint = "#21", SetLastError = true)]
        static extern int MLogoff(ref IntPtr session);
        [DllImport("cemapi.dll", EntryPoint = "#23", SetLastError = true)]
        static extern int MGetStoresTable(IntPtr session, ref IntPtr table);
        [DllImport("cemapi.dll", EntryPoint = "#24", SetLastError = true)]
        static extern int MOpenStore(IntPtr session, IntPtr itemId, uint sizeOfId, ref IntPtr store);
        [DllImport("cemapi.dll", EntryPoint = "#27", SetLastError = true)]
        static extern int MNextId(IntPtr table, ref IntPtr itemId, ref uint sizeOfId);
        [DllImport("cemapi.dll", EntryPoint = "#29", SetLastError = true)]
        static extern int MGetFolder(IntPtr store, uint folderTag, ref IntPtr folder);
        [DllImport("cemapi.dll", EntryPoint = "#43", SetLastError = true)]
        static extern int MGetContentsTable(IntPtr container, ref IntPtr table);
        [DllImport("cemapi.dll", EntryPoint = "#25", SetLastError = true)]
        static extern int MGetMessage(IntPtr session, IntPtr itemId, uint sizeOfId, ref uint objectType, ref IntPtr message);
        [DllImport("cemapi.dll", EntryPoint = "#38", SetLastError = true)]
        static extern int MGetStringProp(IntPtr owner, uint tag, ref IntPtr text, ref IntPtr toFree);
        [DllImport("cemapi.dll", EntryPoint = "#46", SetLastError = true)]
        static extern int MFreeBuffer(ref IntPtr p);
        [DllImport("cemapi.dll", EntryPoint = "#40", SetLastError = true)]
        static extern int MGetDateProp(IntPtr owner, uint tag, ref uint year, ref uint month, ref uint dayOfWeek,
                                       ref uint day, ref uint hour, ref uint minute, ref uint second, ref uint millisecond);
        [DllImport("cemapi.dll", EntryPoint = "#48", SetLastError = true)]
        static extern int SendSmsHelper(string recipient, string text, bool deliveryReport);
    }
}
