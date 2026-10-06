// NetTest - exercises app/Https.cs: certificate validation (positive and
// negative), keep-alive reuse, Content-Length and chunked bodies, server Date.
// Runs on the PC (desktop .NET 2.0+) and on the phone (NETCF 3.5). Writes
// nettest.log next to the exe; each case ends in PASS or FAIL.
using System;
using System.IO;
using System.Text;
using WMAI;

namespace NetTest
{
    class Program
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
            if (exe.StartsWith("file:///")) exe = exe.Substring(8).Replace('/', '\\');
            string dir = Path.GetDirectoryName(exe);
            log = new StreamWriter(Path.Combine(dir, "nettest.log"), false, Encoding.UTF8);
            t0 = Environment.TickCount;
            try
            {
                Run(dir);
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

        static void Run(string dir)
        {
            Log("start, " + Environment.OSVersion + ", CLR " + Environment.Version +
                ", clock " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            TrustStore trust = TrustStore.Load(Path.Combine(dir, "WMAI.roots"));
            Log("loaded " + trust.Count + " trusted roots");

            // 1. DeepSeek: Content-Length body, then reuse of the same connection.
            HttpsConnection ds = new HttpsConnection("api.deepseek.com", 443, trust);
            int t = Environment.TickCount;
            HttpResponse r = ds.Send("GET", "/models", null, null);
            string body = r.ReadBodyText();
            Check("deepseek verified + Content-Length body", r.Status == 401 && body.Length > 0,
                  "status " + r.Status + ", " + body.Length + " chars, handshake " + ds.HandshakeMs + " ms, total " + (Environment.TickCount - t) + " ms");
            if (ds.ServerDate != DateTime.MinValue)
                Log("     server date " + ds.ServerDate.ToString("yyyy-MM-dd HH:mm:ss") + " UTC, phone clock off by " +
                    (int)(DateTime.UtcNow - ds.ServerDate).TotalSeconds + " s");

            t = Environment.TickCount;
            r = ds.Send("GET", "/models", null, null);
            r.ReadBodyText();
            Check("deepseek keep-alive reuse", r.Status == 401 && ds.Handshakes == 1,
                  "handshakes " + ds.Handshakes + ", request " + (Environment.TickCount - t) + " ms");
            ds.Close();

            // 2. OpenRouter: ECDSA chain (GTS Root R4), chunked bodies small and large.
            HttpsConnection or = new HttpsConnection("openrouter.ai", 443, trust);
            t = Environment.TickCount;
            r = or.Send("GET", "/api/v1/auth/key", null, null);
            body = r.ReadBodyText();
            Check("openrouter verified (ECDSA chain) + small chunked body", r.Status == 401 && body.IndexOf('{') >= 0,
                  "status " + r.Status + ", " + body.Length + " chars, handshake " + or.HandshakeMs + " ms");

            t = Environment.TickCount;
            r = or.Send("GET", "/api/v1/providers", null, null);
            body = r.ReadBodyText();
            int ms = Environment.TickCount - t;
            Check("openrouter large chunked body on reused connection",
                  r.Status == 200 && body.TrimEnd().EndsWith("}") && or.Handshakes == 1,
                  body.Length + " chars in " + ms + " ms (" + (ms > 0 ? body.Length * 1000L / ms : 0) + " chars/s), handshakes " + or.Handshakes);
            or.Close();

            // 3. Negative: right server, wrong expected name -> must be rejected.
            Expect("rejects hostname mismatch",
                   new HttpsConnection("api.deepseek.com", 443, trust, "wrong.example.com"), "not for");

            // 4. Negative: trust store without Amazon's root -> must be rejected.
            Org.BouncyCastle.X509.X509Certificate[] none = new Org.BouncyCastle.X509.X509Certificate[0];
            Expect("rejects untrusted root",
                   new HttpsConnection("api.deepseek.com", 443, new TrustStore(none)), "no trusted root");
        }

        static void Expect(string name, HttpsConnection c, string reasonPart)
        {
            try
            {
                HttpResponse r = c.Send("GET", "/models", null, null);
                Check(name, false, "connection was accepted (status " + r.Status + ")");
            }
            catch (IOException ex)
            {
                Check(name, ex.Message.IndexOf(reasonPart) >= 0, ex.Message);
            }
            finally
            {
                c.Close();
            }
        }
    }
}
