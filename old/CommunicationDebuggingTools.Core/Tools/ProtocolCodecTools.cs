using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CommunicationDebuggingTools.Core.Enums;

namespace CommunicationDebuggingTools.Core.Tools {
    /// <summary>
    /// MEWTOCOL 插件共用工具：字序/字节序、字符串编码、数值拆合。
    /// 不含网络与报文收发。
    /// </summary>
    /// 
    /// <summary>
    /// 协议编解码公共工具：字序/字节序转换、字符串编码、数值拆合。
    /// 与协议无关，供所有插件共用；不含网络与报文收发逻辑。
    /// </summary>
    public static class ProtocolCodecTools {
        /// <summary>字内字节交换（逻辑值 ↔ 线上低字节在前）。</summary>
        public static ushort SwapBytes (ushort w) {
            return (ushort)((w << 8) | (w >> 8));
        }

        /// <summary>将设备配置的编码枚举转为 <see cref="Encoding"/>。</summary>
        public static Encoding ResolveEncoding (StringEncodingKind kind) {
            switch (kind) {
                case StringEncodingKind.Ascii:
                    return Encoding.ASCII;
                case StringEncodingKind.DefaultAnsi:
                    // 中文 Windows 上一般为代码页 936（GBK）
                    return Encoding.Default;
                case StringEncodingKind.Utf16Le:
                    return Encoding.Unicode;
                case StringEncodingKind.Utf16Be:
                    return Encoding.BigEndianUnicode;
                default:
                    return Encoding.UTF8;
            }
        }

        /// <summary>按类型估算读请求需要的字数。</summary>
        public static int WordsNeeded (
            VariableDataType type,
            int length,
            StringEncodingKind encoding) {
            switch (type) {
                case VariableDataType.Bool:
                case VariableDataType.Int16:
                case VariableDataType.UInt16:
                    return 1;
                case VariableDataType.Int32:
                case VariableDataType.UInt32:
                case VariableDataType.Float:
                    return 2;
                case VariableDataType.Int64:
                case VariableDataType.UInt64:
                case VariableDataType.Double:
                    return 4;
                case VariableDataType.String: {
                    if (length <= 0)
                        return 1;
                    // length 按「最大字符数」；按编码最大字节数估字数
                    int maxBytes = ResolveEncoding(encoding).GetMaxByteCount(length);
                    int words = (maxBytes + 1) / 2;
                    return words < 1 ? 1 : words;
                }
                default:
                    return 1;
            }
        }

        public static object FromWords (
            ushort[] w,
            VariableDataType type,
            WordOrder wordOrder,
            ByteOrder byteOrder,
            int length,
            StringEncodingKind encoding) {
            if (w == null || w.Length == 0)
                return null;

            switch (type) {
                case VariableDataType.Bool:
                    return w[0] != 0;
                case VariableDataType.Int16:
                    return (short)w[0];
                case VariableDataType.UInt16:
                    return w[0];
                case VariableDataType.Int32:
                    return (int)Combine2(w, wordOrder);
                case VariableDataType.UInt32:
                    return Combine2(w, wordOrder);
                case VariableDataType.Int64:
                    return (long)Combine4(w, wordOrder);
                case VariableDataType.UInt64:
                    return Combine4(w, wordOrder);
                case VariableDataType.Float: {
                    uint bits = Combine2(w, wordOrder);
                    return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                }
                case VariableDataType.Double: {
                    ulong bits = Combine4(w, wordOrder);
                    return BitConverter.ToDouble(BitConverter.GetBytes(bits), 0);
                }
                case VariableDataType.String:
                    return WordsToString(w, byteOrder, length, encoding);
                default:
                    return w[0];
            }
        }

        public static ushort[] ToWords (
            object value,
            VariableDataType type,
            int length,
            WordOrder wordOrder,
            ByteOrder byteOrder,
            StringEncodingKind encoding) {
            switch (type) {
                case VariableDataType.Bool:
                    return new ushort[] { (ushort)(ToBool(value) ? 1 : 0) };
                case VariableDataType.Int16:
                    return new ushort[] { (ushort)(short)ToInt64(value) };
                case VariableDataType.UInt16:
                    return new ushort[] { (ushort)ToInt64(value) };
                case VariableDataType.Int32:
                    return Split2((uint)(int)ToInt64(value), wordOrder);
                case VariableDataType.UInt32:
                    return Split2((uint)ToInt64(value), wordOrder);
                case VariableDataType.Int64:
                    return Split4((ulong)ToInt64(value), wordOrder);
                case VariableDataType.UInt64:
                    return Split4((ulong)ToInt64(value), wordOrder);
                case VariableDataType.Float: {
                    float f = ToFloat(value);
                    uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
                    return Split2(bits, wordOrder);
                }
                case VariableDataType.Double: {
                    double d = ToDouble(value);
                    ulong bits = BitConverter.ToUInt64(BitConverter.GetBytes(d), 0);
                    return Split4(bits, wordOrder);
                }
                case VariableDataType.String:
                    return StringToWords(
                        value != null ? value.ToString() : "",
                        length,
                        byteOrder,
                        encoding);
                default:
                    return new ushort[] { (ushort)ToInt64(value) };
            }
        }

        public static string WordsToString (
            ushort[] w,
            ByteOrder byteOrder,
            int maxChars,
            StringEncodingKind encoding) {
            var bytes = new List<byte>();
            for (int i = 0; i < w.Length; i++) {
                byte hi = (byte)(w[i] >> 8);
                byte lo = (byte)(w[i] & 0xFF);
                if (byteOrder == ByteOrder.BigEndian) {
                    bytes.Add(hi);
                    bytes.Add(lo);
                } else {
                    bytes.Add(lo);
                    bytes.Add(hi);
                }
            }
            while (bytes.Count > 0 && bytes[bytes.Count - 1] == 0)
                bytes.RemoveAt(bytes.Count - 1);

            string s = ResolveEncoding(encoding).GetString(bytes.ToArray());
            if (maxChars > 0 && s.Length > maxChars)
                s = s.Substring(0, maxChars);
            return s;
        }

        public static ushort[] StringToWords (
            string s,
            int maxChars,
            ByteOrder byteOrder,
            StringEncodingKind encoding) {
            if (s == null)
                s = "";
            Encoding enc = ResolveEncoding(encoding);
            if (maxChars > 0 && s.Length > maxChars)
                s = s.Substring(0, maxChars);

            byte[] raw = enc.GetBytes(s);
            int wordCount = (raw.Length + 1) / 2;
            if (wordCount < 1)
                wordCount = 1;

            ushort[] words = new ushort[wordCount];
            for (int i = 0; i < wordCount; i++) {
                int p = i * 2;
                byte b0 = p < raw.Length ? raw[p] : (byte)0;
                byte b1 = p + 1 < raw.Length ? raw[p + 1] : (byte)0;
                if (byteOrder == ByteOrder.BigEndian)
                    words[i] = (ushort)((b0 << 8) | b1);
                else
                    words[i] = (ushort)((b1 << 8) | b0);
            }
            return words;
        }

        public static uint Combine2 (ushort[] w, WordOrder order) {
            ushort a = w != null && w.Length > 0 ? w[0] : (ushort)0;
            ushort b = w != null && w.Length > 1 ? w[1] : (ushort)0;
            if (order == WordOrder.HighWordFirst)
                return ((uint)a << 16) | b;
            return ((uint)b << 16) | a;
        }

        public static ushort[] Split2 (uint v, WordOrder order) {
            ushort lo = (ushort)(v & 0xFFFF);
            ushort hi = (ushort)(v >> 16);
            if (order == WordOrder.HighWordFirst)
                return new ushort[] { hi, lo };
            return new ushort[] { lo, hi };
        }

        public static ulong Combine4 (ushort[] w, WordOrder order) {
            if (order == WordOrder.HighWordFirst) {
                ulong r = 0;
                for (int i = 0; i < 4; i++) {
                    ushort x = w != null && w.Length > i ? w[i] : (ushort)0;
                    r = (r << 16) | x;
                }
                return r;
            }
            ulong r2 = 0;
            for (int i = 3; i >= 0; i--) {
                ushort x = w != null && w.Length > i ? w[i] : (ushort)0;
                r2 = (r2 << 16) | x;
            }
            return r2;
        }

        public static ushort[] Split4 (ulong v, WordOrder order) {
            ushort[] parts = new ushort[4];
            for (int i = 0; i < 4; i++) {
                parts[i] = (ushort)(v & 0xFFFF);
                v >>= 16;
            }
            if (order == WordOrder.HighWordFirst)
                return new ushort[] { parts[3], parts[2], parts[1], parts[0] };
            return parts;
        }

        public static bool ToBool (object v) {
            if (v is bool b)
                return b;
            if (v == null)
                return false;
            string s = v.ToString().Trim();
            if (s == "1" ||
                s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("on", StringComparison.OrdinalIgnoreCase))
                return true;
            long n;
            return long.TryParse(s, out n) && n != 0;
        }

        public static long ToInt64 (object v) {
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is short s) return s;
            if (v is ushort us) return us;
            if (v is uint u) return u;
            long r;
            if (long.TryParse(v != null ? v.ToString() : "", NumberStyles.Any,
                    CultureInfo.InvariantCulture, out r))
                return r;
            return 0;
        }

        public static float ToFloat (object v) {
            if (v is float f) return f;
            if (v is double d) return (float)d;
            float r;
            float.TryParse(v != null ? v.ToString() : "", NumberStyles.Any,
                CultureInfo.InvariantCulture, out r);
            return r;
        }

        public static double ToDouble (object v) {
            if (v is double d) return d;
            if (v is float f) return f;
            double r;
            double.TryParse(v != null ? v.ToString() : "", NumberStyles.Any,
                CultureInfo.InvariantCulture, out r);
            return r;
        }
    }
}