// Https.cs - HTTPS for the phone: TLS 1.2 (Bouncy Castle) + a small keep-alive
// HTTP/1.1 client. WM5's own TLS stops at 1.0 and the LLM APIs require ECDHE
// over TLS 1.2, so the app brings its own.
//
// Certificates are validated: hostname (subjectAltName), validity dates, and
// the signature chain up to a root in WMAI.roots (PEM, next to the exe).
// C# 2.0 style, NETCF 3.5 APIs only (checked by tools/cfcheck.ps1).
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;
using X509CertificateParser = Org.BouncyCastle.X509.X509CertificateParser;

namespace WMAI
{
    // Trusted roots and the clock used for validity checks.
    public class TrustStore
    {
        // The phone's clock has reset to 2000 before (flat battery). Certificates
        // are never checked against a time earlier than this build-era floor.
        static readonly DateTime ClockFloor = new DateTime(2026, 10, 1, 0, 0, 0);

        readonly X509Certificate[] roots;
        // Optional big bundle (WMAI.roots.idx, built by tools/rootindex): subject
        // DN string -> ArrayList of base64 DER. Parsing ~140 roots up front would
        // cost seconds on the phone, so only roots a chain names are parsed.
        readonly Hashtable index = new Hashtable();
        readonly Hashtable parsed = new Hashtable(); // base64 -> X509Certificate
        int indexed;

        public TrustStore(X509Certificate[] roots) { this.roots = roots; }

        public static TrustStore Load(string pemPath)
        {
            ArrayList list = new ArrayList();
            using (FileStream fs = new FileStream(pemPath, FileMode.Open, FileAccess.Read))
                foreach (X509Certificate c in new X509CertificateParser().ReadCertificates(fs))
                    list.Add(c);
            if (list.Count == 0) throw new IOException("no certificates in " + pemPath);
            return new TrustStore((X509Certificate[])list.ToArray(typeof(X509Certificate)));
        }

        // WMAI.roots (PEM, always) plus WMAI.roots.idx (big bundle) when present.
        public static TrustStore LoadDefault(string dir)
        {
            TrustStore t = Load(Path.Combine(dir, "WMAI.roots"));
            string idx = Path.Combine(dir, "WMAI.roots.idx");
            if (File.Exists(idx))
            {
                using (StreamReader r = new StreamReader(idx, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string dn = line.Substring(0, tab);
                        ArrayList l = (ArrayList)t.index[dn];
                        if (l == null) t.index[dn] = l = new ArrayList();
                        l.Add(line.Substring(tab + 1));
                        t.indexed++;
                    }
                }
            }
            return t;
        }

        public int Count { get { return roots.Length + indexed; } }

        public static DateTime NowUtc()
        {
            DateTime now = DateTime.UtcNow;
            return now < ClockFloor ? ClockFloor : now;
        }

        // A root whose subject is c's issuer and whose key verifies c, or null.
        public X509Certificate FindIssuerOf(X509Certificate c)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                if (!roots[i].SubjectDN.Equivalent(c.IssuerDN)) continue;
                try
                {
                    c.Verify(roots[i].GetPublicKey());
                    return roots[i];
                }
                catch (Exception) { } // same name, different key: keep looking
            }
            ArrayList candidates = (ArrayList)index[c.IssuerDN.ToString()];
            if (candidates == null) return null;
            foreach (string b64 in candidates)
            {
                X509Certificate root = (X509Certificate)parsed[b64];
                if (root == null)
                {
                    try { root = new X509CertificateParser().ReadCertificate(Convert.FromBase64String(b64)); }
                    catch (Exception) { continue; }
                    parsed[b64] = root;
                }
                try
                {
                    c.Verify(root.GetPublicKey());
                    return root;
                }
                catch (Exception) { }
            }
            return null;
        }
    }

    // Validates the server chain; on failure keeps the reason in Error.
    class ChainValidator : TlsAuthentication
    {
        readonly string host;
        readonly TrustStore trust;
        public string Error;

        public ChainValidator(string host, TrustStore trust)
        {
            this.host = host;
            this.trust = trust;
        }

        public void NotifyServerCertificate(Certificate serverCertificate)
        {
            Error = Validate(serverCertificate.GetCertificateList());
            if (Error != null) throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            return null;
        }

        string Validate(X509CertificateStructure[] list)
        {
            if (list == null || list.Length == 0) return "server sent no certificate";
            X509Certificate[] chain = new X509Certificate[list.Length];
            for (int i = 0; i < list.Length; i++) chain[i] = new X509Certificate(list[i]);

            if (!HostMatches(chain[0], host)) return "certificate is not for " + host;

            DateTime now = TrustStore.NowUtc();
            for (int i = 0; i < chain.Length; i++)
            {
                X509Certificate c = chain[i];
                // Bouncy Castle returns these as UTC values (Kind unspecified): compare as-is.
                if (now < c.NotBefore || now > c.NotAfter)
                    return "certificate outside its validity period: " + c.SubjectDN;

                if (trust.FindIssuerOf(c) != null) return null; // anchored in a trusted root

                if (i + 1 >= chain.Length) return "no trusted root for " + c.IssuerDN;
                X509Certificate issuer = chain[i + 1];
                if (!issuer.SubjectDN.Equivalent(c.IssuerDN)) return "broken chain at " + c.SubjectDN;
                if (issuer.GetBasicConstraints() < 0) return "issuer is not a CA: " + issuer.SubjectDN;
                try { c.Verify(issuer.GetPublicKey()); }
                catch (Exception) { return "bad signature on " + c.SubjectDN; }
            }
            return "no trusted root";
        }

        static bool HostMatches(X509Certificate leaf, string host)
        {
            ICollection names = leaf.GetSubjectAlternativeNames();
            if (names == null) return false;
            host = host.ToLower();
            foreach (IList entry in names)
            {
                if ((int)entry[0] != GeneralName.DnsName) continue;
                string name = ((string)entry[1]).ToLower();
                if (name == host) return true;
                // "*.example.com" matches exactly one extra label.
                if (name.StartsWith("*.") && host.EndsWith(name.Substring(1)))
                {
                    string label = host.Substring(0, host.Length - name.Length + 1);
                    if (label.Length > 0 && label.IndexOf('.') < 0) return true;
                }
            }
            return false;
        }
    }

    class WmaiTlsClient : DefaultTlsClient
    {
        readonly string host;
        readonly ChainValidator validator;

        public WmaiTlsClient(string host, ChainValidator validator)
        {
            this.host = host;
            this.validator = validator;
        }

        // Forward-secret suites only; the APIs reject plain RSA key exchange anyway.
        public override int[] GetCipherSuites()
        {
            return new int[]
            {
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA,
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
            };
        }

        public override IDictionary GetClientExtensions()
        {
            IDictionary ext = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetClientExtensions());
            ArrayList names = new ArrayList();
            names.Add(new ServerName(NameType.host_name, host));
            TlsExtensionsUtilities.AddServerNameExtension(ext, new ServerNameList(names));
            return ext;
        }

        public override TlsAuthentication GetAuthentication()
        {
            return validator;
        }
    }

    // Buffered reads over the TLS stream; lines are ASCII (HTTP headers).
    class BufferedIn
    {
        readonly Stream s;
        readonly byte[] buf = new byte[8192];
        int pos, len;

        public BufferedIn(Stream s) { this.s = s; }

        bool Fill()
        {
            pos = 0;
            try { len = s.Read(buf, 0, buf.Length); }
            catch (TlsNoCloseNotifyException) { len = 0; } // peer closed without close_notify
            if (len < 0) len = 0;
            return len > 0;
        }

        public int Read(byte[] dst, int off, int count)
        {
            if (pos >= len && !Fill()) return 0;
            int n = Math.Min(count, len - pos);
            Array.Copy(buf, pos, dst, off, n);
            pos += n;
            return n;
        }

        // Line without CR/LF, or null at end of stream.
        public string ReadLine()
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (pos >= len && !Fill()) return sb.Length > 0 ? sb.ToString() : null;
                byte b = buf[pos++];
                if (b == (byte)'\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)b);
            }
        }
    }

    public class HttpResponse
    {
        public int Status;
        public string StatusLine;
        public Hashtable Headers = new Hashtable(); // lower-case names
        public Stream Body;

        public string Header(string name)
        {
            return (string)Headers[name.ToLower()];
        }

        public string ReadBodyText()
        {
            MemoryStream ms = new MemoryStream();
            byte[] b = new byte[4096];
            int n;
            while ((n = Body.Read(b, 0, b.Length)) > 0) ms.Write(b, 0, n);
            byte[] all = ms.ToArray();
            return Encoding.UTF8.GetString(all, 0, all.Length);
        }
    }

    // Decodes the response body (Content-Length, chunked, or until close) and
    // tells the connection when it has been read completely.
    class BodyStream : Stream
    {
        readonly BufferedIn input;
        readonly HttpsConnection owner;
        readonly bool chunked, untilClose;
        long remaining; // bytes left in the body (length mode) or current chunk
        bool done;

        public BodyStream(BufferedIn input, HttpsConnection owner, bool chunked, long length)
        {
            this.input = input;
            this.owner = owner;
            this.chunked = chunked;
            untilClose = !chunked && length < 0;
            remaining = chunked ? 0 : length;
            if (!chunked && length == 0) Finish();
        }

        void Finish()
        {
            if (done) return;
            done = true;
            owner.BodyFinished(!untilClose);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (done || count == 0) return 0;
            if (chunked && remaining == 0)
            {
                string line = input.ReadLine();
                if (line == null) throw new IOException("connection closed inside chunked body");
                int semi = line.IndexOf(';');
                if (semi >= 0) line = line.Substring(0, semi);
                remaining = Convert.ToInt64(line.Trim(), 16);
                if (remaining == 0)
                {
                    string trailer;
                    while ((trailer = input.ReadLine()) != null && trailer.Length > 0) { }
                    Finish();
                    return 0;
                }
            }
            int want = untilClose ? count : (int)Math.Min(count, remaining);
            int n = input.Read(buffer, offset, want);
            if (n == 0)
            {
                if (untilClose) { Finish(); return 0; }
                throw new IOException("connection closed inside body");
            }
            if (!untilClose)
            {
                remaining -= n;
                if (remaining == 0)
                {
                    if (chunked) input.ReadLine(); // CRLF after the chunk data
                    else Finish();
                }
            }
            return n;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    // One keep-alive HTTPS connection to one host. Not thread-safe; read each
    // response body to the end before sending the next request.
    public class HttpsConnection : IDisposable
    {
        readonly string host, verifyName;
        readonly int port;
        readonly TrustStore trust;
        static SecureRandom random; // seeding costs ~0.8 s on the phone: do it once

        TcpClient tcp;
        TlsClientProtocol tls;
        Stream io;          // TLS stream, or the raw socket stream for plain HTTP
        BufferedIn input;
        bool reusable;
        bool useTls = true; // false: plain HTTP (web_fetch of http:// URLs)
        public int ReceiveTimeoutMs = 60000;

        public int HandshakeMs;     // duration of the last handshake
        public int Handshakes;      // number of handshakes on this object
        public DateTime ServerDate; // Date header of the last response (UTC), or MinValue

        public HttpsConnection(string host, int port, TrustStore trust) : this(host, port, trust, host) { }

        // verifyName differs from host only in tests (hostname mismatch check).
        public HttpsConnection(string host, int port, TrustStore trust, string verifyName)
        {
            this.host = host;
            this.port = port;
            this.trust = trust;
            this.verifyName = verifyName;
        }

        // Plain HTTP connection (no TLS, no certificates).
        public static HttpsConnection Plain(string host, int port)
        {
            HttpsConnection c = new HttpsConnection(host, port, null, host);
            c.useTls = false;
            return c;
        }

        void Connect()
        {
            Close();
            IPAddress ip = null;
            IPAddress[] addrs = Dns.GetHostEntry(host).AddressList;
            for (int i = 0; i < addrs.Length && ip == null; i++)
                if (addrs[i].AddressFamily == AddressFamily.InterNetwork) ip = addrs[i];
            if (ip == null) throw new IOException("no IPv4 address for " + host);

            tcp = new TcpClient();
            tcp.Connect(new IPEndPoint(ip, port));
            // A dead network must not hang a tool forever - where supported: on
            // the phone (NETCF 3.5 / WM5) this option throws, so it is best effort.
            try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, ReceiveTimeoutMs); }
            catch (Exception) { }
            if (!useTls)
            {
                io = tcp.GetStream();
                input = new BufferedIn(io);
                reusable = false;
                return;
            }
            if (random == null) random = new SecureRandom();
            ChainValidator validator = new ChainValidator(verifyName, trust);
            tls = new TlsClientProtocol(tcp.GetStream(), random);
            int t = Environment.TickCount;
            try
            {
                tls.Connect(new WmaiTlsClient(host, validator)); // SNI = host; validator checks verifyName
            }
            catch (Exception ex)
            {
                Close();
                if (validator.Error != null) throw new IOException("certificate rejected: " + validator.Error);
                throw new IOException("TLS handshake failed: " + ex.Message);
            }
            HandshakeMs = Environment.TickCount - t;
            Handshakes++;
            io = tls.Stream;
            input = new BufferedIn(io);
            reusable = false;
        }

        // Sends a request and returns once the status line and headers are read.
        // headers: "Name: value" lines without CRLF (may be null).
        public HttpResponse Send(string method, string path, string[] headers, byte[] body)
        {
            bool reused = io != null && reusable;
            if (!reused) Connect();
            try
            {
                return SendOnce(method, path, headers, body);
            }
            catch (IOException)
            {
                if (!reused) throw;
                // The server may have closed the idle connection: retry once, fresh.
                Connect();
                return SendOnce(method, path, headers, body);
            }
        }

        HttpResponse SendOnce(string method, string path, string[] headers, byte[] body)
        {
            reusable = false;
            StringBuilder sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append("\r\n");
            sb.Append("User-Agent: WMAI\r\nAccept-Encoding: identity\r\nConnection: keep-alive\r\n");
            if (headers != null)
                for (int i = 0; i < headers.Length; i++) sb.Append(headers[i]).Append("\r\n");
            if (body != null || method == "POST" || method == "PUT")
                sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
            sb.Append("\r\n");

            // One write per request keeps it to a single TLS record where possible.
            byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] all = new byte[head.Length + (body == null ? 0 : body.Length)];
            Array.Copy(head, all, head.Length);
            if (body != null) Array.Copy(body, 0, all, head.Length, body.Length);
            Stream s;
            try
            {
                s = io;
                s.Write(all, 0, all.Length);
                s.Flush();
            }
            catch (Exception ex) { throw new IOException("send failed: " + ex.Message); }

            HttpResponse r = new HttpResponse();
            r.StatusLine = input.ReadLine();
            if (r.StatusLine == null) throw new IOException("connection closed before response");
            string[] parts = r.StatusLine.Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/")) throw new IOException("bad status line: " + r.StatusLine);
            r.Status = int.Parse(parts[1]);
            string line;
            while ((line = input.ReadLine()) != null && line.Length > 0)
            {
                int c = line.IndexOf(':');
                if (c > 0) r.Headers[line.Substring(0, c).Trim().ToLower()] = line.Substring(c + 1).Trim();
            }
            ServerDate = ParseHttpDate(r.Header("date"));

            bool closeAfter = (r.Header("connection") ?? "").ToLower() == "close";
            bool chunked = (r.Header("transfer-encoding") ?? "").ToLower().IndexOf("chunked") >= 0;
            long length = -1;
            if (method == "HEAD" || r.Status == 204 || r.Status == 304 || (r.Status >= 100 && r.Status < 200)) length = 0;
            else if (!chunked && r.Header("content-length") != null) length = long.Parse(r.Header("content-length"));
            keepAliveAllowed = !closeAfter;
            r.Body = new BodyStream(input, this, chunked && length != 0, length);
            return r;
        }

        bool keepAliveAllowed;

        internal void BodyFinished(bool cleanEnd)
        {
            reusable = cleanEnd && keepAliveAllowed;
            if (!reusable) Close();
        }

        // RFC 1123 date ("Tue, 06 Oct 2026 12:34:56 GMT") -> UTC, MinValue if absent/bad.
        static DateTime ParseHttpDate(string s)
        {
            if (s == null) return DateTime.MinValue;
            string[] p = s.Split(' ');
            if (p.Length < 5) return DateTime.MinValue;
            string[] months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
            int month = 0; // NETCF lacks Array.IndexOf(Array, object) (cfcheck)
            for (int i = 0; i < months.Length && month == 0; i++)
                if (months[i] == p[2].ToLower()) month = i + 1;
            string[] hms = p[4].Split(':');
            if (month == 0 || hms.Length != 3) return DateTime.MinValue;

            try
            {
                return new DateTime(int.Parse(p[3]), month, int.Parse(p[1]),
                                    int.Parse(hms[0]), int.Parse(hms[1]), int.Parse(hms[2]));
            }
            catch (Exception) { return DateTime.MinValue; }
        }

        public void Close()
        {
            reusable = false;
            if (tls != null) { try { tls.Close(); } catch (Exception) { } tls = null; }
            if (tcp != null) { try { tcp.Close(); } catch (Exception) { } tcp = null; }
            input = null;
            io = null;
        }

        public void Dispose() { Close(); }
    }
}
