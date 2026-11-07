using System.Globalization;
using System.Text;

namespace PerfProbe
{
    /// <summary>手写 JSON 片段。不引第三方序列化器，避免与游戏内 JsonSerialization 的版本差异。</summary>
    internal static class Json
    {
        /// <summary>转义成可安全放进 JSON 字符串的值（不含两侧引号）。</summary>
        public static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            var sb = new StringBuilder(s.Length + 8);
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7F)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>写 "key":"value"（含逗号由调用方决定）。</summary>
        public static void KeyStr(StringBuilder sb, string key, string val)
        {
            sb.Append('"').Append(key).Append("\":\"").Append(Esc(val)).Append('"');
        }

        /// <summary>写 "key":number。NaN/Infinity 落成 null，保证 JSON 合法。</summary>
        public static void KeyNum(StringBuilder sb, string key, double val)
        {
            sb.Append('"').Append(key).Append("\":").Append(Num(val));
        }

        public static void KeyInt(StringBuilder sb, string key, long val)
        {
            sb.Append('"').Append(key).Append("\":").Append(val.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>数值格式化。保留 4 位小数，去掉尾随 0，避免文件膨胀。</summary>
        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return "null";
            }
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string Num(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                return "null";
            }
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
