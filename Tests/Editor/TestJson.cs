using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QamelCapture.Tests
{
    /// <summary>
    /// Tiny strict parser for the flat JSON objects Qamel emits (string and number
    /// values only, no nesting). Values are returned as strings; numbers keep their
    /// literal form. Throws on anything malformed so tests catch format drift.
    /// </summary>
    internal static class TestJson
    {
        public static Dictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>();
            int i = 0;
            Expect(json, ref i, '{');
            SkipWs(json, ref i);
            if (json[i] == '}') return result;

            while (true)
            {
                SkipWs(json, ref i);
                string key = ParseString(json, ref i);
                SkipWs(json, ref i);
                Expect(json, ref i, ':');
                SkipWs(json, ref i);

                string value;
                if (json[i] == '"')
                {
                    value = ParseString(json, ref i);
                }
                else if (json.Substring(i).StartsWith("null"))
                {
                    value = null;
                    i += 4;
                }
                else
                {
                    int start = i;
                    while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '+' ||
                                               json[i] == '.' || json[i] == 'e' || json[i] == 'E'))
                        i++;
                    value = json.Substring(start, i - start);
                    double.Parse(value, CultureInfo.InvariantCulture); // must be a number
                }
                result[key] = value;

                SkipWs(json, ref i);
                if (json[i] == ',') { i++; continue; }
                Expect(json, ref i, '}');
                if (i != json.Length) throw new System.FormatException("Trailing data after object");
                return result;
            }
        }

        static string ParseString(string json, ref int i)
        {
            Expect(json, ref i, '"');
            var sb = new StringBuilder();
            while (json[i] != '"')
            {
                if (json[i] == '\\')
                {
                    i++;
                    switch (json[i])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            sb.Append((char)int.Parse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new System.FormatException("Bad escape \\" + json[i]);
                    }
                    i++;
                }
                else
                {
                    sb.Append(json[i]);
                    i++;
                }
            }
            i++;
            return sb.ToString();
        }

        static void Expect(string json, ref int i, char c)
        {
            if (json[i] != c) throw new System.FormatException("Expected '" + c + "' at " + i + " in: " + json);
            i++;
        }

        static void SkipWs(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }
    }
}
