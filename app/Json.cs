// Json.cs - minimal JSON for NETCF (which has none).
// Parse: objects -> Hashtable, arrays -> ArrayList, strings, double, bool, null.
// Write: the same types plus int/long. Numbers are culture-independent (the
// phone's locale may use a decimal comma).
using System;
using System.Collections;
using System.Text;

namespace WMAI
{
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("JSON: trailing data at " + i);
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            return Num(s, ref i);
        }

        static bool Lit(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static Hashtable Obj(string s, ref int i)
        {
            Hashtable h = new Hashtable();
            i++; // {
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return h; }
            while (true)
            {
                Ws(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("JSON: expected key at " + i);
                string key = Str(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: expected ':' at " + i);
                i++;
                h[key] = Value(s, ref i);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return h; }
                throw new FormatException("JSON: expected ',' or '}' at " + i);
            }
        }

        static ArrayList Arr(string s, ref int i)
        {
            ArrayList a = new ArrayList();
            i++; // [
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value(s, ref i));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw new FormatException("JSON: expected ',' or ']' at " + i);
            }
        }

        static string Str(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder b = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON: bad \\u escape");
                        b.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); // surrogate halves pass through
                        i += 4;
                        break;
                    default: throw new FormatException("JSON: bad escape \\" + e);
                }
            }
            throw new FormatException("JSON: unterminated string");
        }

        static double Num(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && "0123456789.eE+-".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("JSON: unexpected '" + s[i] + "' at " + i);
            return ParseDouble(s.Substring(start, i - start));
        }

        // Culture-free: digits, optional fraction, optional exponent.
        static double ParseDouble(string t)
        {
            int e = t.IndexOfAny(new char[] { 'e', 'E' });
            string mant = e >= 0 ? t.Substring(0, e) : t;
            int exp = e >= 0 ? int.Parse(t.Substring(e + 1).Replace("+", "")) : 0;
            bool neg = mant.StartsWith("-");
            if (neg || mant.StartsWith("+")) mant = mant.Substring(1);
            int dot = mant.IndexOf('.');
            string digits = dot >= 0 ? mant.Substring(0, dot) + mant.Substring(dot + 1) : mant;
            if (dot >= 0) exp -= mant.Length - dot - 1;
            double v = 0;
            for (int k = 0; k < digits.Length; k++)
            {
                int d = digits[k] - '0';
                if (d < 0 || d > 9) throw new FormatException("JSON: bad number " + t);
                v = v * 10 + d;
            }
            if (exp != 0) v *= Math.Pow(10, exp);
            return neg ? -v : v;
        }

        // ---- writer ----
        public static string Write(object v)
        {
            StringBuilder b = new StringBuilder();
            Write(b, v);
            return b.ToString();
        }

        public static void Write(StringBuilder b, object v)
        {
            if (v == null) { b.Append("null"); return; }
            string s = v as string;
            if (s != null) { Quote(b, s); return; }
            if (v is bool) { b.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long) { b.Append(v.ToString()); return; }
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) b.Append(((long)d).ToString());
                else b.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            Hashtable h = v as Hashtable;
            if (h != null)
            {
                b.Append('{');
                bool first = true;
                foreach (DictionaryEntry de in h)
                {
                    if (!first) b.Append(',');
                    first = false;
                    Quote(b, (string)de.Key);
                    b.Append(':');
                    Write(b, de.Value);
                }
                b.Append('}');
                return;
            }
            IList list = v as IList;
            if (list != null)
            {
                b.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) b.Append(',');
                    Write(b, list[i]);
                }
                b.Append(']');
                return;
            }
            throw new ArgumentException("JSON: cannot write " + v.GetType().Name);
        }

        static void Quote(StringBuilder b, string s)
        {
            b.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c); // non-ASCII stays raw; the body is UTF-8
                        break;
                }
            }
            b.Append('"');
        }

        // ---- helpers for reading parsed trees ----
        public static string GetString(Hashtable h, string key)
        {
            return h == null ? null : h[key] as string;
        }

        public static Hashtable GetObject(Hashtable h, string key)
        {
            return h == null ? null : h[key] as Hashtable;
        }

        public static ArrayList GetArray(Hashtable h, string key)
        {
            return h == null ? null : h[key] as ArrayList;
        }
    }
}
