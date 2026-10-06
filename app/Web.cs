// Web.cs - web_fetch and web_search for the agent, over Https.cs (TLS 1.2 via
// Bouncy Castle with certificate checks, or plain HTTP). Pages are reduced to
// readable text so they fit the model's context and the phone's memory.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.Utilities.Zlib;

namespace WMAI
{
    public class WebTools
    {
        const int MaxBodyBytes = 512 * 1024;
        const string UserAgent = "Mozilla/5.0 (compatible; WMAI; Windows CE 5)";

        readonly string appDir;
        TrustStore trust;

        public WebTools(string appDir) { this.appDir = appDir; }

        TrustStore Trust
        {
            get
            {
                if (trust == null) trust = TrustStore.LoadDefault(appDir);
                return trust;
            }
        }

        static string Arg(Dictionary<string, string> a, string k)
        {
            string v;
            return a.TryGetValue(k, out v) ? v : null;
        }

        static int IntArg(Dictionary<string, string> a, string k, int def)
        {
            string v = Arg(a, k);
            if (v == null) return def;
            try { return int.Parse(v.Trim()); }
            catch (Exception) { return def; }
        }

        // ---- HTTP GET with redirects ----
        public class Page
        {
            public string Url, ContentType, Text;
            public int Status;
            public bool Truncated;
        }

        public Page Get(string url)
        {
            for (int hop = 0; hop < 6; hop++)
            {
                string scheme, host, path;
                int port;
                if (!SplitUrl(url, out scheme, out host, out port, out path)) throw new IOException("unsupported URL: " + url);
                HttpsConnection c = scheme == "https" ? new HttpsConnection(host, port, Trust) : HttpsConnection.Plain(host, port);
                try
                {
                    HttpResponse r = c.Send("GET", path, new string[] {
                        "User-Agent: " + UserAgent,
                        "Accept: text/html,application/xhtml+xml,text/plain,application/json;q=0.9,*/*;q=0.5",
                        "Accept-Language: en,bg;q=0.8" }, null);
                    if (r.Status >= 300 && r.Status < 400 && r.Header("location") != null)
                    {
                        Drain(r.Body);
                        url = Resolve(url, r.Header("location"));
                        continue;
                    }
                    Page p = new Page();
                    p.Url = url;
                    p.Status = r.Status;
                    p.ContentType = (r.Header("content-type") ?? "").ToLower();
                    byte[] body = ReadCapped(r.Body, MaxBodyBytes, out p.Truncated);
                    if ((r.Header("content-encoding") ?? "").ToLower().IndexOf("gzip") >= 0) body = Gunzip(body);
                    p.Text = Decode(body, p.ContentType);
                    return p;
                }
                finally
                {
                    c.Close();
                }
            }
            throw new IOException("too many redirects");
        }

        static void Drain(Stream s)
        {
            byte[] b = new byte[4096];
            while (s.Read(b, 0, b.Length) > 0) { }
        }

        static byte[] ReadCapped(Stream s, int max, out bool truncated)
        {
            MemoryStream ms = new MemoryStream();
            byte[] b = new byte[8192];
            int n;
            truncated = false;
            while ((n = s.Read(b, 0, b.Length)) > 0)
            {
                ms.Write(b, 0, n);
                if (ms.Length >= max) { truncated = true; break; }
            }
            return ms.ToArray();
        }

        // gzip = 10-byte header (+ optional fields) + raw deflate + trailer.
        static byte[] Gunzip(byte[] data)
        {
            if (data.Length < 18 || data[0] != 0x1f || data[1] != 0x8b) return data;
            int flags = data[3], p = 10;
            if ((flags & 4) != 0) p += 2 + data[p] + (data[p + 1] << 8);
            if ((flags & 8) != 0) { while (p < data.Length && data[p] != 0) p++; p++; }
            if ((flags & 16) != 0) { while (p < data.Length && data[p] != 0) p++; p++; }
            if ((flags & 2) != 0) p += 2;
            MemoryStream outp = new MemoryStream();
            ZInputStream z = new ZInputStream(new MemoryStream(data, p, data.Length - p), true);
            byte[] b = new byte[8192];
            int n;
            try { while ((n = z.Read(b, 0, b.Length)) > 0) outp.Write(b, 0, n); }
            catch (IOException) { } // trailer or truncated body
            return outp.ToArray();
        }

        static string Decode(byte[] body, string contentType)
        {
            string charset = null;
            int i = contentType.IndexOf("charset=");
            if (i >= 0) charset = contentType.Substring(i + 8).Trim(' ', '"', ';');
            if (charset == null)
            {
                // <meta charset="..."> or http-equiv content="...charset=..."
                string head = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 2048)).ToLower();
                int m = head.IndexOf("charset=");
                if (m >= 0)
                {
                    int e = m + 8;
                    while (e < head.Length && " \"'>;/".IndexOf(head[e]) < 0) e++;
                    charset = head.Substring(m + 8, e - m - 8).Trim('"', '\'');
                }
            }
            Encoding enc = Encoding.UTF8;
            if (charset != null && charset.Length > 0 && charset != "utf-8" && charset != "utf8")
            {
                try { enc = Encoding.GetEncoding(charset == "windows-1251" ? "windows-1251" : charset); }
                catch (Exception) { enc = Encoding.UTF8; }
            }
            return enc.GetString(body, 0, body.Length);
        }

        static bool SplitUrl(string url, out string scheme, out string host, out int port, out string path)
        {
            scheme = host = path = null;
            port = 0;
            int se = url.IndexOf("://");
            if (se <= 0) return false;
            scheme = url.Substring(0, se).ToLower();
            if (scheme != "http" && scheme != "https") return false;
            string rest = url.Substring(se + 3);
            int slash = rest.IndexOf('/');
            int q = rest.IndexOf('?');
            if (q >= 0 && (slash < 0 || q < slash)) slash = q;
            string hostPort = slash < 0 ? rest : rest.Substring(0, slash);
            path = slash < 0 ? "/" : rest.Substring(slash);
            if (path.StartsWith("?")) path = "/" + path;
            int hash = path.IndexOf('#');
            if (hash >= 0) path = path.Substring(0, hash);
            int colon = hostPort.LastIndexOf(':');
            port = scheme == "https" ? 443 : 80;
            if (colon > 0)
            {
                try { port = int.Parse(hostPort.Substring(colon + 1)); }
                catch (Exception) { return false; }
                hostPort = hostPort.Substring(0, colon);
            }
            int at = hostPort.LastIndexOf('@');
            host = (at >= 0 ? hostPort.Substring(at + 1) : hostPort).ToLower();
            path = path.Replace(" ", "%20");
            return host.Length > 0;
        }

        static string Resolve(string baseUrl, string href)
        {
            href = href.Trim();
            if (href.StartsWith("http://") || href.StartsWith("https://")) return href;
            string scheme, host, path;
            int port;
            if (!SplitUrl(baseUrl, out scheme, out host, out port, out path)) return href;
            string origin = scheme + "://" + host + ((scheme == "https" && port == 443) || (scheme == "http" && port == 80) ? "" : ":" + port);
            if (href.StartsWith("//")) return scheme + ":" + href;
            if (href.StartsWith("/")) return origin + href;
            if (href.StartsWith("?")) { int qi = path.IndexOf('?'); return origin + (qi >= 0 ? path.Substring(0, qi) : path) + href; }
            int q2 = path.IndexOf('?');
            string dir = q2 >= 0 ? path.Substring(0, q2) : path;
            dir = dir.Substring(0, dir.LastIndexOf('/') + 1);
            return origin + dir + href;
        }

        // ---- HTML -> text ----
        public static string HtmlToText(string html, string baseUrl, ArrayList links, out string title)
        {
            title = "";
            StringBuilder b = new StringBuilder();
            string lower = html.ToLower(); // once: pages can be 512 KB on a 200 MHz CPU
            int i = 0, n = html.Length;
            string pendingHref = null;
            StringBuilder linkText = null;
            while (i < n)
            {
                char ch = html[i];
                if (ch != '<')
                {
                    int lt = html.IndexOf('<', i);
                    if (lt < 0) lt = n;
                    string t = Entities(html.Substring(i, lt - i));
                    b.Append(t);
                    if (linkText != null) linkText.Append(t);
                    i = lt;
                    continue;
                }
                int gt = html.IndexOf('>', i);
                if (gt < 0) break;
                string tag = html.Substring(i + 1, gt - i - 1);
                string name = TagName(tag);
                i = gt + 1;
                if (name == "!--")
                {
                    int end = html.IndexOf("-->", i - 1);
                    i = end < 0 ? n : end + 3;
                    continue;
                }
                if (name == "script" || name == "style" || name == "noscript" || name == "svg")
                {
                    int end = lower.IndexOf("</" + name, i);
                    i = end < 0 ? n : end;
                    continue;
                }
                if (name == "title")
                {
                    int end = lower.IndexOf("</title", i);
                    if (end > i) title = Collapse(Entities(html.Substring(i, end - i))).Trim();
                    i = end < 0 ? n : end;
                    continue;
                }
                if (name == "a" && links != null)
                {
                    pendingHref = Attr(tag, "href");
                    linkText = new StringBuilder();
                }
                else if (name == "/a" && links != null && pendingHref != null)
                {
                    string text = Collapse(linkText.ToString()).Trim();
                    if (text.Length > 0 && !pendingHref.StartsWith("#") && !pendingHref.StartsWith("javascript:") && links.Count < 60)
                    {
                        links.Add(new string[] { text.Length > 60 ? text.Substring(0, 57) + "..." : text, Resolve(baseUrl, Entities(pendingHref)) });
                        b.Append(" [").Append(links.Count).Append(']');
                    }
                    pendingHref = null;
                    linkText = null;
                }
                if (name == "li") b.Append("\n- ");
                else if (name == "br" || name == "p" || name == "/p" || name == "div" || name == "/div" || name == "tr"
                    || name == "/tr" || name == "table" || name == "/table" || name == "/li" || name == "ul" || name == "/ul"
                    || name == "ol" || name == "/ol" || name == "section" || name == "article" || name == "header" || name == "footer"
                    || name == "pre" || name == "/pre" || name == "blockquote" || name == "/blockquote" || name == "hr"
                    || (name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]))
                    || (name.Length == 3 && name[0] == '/' && name[1] == 'h' && char.IsDigit(name[2])))
                    b.Append('\n');
                else if (name == "td" || name == "th") b.Append(" | ");
            }
            return CollapseLines(b.ToString());
        }

        static string TagName(string tag)
        {
            int e = 0;
            while (e < tag.Length && " \t\r\n/>".IndexOf(tag[e]) < 0 || (e == 0 && tag.Length > 0 && tag[0] == '/')) e++;
            string nm = tag.Substring(0, e).ToLower();
            if (nm.StartsWith("!--")) return "!--";
            return nm;
        }

        static string Attr(string tag, string attr)
        {
            string low = tag.ToLower();
            int i = low.IndexOf(attr + "=");
            if (i < 0) return null;
            i += attr.Length + 1;
            if (i >= tag.Length) return null;
            char q = tag[i];
            if (q == '"' || q == '\'')
            {
                int e = tag.IndexOf(q, i + 1);
                return e < 0 ? tag.Substring(i + 1) : tag.Substring(i + 1, e - i - 1);
            }
            int end = i;
            while (end < tag.Length && " \t>".IndexOf(tag[end]) < 0) end++;
            return tag.Substring(i, end - i);
        }

        public static string Entities(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '&') { b.Append(s[i]); continue; }
                int semi = s.IndexOf(';', i);
                if (semi < 0 || semi - i > 10) { b.Append('&'); continue; }
                string e = s.Substring(i + 1, semi - i - 1);
                string r = null;
                if (e == "amp") r = "&";
                else if (e == "lt") r = "<";
                else if (e == "gt") r = ">";
                else if (e == "quot") r = "\"";
                else if (e == "apos" || e == "#39") r = "'";
                else if (e == "nbsp") r = " ";
                else if (e == "mdash") r = "-";
                else if (e == "ndash") r = "-";
                else if (e == "hellip") r = "...";
                else if (e.StartsWith("#x") || e.StartsWith("#X"))
                {
                    try { r = ((char)Convert.ToInt32(e.Substring(2), 16)).ToString(); }
                    catch (Exception) { }
                }
                else if (e.StartsWith("#"))
                {
                    try { r = ((char)int.Parse(e.Substring(1))).ToString(); }
                    catch (Exception) { }
                }
                if (r == null) { b.Append('&'); continue; }
                b.Append(r);
                i = semi;
            }
            return b.ToString();
        }

        static string Collapse(string s)
        {
            StringBuilder b = new StringBuilder();
            bool space = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ' ')
                {
                    if (!space) b.Append(' ');
                    space = true;
                }
                else { b.Append(c); space = false; }
            }
            return b.ToString();
        }

        static string CollapseLines(string s)
        {
            string[] lines = s.Split('\n');
            StringBuilder b = new StringBuilder();
            bool blank = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = Collapse(lines[i]).Trim();
                if (l.Length == 0 || l == "|") { if (!blank && b.Length > 0) b.Append('\n'); blank = true; continue; }
                b.Append(l).Append('\n');
                blank = false;
            }
            return b.ToString().Trim();
        }

        // ---- tools ----
        public string Fetch(Dictionary<string, string> a)
        {
            string url = Arg(a, "url");
            if (url == null || url.Trim().Length == 0) return "error: 'url' required";
            url = url.Trim();
            if (url.IndexOf("://") < 0) url = "https://" + url;
            int offset = Math.Max(0, IntArg(a, "offset", 0));
            int max = Math.Min(20000, Math.Max(500, IntArg(a, "max_chars", 6000)));
            bool wantLinks = (Arg(a, "links") ?? "").ToLower() == "true";

            Page p;
            try { p = Get(url); }
            catch (Exception ex) { return "error: " + Tools.Describe(ex); }

            string title = "", text;
            ArrayList links = wantLinks ? new ArrayList() : null;
            bool html = p.ContentType.IndexOf("html") >= 0 || (p.ContentType.Length == 0 && p.Text.TrimStart().StartsWith("<"));
            if (html) text = HtmlToText(p.Text, p.Url, links, out title);
            else if (p.ContentType.StartsWith("text/") || p.ContentType.IndexOf("json") >= 0 || p.ContentType.IndexOf("xml") >= 0) text = p.Text;
            else return "fetched " + p.Url + " (HTTP " + p.Status + ", " + p.ContentType + "): not text, not shown";

            StringBuilder b = new StringBuilder();
            b.Append("URL: ").Append(p.Url).Append("  (HTTP ").Append(p.Status).Append(")\n");
            if (title.Length > 0) b.Append("Title: ").Append(title).Append('\n');
            if (offset >= text.Length) b.Append("(no text at offset ").Append(offset).Append("; ").Append(text.Length).Append(" chars total)\n");
            else
            {
                int len = Math.Min(max, text.Length - offset);
                b.Append('\n').Append(text.Substring(offset, len)).Append('\n');
                if (offset + len < text.Length)
                    b.Append("...(").Append(text.Length - offset - len).Append(" more chars; call again with offset=").Append(offset + len).Append(")\n");
            }
            if (p.Truncated) b.Append("(page larger than 512 KB; only the start was read)\n");
            if (links != null && links.Count > 0)
            {
                b.Append("\nLinks:\n");
                for (int i = 0; i < links.Count; i++)
                {
                    string[] l = (string[])links[i];
                    b.Append('[').Append(i + 1).Append("] ").Append(l[0]).Append(" -> ").Append(l[1]).Append('\n');
                }
            }
            return b.ToString();
        }

        public string Search(Dictionary<string, string> a)
        {
            string q = Arg(a, "query");
            if (q == null || q.Trim().Length == 0) return "error: 'query' required";
            int count = Math.Min(10, Math.Max(1, IntArg(a, "count", 6)));
            Page p;
            try { p = Get("https://lite.duckduckgo.com/lite/?q=" + UrlEncode(q.Trim())); }
            catch (Exception ex) { return "error: " + Tools.Describe(ex); }

            // DuckDuckGo lite: <a ... href="//duckduckgo.com/l/?uddg=URL&..." class='result-link'>TITLE</a>
            // followed by <td class='result-snippet'>SNIPPET</td>.
            StringBuilder b = new StringBuilder();
            string html = p.Text;
            int pos = 0, found = 0;
            while (found < count)
            {
                int cls = html.IndexOf("class='result-link'", pos);
                if (cls < 0) break;
                int aStart = html.LastIndexOf("<a ", cls);
                int textStart = html.IndexOf('>', cls) + 1;
                int textEnd = html.IndexOf("</a>", textStart);
                if (aStart < 0 || textStart <= 0 || textEnd < 0) break;
                string href = Attr(html.Substring(aStart, cls - aStart), "href") ?? "";
                string target = href;
                int u = href.IndexOf("uddg=");
                if (u >= 0)
                {
                    int amp = href.IndexOf('&', u);
                    target = UrlDecode(amp < 0 ? href.Substring(u + 5) : href.Substring(u + 5, amp - u - 5));
                }
                string title = Collapse(Entities(StripTags(html.Substring(textStart, textEnd - textStart)))).Trim();
                string snippet = "";
                int sn = html.IndexOf("class='result-snippet'", textEnd);
                int nextLink = html.IndexOf("class='result-link'", textEnd);
                if (sn >= 0 && (nextLink < 0 || sn < nextLink))
                {
                    int ss = html.IndexOf('>', sn) + 1, se = html.IndexOf("</td>", ss);
                    if (ss > 0 && se > ss) snippet = Collapse(Entities(StripTags(html.Substring(ss, se - ss)))).Trim();
                }
                found++;
                b.Append(found).Append(". ").Append(title).Append('\n').Append("   ").Append(target).Append('\n');
                if (snippet.Length > 0) b.Append("   ").Append(snippet.Length > 300 ? snippet.Substring(0, 297) + "..." : snippet).Append('\n');
                pos = textEnd;
            }
            if (found == 0)
                return p.Text.ToLower().IndexOf("anomaly") >= 0 ? "error: the search engine refused this request (bot check); try again later or web_fetch a site directly"
                                                               : "no results for: " + q;
            return b.ToString();
        }

        static string StripTags(string s)
        {
            StringBuilder b = new StringBuilder();
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') inTag = true;
                else if (s[i] == '>') inTag = false;
                else if (!inTag) b.Append(s[i]);
            }
            return b.ToString();
        }

        static string UrlEncode(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                byte c = bytes[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                    b.Append((char)c);
                else if (c == ' ') b.Append('+');
                else b.Append('%').Append(c.ToString("X2"));
            }
            return b.ToString();
        }

        static string UrlDecode(string s)
        {
            MemoryStream ms = new MemoryStream();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%' && i + 2 < s.Length)
                {
                    try { ms.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; continue; }
                    catch (Exception) { }
                }
                if (c == '+') ms.WriteByte((byte)' ');
                else
                {
                    byte[] cb = Encoding.UTF8.GetBytes(c.ToString());
                    ms.Write(cb, 0, cb.Length);
                }
            }
            byte[] all = ms.ToArray();
            return Encoding.UTF8.GetString(all, 0, all.Length);
        }
    }
}
