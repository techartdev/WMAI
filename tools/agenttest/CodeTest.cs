// CodeTest - exercises app/CodeTools.cs on the phone without UI or AI:
// new project -> build -> break it -> build (expect a compiler error) -> fix ->
// build. Approvals are answered "yes" by this test. Writes codetest.log.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WMAI;

class CodeTest
{
    static StreamWriter log;
    static int t0, failures;

    static void Log(string s)
    {
        log.WriteLine(string.Format("{0,7} ms  {1}", Environment.TickCount - t0, s));
        log.Flush();
    }

    static void Check(string name, bool ok, string result)
    {
        if (!ok) failures++;
        Log((ok ? "PASS " : "FAIL ") + name);
        Log("     " + result.Replace("\n", "\n     "));
    }

    static Dictionary<string, string> Args(params string[] kv)
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    static bool Yes(string message) { return true; }

    [MTAThread]
    static void Main()
    {
        string here = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase);
        log = new StreamWriter(Path.Combine(here, "codetest.log"), false, Encoding.UTF8);
        t0 = Environment.TickCount;
        try
        {
            Tools tools = new Tools(new ApproveHandler(Yes));
            string dir = "\\Storage Card\\Projects\\codetest";
            if (Directory.Exists(dir)) Directory.Delete(dir, true);

            string r = tools.Execute("project_new", Args("project", "codetest", "title", "Code test"));
            Check("project_new", r.StartsWith("created"), r);

            r = tools.Execute("build", Args("project", "codetest"));
            Check("first build succeeds", r.StartsWith("build OK"), r);

            r = tools.Execute("project_edit", Args("project", "codetest", "path", "main.cpp",
                "old_text", "SetWindowTextW(g_edit, L\"\");", "new_text", "SetWindowTextW(g_edit, L\"\") oops;"));
            Check("project_edit introduces an error", r.StartsWith("edited"), r);

            r = tools.Execute("build", Args("project", "codetest"));
            Check("broken build reports file:line", r.StartsWith("error: build failed at cc1plus") && r.IndexOf("main.cpp:") >= 0, r);

            r = tools.Execute("project_edit", Args("project", "codetest", "path", "main.cpp",
                "old_text", "SetWindowTextW(g_edit, L\"\") oops;", "new_text", "SetWindowTextW(g_edit, L\"\");"));
            Check("project_edit fixes it", r.StartsWith("edited"), r);

            r = tools.Execute("build", Args("project", "codetest"));
            Check("rebuild succeeds", r.StartsWith("build OK") && r.IndexOf("(1 source file(s) compiled)") >= 0, r);

            r = tools.Execute("build", Args("project", "codetest"));
            Check("unchanged project only relinks", r.StartsWith("build OK") && r.IndexOf("(0 source file(s) compiled)") >= 0, r);

            r = tools.Execute("project_read", Args("project", "codetest", "path", "main.cpp", "start_line", "1", "max_lines", "3"));
            Check("project_read numbers lines", r.StartsWith("   1| "), r);

            r = tools.Execute("project_write", Args("project", "codetest", "path", "..\\escape.txt", "content", "x"));
            Check("paths cannot leave the project", r.StartsWith("error:"), r);
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
