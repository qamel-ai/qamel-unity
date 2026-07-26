using System.Globalization;
using System.Text;

namespace QamelCapture
{
    /// <summary>
    /// Minimal allocation-friendly JSON writer for the flat objects Qamel emits.
    /// Avoids external dependencies and JsonUtility's fixed-shape limitations.
    /// </summary>
    internal sealed class QamelJson
    {
        readonly StringBuilder _sb = new StringBuilder(256);
        bool _first = true;

        public QamelJson Begin()
        {
            _sb.Length = 0;
            _first = true;
            _sb.Append('{');
            return this;
        }

        public QamelJson Str(string key, string value)
        {
            Key(key);
            if (value == null)
            {
                _sb.Append("null");
            }
            else
            {
                _sb.Append('"');
                Escape(_sb, value);
                _sb.Append('"');
            }
            return this;
        }

        public QamelJson Num(string key, double value)
        {
            Key(key);
            _sb.Append(value.ToString("0.####", CultureInfo.InvariantCulture));
            return this;
        }

        public QamelJson Int(string key, long value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public string End()
        {
            _sb.Append('}');
            return _sb.ToString();
        }

        void Key(string key)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            _sb.Append('"').Append(key).Append("\":");
        }

        /// <summary>
        /// Extracts a top-level string field from a small JSON object, e.g. the
        /// <c>bundleUploadUrl</c> in an ingest response. Deliberately minimal:
        /// no dependency, no allocation-heavy parser; returns null when the
        /// field is absent or not a string.
        /// </summary>
        public static string ExtractString(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return null;

            string needle = "\"" + field + "\"";
            int keyIndex = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (keyIndex < 0) return null;

            int i = keyIndex + needle.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
            if (i >= json.Length || json[i] != ':') return null;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;

            var sb = new StringBuilder(64);
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    char e = json[i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < json.Length &&
                                int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break; // covers \" \\ \/
                    }
                }
                else
                {
                    sb.Append(c);
                }
                i++;
            }
            return null; // unterminated string
        }

        static void Escape(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
        }
    }
}
