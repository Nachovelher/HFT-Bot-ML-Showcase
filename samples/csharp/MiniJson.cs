/*
=============================================================
HFT_BOT_ML — MiniJson: JSON parser sin Newtonsoft
=============================================================
Reemplaza Newtonsoft.Json.Linq (JObject, JArray, JToken)
con un parser propio que solo usa System.*.
Compatible con NinjaTrader 8 (.NET Framework 4.8).
=============================================================
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HFT_BOT_ML
{
    // ─────────────────────────────────────────────────────────
    // JObject replacement
    // ─────────────────────────────────────────────────────────

    public class JObject : IEnumerable<KeyValuePair<string, JToken>>
    {
        private readonly Dictionary<string, JToken> _props
            = new Dictionary<string, JToken>(StringComparer.Ordinal);

        public JToken this[string key]
        {
            get => _props.TryGetValue(key, out var v) ? v : null;
            set => _props[key] = value;
        }

        public bool ContainsKey(string key) => _props.ContainsKey(key);
        public int Count => _props.Count;

        public IEnumerator<KeyValuePair<string, JToken>> GetEnumerator()
            => _props.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static JObject Parse(string json)
        {
            var parser = new JsonParser(json);
            var val = parser.ParseValue();
            return val?.AsJObject() ?? new JObject();
        }
    }


    // ─────────────────────────────────────────────────────────
    // JArray replacement
    // ─────────────────────────────────────────────────────────

    public class JArray : IEnumerable<JToken>
    {
        private readonly List<JToken> _items = new List<JToken>();

        public JToken this[int index]
        {
            get => (index >= 0 && index < _items.Count) ? _items[index] : null;
            set { while (_items.Count <= index) _items.Add(null); _items[index] = value; }
        }

        public int Count => _items.Count;
        public void Add(JToken item) => _items.Add(item);

        public IEnumerator<JToken> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public List<string> Values_String()
        {
            var result = new List<string>();
            foreach (var item in _items)
                result.Add(item?.ToString() ?? "");
            return result;
        }
    }


    // ─────────────────────────────────────────────────────────
    // JToken replacement — wraps any JSON value
    // ─────────────────────────────────────────────────────────

    public class JToken
    {
        internal object _value;

        public JToken() { _value = null; }
        public JToken(object val) { _value = val; }

        // Navigation
        public virtual JToken this[string key]
        {
            get
            {
                if (_value is JObject obj) return obj[key];
                return null;
            }
        }

        public virtual JToken this[int index]
        {
            get
            {
                if (_value is JArray arr) return arr[index];
                return null;
            }
        }

        // Safe type access (replaces "as JObject" / "as JArray")
        public JObject AsJObject() => _value as JObject;
        public JArray  AsJArray()  => _value as JArray;

        // Type checks
        public bool IsObject => _value is JObject;
        public bool IsArray => _value is JArray;
        public bool IsNull => _value == null;

        // Value extraction
        public T Value<T>()
        {
            if (_value == null) return default(T);

            Type t = typeof(T);
            try
            {
                if (t == typeof(double))
                    return (T)(object)Convert.ToDouble(_value, CultureInfo.InvariantCulture);
                if (t == typeof(float))
                    return (T)(object)Convert.ToSingle(_value, CultureInfo.InvariantCulture);
                if (t == typeof(int))
                    return (T)(object)Convert.ToInt32(_value, CultureInfo.InvariantCulture);
                if (t == typeof(long))
                    return (T)(object)Convert.ToInt64(_value, CultureInfo.InvariantCulture);
                if (t == typeof(bool))
                    return (T)(object)Convert.ToBoolean(_value, CultureInfo.InvariantCulture);
                if (t == typeof(string))
                    return (T)(object)_value.ToString();
            }
            catch { return default(T); }

            return default(T);
        }

        // ToObject for arrays
        public T ToObject<T>()
        {
            Type t = typeof(T);

            if (t == typeof(string[]))
            {
                if (_value is JArray arr)
                    return (T)(object)arr.Cast<JToken>().Select(x => x?.ToString() ?? "").ToArray();
                return default(T);
            }

            if (t == typeof(double[]))
            {
                if (_value is JArray arr)
                    return (T)(object)arr.Cast<JToken>()
                        .Select(x => x?.Value<double>() ?? 0).ToArray();
                return default(T);
            }

            if (t == typeof(int[]))
            {
                if (_value is JArray arr)
                    return (T)(object)arr.Cast<JToken>()
                        .Select(x => x?.Value<int>() ?? 0).ToArray();
                return default(T);
            }

            if (t == typeof(bool[]))
            {
                if (_value is JArray arr)
                    return (T)(object)arr.Cast<JToken>()
                        .Select(x => x?.Value<bool>() ?? false).ToArray();
                return default(T);
            }

            if (t == typeof(double[][]))
            {
                if (_value is JArray outerArr)
                {
                    var result = new double[outerArr.Count][];
                    for (int i = 0; i < outerArr.Count; i++)
                    {
                        if (outerArr[i]?._value is JArray innerArr)
                            result[i] = innerArr.Cast<JToken>()
                                .Select(x => x?.Value<double>() ?? 0).ToArray();
                        else
                            result[i] = new double[0];
                    }
                    return (T)(object)result;
                }
                return default(T);
            }

            if (t == typeof(List<string>))
            {
                if (_value is JArray arr)
                    return (T)(object)arr.Cast<JToken>()
                        .Select(x => x?.ToString() ?? "").ToList();
                return default(T);
            }

            return default(T);
        }

        // Values<T> for JArray items
        public IEnumerable<string> Values_String()
        {
            if (_value is JArray arr)
                return arr.Cast<JToken>().Select(x => x?.ToString() ?? "");
            return Enumerable.Empty<string>();
        }

        public override string ToString()
        {
            if (_value == null) return "";
            if (_value is string s) return s;
            if (_value is bool b) return b ? "true" : "false";
            if (_value is double d) return d.ToString(CultureInfo.InvariantCulture);
            return _value.ToString();
        }

        // Implicit conversions for convenience
        public static implicit operator JToken(string s) => new JToken(s);
        public static implicit operator JToken(double d) => new JToken(d);
        public static implicit operator JToken(int i) => new JToken(i);
        public static implicit operator JToken(bool b) => new JToken(b);
        public static implicit operator JToken(JObject obj) => new JToken(obj) { _value = obj };
        public static implicit operator JToken(JArray arr) => new JToken(arr) { _value = arr };

        // Explicit cast operators for Newtonsoft compatibility
        public static explicit operator JObject(JToken t)
            => t?._value as JObject;
        public static explicit operator JArray(JToken t)
            => t?._value as JArray;
    }


    // ─────────────────────────────────────────────────────────
    // JSON PARSER — recursive descent
    // ─────────────────────────────────────────────────────────

    internal class JsonParser
    {
        private readonly string _json;
        private int _pos;

        public JsonParser(string json)
        {
            _json = json ?? "";
            _pos = 0;
        }

        public JToken ParseValue()
        {
            SkipWhitespace();
            if (_pos >= _json.Length) return new JToken(null);

            char c = _json[_pos];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (c == 't' || c == 'f') return ParseBool();
            if (c == 'n') return ParseNull();
            return ParseNumber();
        }

        private JToken ParseObject()
        {
            var obj = new JObject();
            _pos++; // skip '{'
            SkipWhitespace();

            if (_pos < _json.Length && _json[_pos] == '}')
            {
                _pos++;
                return new JToken(obj) { _value = obj };
            }

            while (_pos < _json.Length)
            {
                SkipWhitespace();
                if (_pos >= _json.Length || _json[_pos] == '}') break;

                string key = ReadString();
                SkipWhitespace();
                if (_pos < _json.Length && _json[_pos] == ':') _pos++;
                SkipWhitespace();

                JToken value = ParseValue();
                obj[key] = value;

                SkipWhitespace();
                if (_pos < _json.Length && _json[_pos] == ',') _pos++;
                else break;
            }

            if (_pos < _json.Length && _json[_pos] == '}') _pos++;
            var token = new JToken(obj);
            token._value = obj;
            return token;
        }

        private JToken ParseArray()
        {
            var arr = new JArray();
            _pos++; // skip '['
            SkipWhitespace();

            if (_pos < _json.Length && _json[_pos] == ']')
            {
                _pos++;
                var t = new JToken(arr);
                t._value = arr;
                return t;
            }

            while (_pos < _json.Length)
            {
                SkipWhitespace();
                if (_pos >= _json.Length || _json[_pos] == ']') break;

                arr.Add(ParseValue());

                SkipWhitespace();
                if (_pos < _json.Length && _json[_pos] == ',') _pos++;
                else break;
            }

            if (_pos < _json.Length && _json[_pos] == ']') _pos++;
            var token = new JToken(arr);
            token._value = arr;
            return token;
        }

        private JToken ParseString()
        {
            return new JToken(ReadString());
        }

        private string ReadString()
        {
            if (_pos >= _json.Length || _json[_pos] != '"')
                return "";
            _pos++; // skip opening quote

            var sb = new StringBuilder();
            while (_pos < _json.Length)
            {
                char c = _json[_pos];
                if (c == '\\')
                {
                    _pos++;
                    if (_pos < _json.Length)
                    {
                        char esc = _json[_pos];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_pos + 4 < _json.Length)
                                {
                                    string hex = _json.Substring(_pos + 1, 4);
                                    sb.Append((char)Convert.ToInt32(hex, 16));
                                    _pos += 4;
                                }
                                break;
                            default: sb.Append(esc); break;
                        }
                    }
                }
                else if (c == '"')
                {
                    _pos++; // skip closing quote
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
                _pos++;
            }
            return sb.ToString();
        }

        private JToken ParseNumber()
        {
            int start = _pos;
            bool negative = false;
            if (_pos < _json.Length && (_json[_pos] == '-' || _json[_pos] == '+'))
            {
                negative = _json[_pos] == '-';
                _pos++;
            }

            // XGBoost y json.dumps de Python escriben estos literales sin comillas
            // (p.ej. en sum_hessian). Antes caian aqui como "numero vacio" = 0.0 sin
            // avanzar el cursor, y el resto del documento se descartaba en silencio.
            if (MatchLiteral("NaN")) return new JToken(double.NaN);
            if (MatchLiteral("Infinity"))
                return new JToken(negative ? double.NegativeInfinity : double.PositiveInfinity);

            while (_pos < _json.Length)
            {
                char c = _json[_pos];
                if (char.IsDigit(c)) { _pos++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                {
                    _pos++; continue;
                }
                break;
            }

            string numStr = _json.Substring(start, _pos - start);
            double d;
            if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw Fail("numero invalido '" + numStr + "'");
            return new JToken(d);
        }

        private JToken ParseBool()
        {
            if (MatchLiteral("true")) return new JToken(true);
            if (MatchLiteral("false")) return new JToken(false);
            throw Fail("literal booleano invalido");
        }

        private JToken ParseNull()
        {
            if (MatchLiteral("null")) return new JToken(null);
            throw Fail("literal 'null' invalido");
        }

        /// <summary>
        /// Consume el literal si esta en la posicion actual. Comparacion ordinal in situ:
        /// antes se hacia Substring(_pos).StartsWith(...), que copiaba el resto del JSON en
        /// cada booleano (O(n^2) en arrays largos) y comparaba segun la cultura.
        /// </summary>
        private bool MatchLiteral(string literal)
        {
            if (string.CompareOrdinal(_json, _pos, literal, 0, literal.Length) != 0) return false;
            _pos += literal.Length;
            return true;
        }

        private Exception Fail(string what)
        {
            return new FormatException("JSON invalido en la posicion " + _pos + ": " + what);
        }

        private void SkipWhitespace()
        {
            while (_pos < _json.Length && char.IsWhiteSpace(_json[_pos]))
                _pos++;
        }
    }
}
