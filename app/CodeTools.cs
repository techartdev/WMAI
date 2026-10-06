// CodeTools.cs - a small coding harness on the phone: project files plus a
// build tool driving PocketGCC (GCC 3.2.2), so the agent can write, compile,
// read errors, fix and run native Windows Mobile apps.
//
// Projects live in \Storage Card\Projects\<name>. The compiler is the rebuilt
// PocketGCC in \Storage Card\pgcc\wmai, whose --stdout/--stderr options make
// its messages readable (see docs/DEVELOPMENT.md). The first change to a project in a
// WMAI session asks the user once; launching the result goes through
// run_program, which asks as well.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WMAI
{
    public class CodeTools
    {
        // Projects and the compiler live on the same card as WMAI (e.g. WMAI in
        // "\Storage Card\WMAI" -> "\Storage Card\Projects" and "\Storage Card\pgcc");
        // devices name their card differently ("SD Card", "Storage Card"...).
        static readonly string Card = DetectCard();
        static readonly string Root = Card + "\\Projects";
        static readonly string Pgcc = Card + "\\pgcc";
        static readonly string Bin = Pgcc + "\\wmai";

        static string DetectCard()
        {
            try
            {
                string p = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase;
                if (p.StartsWith("file:///")) p = p.Substring(8).Replace('/', '\\');
                int second = p.IndexOf('\\', 1);
                if (p.StartsWith("\\") && second > 1) return p.Substring(0, second);
            }
            catch (Exception) { }
            return "\\Storage Card";
        }
        const int StageTimeoutMs = 15 * 60 * 1000;
        const int MaxMessageChars = 3000;

        readonly ApproveHandler approve;
        readonly Hashtable approved = new Hashtable(); // project name -> true (this session)

        public CodeTools(ApproveHandler approve)
        {
            this.approve = approve;
        }

        // ---- helpers ----
        static string Arg(Dictionary<string, string> a, string k)
        {
            string v;
            return a.TryGetValue(k, out v) && v != null ? v : null;
        }

        static int IntArg(Dictionary<string, string> a, string k, int def)
        {
            string v = Arg(a, k);
            if (v == null) return def;
            try { return int.Parse(v.Trim()); }
            catch (Exception) { return def; }
        }

        // Project names: letters, digits, '_' and '-' (they become folder and exe names).
        static string CheckName(string name)
        {
            if (name == null || name.Length == 0 || name.Length > 32) return "project name must be 1-32 characters";
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) return "project name may only contain letters, digits, '_' and '-'";
            }
            return null;
        }

        static string ProjectDir(string name) { return Root + "\\" + name; }

        // A path inside the project; null if it would escape it.
        static string FileIn(string project, string rel)
        {
            if (rel == null) return null;
            rel = rel.Replace('/', '\\').Trim();
            while (rel.StartsWith("\\")) rel = rel.Substring(1);
            if (rel.Length == 0 || rel.IndexOf(':') >= 0) return null;
            string[] parts = rel.Split('\\');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i] == ".." || parts[i] == "." || parts[i].Length == 0) return null;
            return ProjectDir(project) + "\\" + rel;
        }

        bool Approve(string project)
        {
            if (approved.ContainsKey(project)) return true;
            if (approve == null || !approve("Allow the AI to create, edit and build files in project '" + project +
                                            "'?\n\n" + ProjectDir(project))) return false;
            approved[project] = true;
            return true;
        }

        static string Clip(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max) + "\n...(truncated)";
        }

        static string ReadAll(string path)
        {
            using (StreamReader r = new StreamReader(path, Encoding.UTF8)) return r.ReadToEnd();
        }

        static void WriteAll(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false))) // no BOM: GCC 3.2 rejects it
                w.Write(text);
        }

        // GCC 3.2 reads source as raw bytes; non-ASCII inside L"..." becomes garbage.
        static string AsciiWarning(string path, string text)
        {
            string ext = Path.GetExtension(path).ToLower();
            if (ext != ".cpp" && ext != ".c" && ext != ".h" && ext != ".rc") return "";
            int line = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') line++;
                else if (text[i] > 127)
                    return "\nwarning: non-ASCII character at line " + line +
                           "; use escapes like L\"\\x0417\" inside wide strings instead";
            }
            return "";
        }

        // ---- project_list ----
        public string List(Dictionary<string, string> a)
        {
            string project = Arg(a, "project");
            StringBuilder b = new StringBuilder();
            if (project == null || project.Length == 0)
            {
                if (!Directory.Exists(Root)) return "(no projects yet; create one with project_new)";
                string[] dirs = Directory.GetDirectories(Root);
                Array.Sort(dirs);
                for (int i = 0; i < dirs.Length; i++) b.Append(Path.GetFileName(dirs[i])).Append('\n');
                return b.Length == 0 ? "(no projects yet; create one with project_new)" : b.ToString();
            }
            string err = CheckName(project);
            if (err != null) return "error: " + err;
            if (!Directory.Exists(ProjectDir(project))) return "error: no project '" + project + "'";
            ListFiles(ProjectDir(project), "", b);
            return b.Length == 0 ? "(empty project)" : Clip(b.ToString(), 6000);
        }

        static void ListFiles(string dir, string prefix, StringBuilder b)
        {
            string[] files = Directory.GetFiles(dir);
            Array.Sort(files);
            for (int i = 0; i < files.Length; i++)
                b.Append(prefix).Append(Path.GetFileName(files[i])).Append("  ").Append(new FileInfo(files[i]).Length).Append('\n');
            string[] dirs = Directory.GetDirectories(dir);
            Array.Sort(dirs);
            for (int i = 0; i < dirs.Length; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                if (prefix.Length == 0 && name.ToLower() == "build") { b.Append("build\\  (compiler output)\n"); continue; }
                ListFiles(dirs[i], prefix + name + "\\", b);
            }
        }

        // ---- project_read ----
        public string Read(Dictionary<string, string> a)
        {
            string project = Arg(a, "project"), err = CheckName(project);
            if (err != null) return "error: " + err;
            string path = FileIn(project, Arg(a, "path"));
            if (path == null) return "error: 'path' must be a file inside the project";
            if (!File.Exists(path)) return "error: no file " + Arg(a, "path") + " in project '" + project + "'";
            string[] lines = ReadAll(path).Replace("\r\n", "\n").Split('\n');
            int start = Math.Max(1, IntArg(a, "start_line", 1));
            int count = Math.Min(400, Math.Max(1, IntArg(a, "max_lines", 200)));
            StringBuilder b = new StringBuilder();
            int end = Math.Min(lines.Length, start + count - 1);
            for (int i = start; i <= end; i++) b.Append(i.ToString().PadLeft(4)).Append("| ").Append(lines[i - 1]).Append('\n');
            if (end < lines.Length) b.Append("...(").Append(lines.Length - end).Append(" more lines; use start_line=").Append(end + 1).Append(")\n");
            return b.Length == 0 ? "(empty file)" : b.ToString();
        }

        // ---- project_write ----
        public string Write(Dictionary<string, string> a)
        {
            string project = Arg(a, "project"), err = CheckName(project);
            if (err != null) return "error: " + err;
            string path = FileIn(project, Arg(a, "path"));
            if (path == null) return "error: 'path' must be a relative file path inside the project";
            string content = Arg(a, "content");
            if (content == null) return "error: 'content' required";
            if (!Approve(project)) return "denied";
            WriteAll(path, content);
            return "wrote " + Arg(a, "path") + " (" + content.Length + " chars)" + AsciiWarning(path, content);
        }

        // ---- project_edit ----
        public string Edit(Dictionary<string, string> a)
        {
            string project = Arg(a, "project"), err = CheckName(project);
            if (err != null) return "error: " + err;
            string path = FileIn(project, Arg(a, "path"));
            if (path == null || !File.Exists(path)) return "error: no file " + Arg(a, "path") + " in project '" + project + "'";
            string oldText = Arg(a, "old_text"), newText = Arg(a, "new_text");
            if (oldText == null || oldText.Length == 0 || newText == null) return "error: 'old_text' and 'new_text' required";
            string text = ReadAll(path);
            // Match regardless of line-ending style.
            string norm = text.Replace("\r\n", "\n"), oldNorm = oldText.Replace("\r\n", "\n");
            int at = norm.IndexOf(oldNorm);
            if (at < 0) return "error: old_text not found in " + Arg(a, "path") + " (read the file again; it must match exactly)";
            if (norm.IndexOf(oldNorm, at + 1) >= 0) return "error: old_text occurs more than once; include more surrounding lines";
            if (!Approve(project)) return "denied";
            string result = norm.Substring(0, at) + newText.Replace("\r\n", "\n") + norm.Substring(at + oldNorm.Length);
            if (text.IndexOf("\r\n") >= 0) result = result.Replace("\n", "\r\n");
            WriteAll(path, result);
            int line = 1;
            for (int i = 0; i < at; i++) if (norm[i] == '\n') line++;
            return "edited " + Arg(a, "path") + " at line " + line + AsciiWarning(path, newText);
        }

        // ---- project_new ----
        public string New(Dictionary<string, string> a, string appDir)
        {
            string project = Arg(a, "project"), err = CheckName(project);
            if (err != null) return "error: " + err;
            string dir = ProjectDir(project);
            if (Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0)
                return "error: project '" + project + "' already exists (use project_list / project_read)";
            string title = Arg(a, "title");
            if (title == null || title.Trim().Length == 0) title = project;
            string tpl = Path.Combine(appDir, "templates\\app");
            if (!Directory.Exists(tpl)) return "error: template folder missing: " + tpl;
            if (!Approve(project)) return "denied";
            StringBuilder b = new StringBuilder("created project '" + project + "' in " + dir + ":\n");
            string[] files = Directory.GetFiles(tpl);
            for (int i = 0; i < files.Length; i++)
            {
                string text = ReadAll(files[i]).Replace("{{NAME}}", project).Replace("{{TITLE}}", title);
                WriteAll(dir + "\\" + Path.GetFileName(files[i]), text);
                b.Append("  ").Append(Path.GetFileName(files[i])).Append('\n');
            }
            b.Append("It builds as-is (a window, soft keys 'Clear' and 'Menu' with About/Exit, and a text box). Read the files, change them, then build.");
            return b.ToString();
        }

        // ---- build ----
        public string Build(Dictionary<string, string> a)
        {
            string project = Arg(a, "project"), err = CheckName(project);
            if (err != null) return "error: " + err;
            string dir = ProjectDir(project);
            if (!Directory.Exists(dir)) return "error: no project '" + project + "' (create it with project_new)";
            if (!File.Exists(Bin + "\\cc1plus.exe")) return "error: compiler not installed at " + Bin;
            if (!Approve(project)) return "denied";

            bool clean = (Arg(a, "clean") ?? "").ToLower() == "true";
            string build = dir + "\\build";
            if (!Directory.Exists(build)) Directory.CreateDirectory(build);

            ArrayList sources = new ArrayList();
            sources.AddRange(Directory.GetFiles(dir, "*.cpp"));
            sources.AddRange(Directory.GetFiles(dir, "*.c"));
            string[] rcs = Directory.GetFiles(dir, "*.rc");
            if (sources.Count == 0) return "error: no .cpp or .c files in the project";
            if (rcs.Length > 1) return "error: only one .rc file per project is supported";

            DateTime newestHeader = DateTime.MinValue;
            string[] headers = Directory.GetFiles(dir, "*.h");
            for (int i = 0; i < headers.Length; i++)
            {
                DateTime t = File.GetLastWriteTime(headers[i]);
                if (t > newestHeader) newestHeader = t;
            }

            string inc = "-I \"" + Pgcc + "\\include\" -I \"" + dir + "\" -include \"" + Pgcc + "\\fixincl.h\"";
            StringBuilder report = new StringBuilder();
            StringBuilder objs = new StringBuilder();
            int t0 = Environment.TickCount, compiled = 0;

            foreach (string src in sources)
            {
                string name = Path.GetFileNameWithoutExtension(src);
                string s = build + "\\" + name + ".s", o = build + "\\" + name + ".o";
                objs.Append(" \"").Append(o).Append('"');
                if (!clean && File.Exists(o))
                {
                    DateTime ot = File.GetLastWriteTime(o);
                    if (ot > File.GetLastWriteTime(src) && ot > newestHeader) continue; // up to date
                }
                string fail = Stage(build, "cc1plus", "\"" + src + "\" -o \"" + s + "\" " + inc + " -fms-extensions", s, report)
                           ?? Stage(build, "as", "\"" + s + "\" -o \"" + o + "\"", o, report);
                if (fail != null) return Result(false, project, fail, report, t0, compiled);
                compiled++;
            }

            if (rcs.Length == 1)
            {
                string p = build + "\\resources.rc.p", ro = build + "\\resources.rc.o";
                objs.Append(" \"").Append(ro).Append('"');
                bool fresh = !clean && File.Exists(ro) && File.GetLastWriteTime(ro) > File.GetLastWriteTime(rcs[0])
                             && File.GetLastWriteTime(ro) > newestHeader;
                if (!fresh)
                {
                    string fail = Stage(build, "cpp0", "\"" + rcs[0] + "\" -o \"" + p + "\" " + inc + " -DRC_INVOKED", p, report)
                               ?? Stage(build, "windres", "\"" + p + "\" -o \"" + ro + "\" --include-dir \"" + dir + "\"", ro, report);
                    if (fail != null) return Result(false, project, fail, report, t0, compiled);
                }
            }

            string exe = dir + "\\" + project + ".exe";
            string linkFail = Stage(build, "ld", objs.ToString().Trim() + " -o \"" + exe + "\" -L \"" + Pgcc + "\\lib\"" +
                                    " -l cpplib -l corelibc -l coredll -l aygshell -l runtime -l portlib", exe, report);
            return Result(linkFail == null, project, linkFail, report, t0, compiled);
        }

        static string Result(bool ok, string project, string fail, StringBuilder report, int t0, int compiled)
        {
            int secs = (Environment.TickCount - t0) / 1000;
            if (!ok) return "error: build failed at " + fail + "\n" + Clip(report.ToString(), MaxMessageChars);
            string exe = ProjectDir(project) + "\\" + project + ".exe";
            return "build OK in " + secs + " s (" + compiled + " source file(s) compiled): " + exe +
                   " (" + new FileInfo(exe).Length / 1024 + " KB). Start it with run_program." +
                   (report.Length > 0 ? "\nmessages:\n" + Clip(report.ToString(), MaxMessageChars) : "");
        }

        // Runs one compiler stage; returns null on success, else a short reason.
        // Messages (warnings or errors) are appended to report.
        static string Stage(string build, string tool, string args, string output, StringBuilder report)
        {
            // Message files in \Temp: the path inside "--stderr=..." must not contain
            // spaces (PocketGCC's own argument parser is untested with quotes there).
            string errFile = "\\Temp\\wmai_" + tool + ".err.txt", outFile = "\\Temp\\wmai_" + tool + ".out.txt";
            if (File.Exists(errFile)) File.Delete(errFile);
            if (File.Exists(outFile)) File.Delete(outFile);
            if (File.Exists(output)) File.Delete(output);

            Process p = Process.Start(Bin + "\\" + tool + ".exe", args + " --stderr=" + errFile + " --stdout=" + outFile);
            if (!p.WaitForExit(StageTimeoutMs))
            {
                try { p.Kill(); } catch (Exception) { }
                return tool + " (timed out after " + StageTimeoutMs / 60000 + " min)";
            }
            int code = p.ExitCode;
            string msg = "";
            if (File.Exists(outFile)) msg += ReadAll(outFile);
            if (File.Exists(errFile)) msg += ReadAll(errFile);
            msg = msg.Trim();
            if (msg.Length > 0) report.Append(msg).Append('\n');
            if (code == 0 && File.Exists(output)) return null;
            if (msg.Length == 0)
                return tool + " (exit code " + code + ", no messages - possibly out of memory; close other apps and retry)";
            return tool + " (exit code " + code + ")";
        }
    }
}
