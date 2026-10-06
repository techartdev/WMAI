// Run - starts a console program on the phone with stdout/stderr sent to files
// and waits for it, without CMD. Windows CE implements redirection with
// SetStdioPathW: a child process inherits the parent's stdio paths.
//   Run.exe <stdout file> <stderr file> <exe> [args...]
// Writes run.log next to Run.exe: command, exit code, elapsed ms.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class Run
{
    [DllImport("coredll.dll")]
    static extern bool SetStdioPathW(int id, string path); // 0 stdin, 1 stdout, 2 stderr
    [DllImport("coredll.dll")]
    static extern bool GetStdioPathW(int id, StringBuilder path, ref int len);

    [MTAThread]
    static void Main(string[] args)
    {
        string exe = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase;
        using (StreamWriter log = new StreamWriter(Path.Combine(Path.GetDirectoryName(exe), "run.log"), false, Encoding.UTF8))
        {
            try
            {
                StringBuilder rest = new StringBuilder();
                for (int i = 3; i < args.Length; i++)
                {
                    if (rest.Length > 0) rest.Append(' ');
                    rest.Append(args[i].IndexOf(' ') >= 0 ? "\"" + args[i] + "\"" : args[i]);
                }
                log.WriteLine("exe:  " + args[2]);
                log.WriteLine("args: " + rest);
                log.Flush();

                bool o = SetStdioPathW(1, args[0]);
                bool e = SetStdioPathW(2, args[1]);
                log.WriteLine("SetStdioPathW stdout=" + o + " stderr=" + e);
                log.Flush();

                int t = Environment.TickCount;
                Process p = Process.Start(args[2], rest.ToString());
                p.WaitForExit();
                log.WriteLine("exit code " + p.ExitCode + " after " + (Environment.TickCount - t) + " ms");
                SetStdioPathW(1, "");
                SetStdioPathW(2, "");
            }
            catch (Exception ex)
            {
                log.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
