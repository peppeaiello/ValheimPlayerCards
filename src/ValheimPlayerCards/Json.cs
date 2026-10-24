using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimPlayerCards
{
    /// <summary>
    /// Minimal JSON for the player records and the API. Values are Dictionary&lt;string, object&gt;,
    /// List&lt;object&gt;, string, double/long/int/float, bool or null. Unity's JsonUtility is not used
    /// because it does not see the fields of classes loaded from a plugin.
    /// </summary>
    internal static class Json
    {
        public static string Write(object value, bool pretty = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, bool pretty, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: WriteNumber(sb, f); break;
                case double d: WriteNumber(sb, d); break;
                case IDictionary<string, object> obj:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, pretty, depth + 1);
                        WriteString(sb, kv.Key);
                        sb.Append(pretty ? ": " : ":");
                        WriteValue(sb, kv.Value, pretty, depth + 1);
                    }
                    if (!first) NewLine(sb, pretty, depth);
                    sb.Append('}');
                    break;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, pretty, depth + 1);
                        WriteValue(sb, item, pretty, depth + 1);
                    }
                    if (!first) NewLine(sb, pretty, depth);
                    sb.Append(']');
                    break;
                }
                default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }

        private static void NewLine(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty) return;
            sb.Append('\n').Append(' ', depth * 2);
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
            if (Math.Abs(d % 1) < 1e-9 && Math.Abs(d) < 1e15) sb.Append(((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture));
            else sb.Append(d.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // < > & as escapes too, so the JSON is safe to inline in an HTML page.
                        if (c < 0x20 || c == '<' || c == '>' || c == '&' || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- Reading -------------------------------------------------------

        public static object Parse(string text)
        {
            int pos = 0;
            object v = ParseValue(text, ref pos, 0);
            SkipSpace(text, ref pos);
            if (pos != text.Length) throw new FormatException("trailing characters");
            return v;
        }

        private static object ParseValue(string s, ref int pos, int depth)
        {
            if (depth > 32) throw new FormatException("too deep");
            SkipSpace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("unexpected end");
            char c = s[pos];
            if (c == '{')
            {
                pos++;
                var obj = new Dictionary<string, object>();
                SkipSpace(s, ref pos);
                if (Peek(s, pos) == '}') { pos++; return obj; }
                while (true)
                {
                    SkipSpace(s, ref pos);
                    string key = ParseString(s, ref pos);
                    SkipSpace(s, ref pos);
                    Expect(s, ref pos, ':');
                    obj[key] = ParseValue(s, ref pos, depth + 1);
                    SkipSpace(s, ref pos);
                    if (Peek(s, pos) == ',') { pos++; continue; }
                    Expect(s, ref pos, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                pos++;
                var list = new List<object>();
                SkipSpace(s, ref pos);
                if (Peek(s, pos) == ']') { pos++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref pos, depth + 1));
                    SkipSpace(s, ref pos);
                    if (Peek(s, pos) == ',') { pos++; continue; }
                    Expect(s, ref pos, ']');
                    return list;
                }
            }
            if (c == '"') return ParseString(s, ref pos);
            if (Match(s, ref pos, "true")) return true;
            if (Match(s, ref pos, "false")) return false;
            if (Match(s, ref pos, "null")) return null;
            int start = pos;
            while (pos < s.Length && "+-0123456789.eE".IndexOf(s[pos]) >= 0) pos++;
            if (pos == start) throw new FormatException("unexpected character");
            return double.Parse(s.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string ParseString(string s, ref int pos)
        {
            Expect(s, ref pos, '"');
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) break;
                char e = s[pos++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new FormatException("bad escape");
                        sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static void SkipSpace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static char Peek(string s, int pos) => pos < s.Length ? s[pos] : '\0';

        private static void Expect(string s, ref int pos, char c)
        {
            if (Peek(s, pos) != c) throw new FormatException($"expected '{c}'");
            pos++;
        }

        private static bool Match(string s, ref int pos, string word)
        {
            if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0) return false;
            pos += word.Length;
            return true;
        }

        // ---- Typed access to parsed objects --------------------------------

        public static string Str(IDictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is string s ? s : "";

        public static double Num(IDictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is double d ? d : 0;

        public static long Long(IDictionary<string, object> o, string key) => (long)Num(o, key);

        public static int Int(IDictionary<string, object> o, string key) => (int)Num(o, key);

        public static IEnumerable<IDictionary<string, object>> Objects(IDictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || !(v is List<object> list)) yield break;
            foreach (var item in list)
                if (item is IDictionary<string, object> d) yield return d;
        }
    }
}
