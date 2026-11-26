// 最小 JSON 解析器：只覆盖本项目内容文件用到的子集，纯 BCL，不引任何包。
//
// 为什么不直接用 System.Text.Json：
//   1. Domain 与 Content 程序集都是 noEngineReferences，Unity 里的可用类型集取决于
//      API Compatibility Level，System.Text.Json 不是 .NET Standard 2.1 的成员，
//      在编辑器里能不能编过取决于项目是否额外塞了 DLL。加载器不该被这件事绑住。
//   2. 这里要的只是「嵌套对象 + 数组 + 数字 + 字符串」，不需要序列化、不需要 schema。
//
// 解析结果的映射：
//   object → Dictionary<string, object>（键区分大小写）
//   array  → List<object>
//   number → double（调用方按需转 float/int）
//   string → string；true/false → bool；null → null
//
// 不做的事：注释、尾逗号、单引号字符串、NaN/Infinity。内容文件不该有这些，
// 出现了就是格式错误，应当报错而不是猜。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StarPawn.Combat.Content
{
    public static class MiniJson
    {
        /// <summary>嵌套深度上限。防止坏文件把递归打爆（栈溢出比报错难查得多）。</summary>
        private const int MaxDepth = 32;

        public static bool TryParse(string text, out object value, out string error)
        {
            value = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "内容为空";
                return false;
            }

            var parser = new Parser { Text = text, Index = 0 };
            try
            {
                parser.SkipWhitespace();
                value = parser.ReadValue(0);
                parser.SkipWhitespace();
                if (parser.Index < parser.Text.Length)
                {
                    error = "根值之后还有多余字符（位置 " + parser.Index + "）";
                    value = null;
                    return false;
                }
                return true;
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                value = null;
                return false;
            }
        }

        /// <summary>失败直接抛 FormatException。加载器内部用 TryParse，这个方法给测试与工具用。</summary>
        public static object Parse(string text)
        {
            object value;
            string error;
            if (!TryParse(text, out value, out error)) throw new FormatException(error);
            return value;
        }

        // ---- 取值辅助：所有读取都走这里，缺键与类型不符都返回 false，不抛 ----

        public static bool TryGetObject(object node, string key, out Dictionary<string, object> value)
        {
            value = null;
            var obj = node as Dictionary<string, object>;
            object child;
            if (obj == null || !obj.TryGetValue(key, out child)) return false;
            value = child as Dictionary<string, object>;
            return value != null;
        }

        public static bool TryGetNumber(object node, string key, out double value)
        {
            value = 0d;
            var obj = node as Dictionary<string, object>;
            object child;
            if (obj == null || !obj.TryGetValue(key, out child)) return false;
            if (!(child is double)) return false;
            value = (double)child;
            return true;
        }

        public static bool TryGetString(object node, string key, out string value)
        {
            value = null;
            var obj = node as Dictionary<string, object>;
            object child;
            if (obj == null || !obj.TryGetValue(key, out child)) return false;
            value = child as string;
            return value != null;
        }

        /// <summary>取数组。元素可能是 object（可能为 null），由调用方各自解释。</summary>
        public static bool TryGetArray(object node, string key, out List<object> value)
        {
            value = null;
            var obj = node as Dictionary<string, object>;
            object child;
            if (obj == null || !obj.TryGetValue(key, out child)) return false;
            value = child as List<object>;
            return value != null;
        }

        private struct Parser
        {
            public string Text;
            public int Index;

            public void SkipWhitespace()
            {
                while (Index < Text.Length)
                {
                    char c = Text[Index];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Index++;
                    else break;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("嵌套超过 " + MaxDepth + " 层");
                SkipWhitespace();
                if (Index >= Text.Length) throw new FormatException("内容意外结束");

                char c = Text[Index];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException("无法识别的字符 '" + c + "'（位置 " + Index + "）");
                }
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                var result = new Dictionary<string, object>();
                Index++;                     // '{'
                SkipWhitespace();
                if (Peek() == '}') { Index++; return result; }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw new FormatException("对象的键必须是字符串（位置 " + Index + "）");
                    string key = ReadString();

                    SkipWhitespace();
                    if (Peek() != ':') throw new FormatException("键 \"" + key + "\" 之后缺少冒号（位置 " + Index + "）");
                    Index++;

                    object val = ReadValue(depth + 1);
                    // 重复键直接报错：静默取最后一个会让「配置里写了两遍」这种错误永远查不出来。
                    if (result.ContainsKey(key))
                        throw new FormatException("键重复: \"" + key + "\"（位置 " + Index + "）");
                    result[key] = val;

                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { Index++; continue; }
                    if (c == '}') { Index++; return result; }
                    throw new FormatException("对象里缺少逗号或右花括号（位置 " + Index + "）");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var result = new List<object>();
                Index++;                     // '['
                SkipWhitespace();
                if (Peek() == ']') { Index++; return result; }

                while (true)
                {
                    result.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { Index++; continue; }
                    if (c == ']') { Index++; return result; }
                    throw new FormatException("数组里缺少逗号或右方括号（位置 " + Index + "）");
                }
            }

            private string ReadString()
            {
                Index++;                     // '"'
                var sb = new StringBuilder();
                while (true)
                {
                    if (Index >= Text.Length) throw new FormatException("字符串没有闭合");
                    char c = Text[Index++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }

                    if (Index >= Text.Length) throw new FormatException("转义序列不完整");
                    char esc = Text[Index++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Index + 4 > Text.Length) throw new FormatException("\\u 转义不完整");
                            int code;
                            if (!int.TryParse(Text.Substring(Index, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out code))
                                throw new FormatException("\\u 转义不是合法的十六进制");
                            sb.Append((char)code);
                            Index += 4;
                            break;
                        default: throw new FormatException("未知转义 \\" + esc);
                    }
                }
            }

            private double ReadNumber()
            {
                int start = Index;
                if (Peek() == '-') Index++;
                while (Index < Text.Length)
                {
                    char c = Text[Index];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') Index++;
                    else break;
                }

                double value;
                if (!double.TryParse(Text.Substring(start, Index - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
                    throw new FormatException("数字格式非法: " + Text.Substring(start, Index - start));
                return value;
            }

            private void Expect(string literal)
            {
                if (Index + literal.Length > Text.Length ||
                    string.CompareOrdinal(Text, Index, literal, 0, literal.Length) != 0)
                    throw new FormatException("期望 \"" + literal + "\"（位置 " + Index + "）");
                Index += literal.Length;
            }

            private char Peek() => Index < Text.Length ? Text[Index] : '\0';
        }
    }
}
