#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/ValueParser.cs
// 层级: UI 层 — WPF 值解析工具
// 作用: TryParse 把界面字符串解析为写入值；TryParseBytes 把 gRPC 大端字节解析为显示字符串。
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Text;
using CommunicationKernel.UI.Wpf.Core.Enums;

namespace CommunicationKernel.UI.Wpf.Services;

/// <summary>界面字符串 → 写入值解析器。</summary>
public static class ValueParser {

    /// <summary>
    /// 尝试将字符串 <paramref name="raw"/> 解析为 <paramref name="dataType"/> 对应的类型。
    /// </summary>
    /// <param name="dataType">目标数据类型。</param>
    /// <param name="raw">界面输入字符串。</param>
    /// <param name="value">解析成功时返回强类型值，否则 null。</param>
    /// <param name="error">解析失败时返回用户可读错误描述，否则 null。</param>
    /// <returns>解析成功返回 true，否则 false。</returns>
    public static bool TryParse(
        VariableDataType dataType,
        string raw,
        out object value,
        out string error) {

        // 去除首尾空白，兼容用户粘贴带空格的值
        string text = (raw ?? string.Empty).Trim();
        value = null;
        error = null;

        switch (dataType) {
            case VariableDataType.Bool:
                // 支持 true/false、1/0、ON/OFF 三种写法
                if (text == "1"
                    || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("ON",   StringComparison.OrdinalIgnoreCase)) {
                    value = true;
                    return true;
                }
                if (text == "0"
                    || text.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("OFF",   StringComparison.OrdinalIgnoreCase)) {
                    value = false;
                    return true;
                }
                error = "Bool 仅支持 true/false、1/0、ON/OFF";
                return false;

            case VariableDataType.Int16:
                // 有符号 16 位整数
                if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short i16)) {
                    value = i16;
                    return true;
                }
                error = "Int16 格式不正确";
                return false;

            case VariableDataType.UInt16:
                // 无符号 16 位整数
                if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort u16)) {
                    value = u16;
                    return true;
                }
                error = "UInt16 格式不正确";
                return false;

            case VariableDataType.Int32:
                // 有符号 32 位整数
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32)) {
                    value = i32;
                    return true;
                }
                error = "Int32 格式不正确";
                return false;

            case VariableDataType.UInt32:
                // 无符号 32 位整数
                if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u32)) {
                    value = u32;
                    return true;
                }
                error = "UInt32 格式不正确";
                return false;

            case VariableDataType.Int64:
                // 有符号 64 位整数
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64)) {
                    value = i64;
                    return true;
                }
                error = "Int64 格式不正确";
                return false;

            case VariableDataType.UInt64:
                // 无符号 64 位整数
                if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u64)) {
                    value = u64;
                    return true;
                }
                error = "UInt64 格式不正确";
                return false;

            case VariableDataType.Float:
                // 单精度，允许千分位
                if (float.TryParse(text,
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out float f)) {
                    value = f;
                    return true;
                }
                error = "Float 格式不正确";
                return false;

            case VariableDataType.Double:
                // 双精度，允许千分位
                if (double.TryParse(text,
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out double d)) {
                    value = d;
                    return true;
                }
                error = "Double 格式不正确";
                return false;

            case VariableDataType.String:
                // 字符串类型直接返回输入文本
                value = text;
                return true;

            default:
                // 未知类型：将文本原样返回，避免崩溃
                value = text;
                return true;
        }
    }

    // =========================================================================
    // 字节数组 → 显示字符串（供轮询读取结果展示）
    // =========================================================================

    /// <summary>
    /// 将 gRPC ReadResponse 返回的字节数组（大端序）解析为可读字符串。
    /// 解析失败时 <paramref name="display"/> 为 "?" 并返回 false。
    /// </summary>
    /// <param name="dataType">变量数据类型，决定字节数解析方式。</param>
    /// <param name="data">gRPC 返回的大端序字节数组；null 或长度不足时解析失败。</param>
    /// <param name="display">解析成功时的可读字符串，失败时为 "?"。</param>
    /// <returns>解析成功返回 true，字节数不足或类型不支持返回 false。</returns>
    public static bool TryParseBytes(
        VariableDataType dataType,
        byte[] data,
        out string display) {

        display = "?";

        // 空数据直接返回失败
        if (data == null || data.Length == 0)
            return false;

        try {
            switch (dataType) {

                case VariableDataType.Bool:
                    // 1 字节，非零即为 true
                    if (data.Length < 1) return false;
                    display = data[0] != 0 ? "true" : "false";
                    return true;

                case VariableDataType.Int16:
                    // 2 字节大端序 → short
                    if (data.Length < 2) return false;
                    display = ((short)((data[0] << 8) | data[1]))
                        .ToString(CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.UInt16:
                    // 2 字节大端序 → ushort
                    if (data.Length < 2) return false;
                    display = ((ushort)((data[0] << 8) | data[1]))
                        .ToString(CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.Int32:
                    // 4 字节大端序 → int
                    if (data.Length < 4) return false;
                    display = ((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3])
                        .ToString(CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.UInt32:
                    // 4 字节大端序 → uint
                    if (data.Length < 4) return false;
                    display = ((uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]))
                        .ToString(CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.Int64:
                    // 8 字节大端序 → long
                    if (data.Length < 8) return false;
                    long i64 = 0;
                    for (int i = 0; i < 8; i++) i64 = (i64 << 8) | data[i];
                    display = i64.ToString(CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.UInt64:
                    // 8 字节大端序 → ulong
                    if (data.Length < 8) return false;
                    ulong u64 = 0;
                    for (int i = 0; i < 8; i++) u64 = (u64 << 8) | data[i];
                    display = u64.ToString(CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.Float:
                    // 4 字节 IEEE 754 大端序 → float
                    if (data.Length < 4) return false;
                    byte[] fBuf = { data[3], data[2], data[1], data[0] }; // 转为小端供 BitConverter
                    display = BitConverter.ToSingle(fBuf, 0)
                        .ToString("G6", CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.Double:
                    // 8 字节 IEEE 754 大端序 → double
                    if (data.Length < 8) return false;
                    byte[] dBuf = new byte[8];
                    for (int i = 0; i < 8; i++) dBuf[i] = data[7 - i]; // 翻转为小端
                    display = BitConverter.ToDouble(dBuf, 0)
                        .ToString("G10", CultureInfo.InvariantCulture);
                    return true;

                case VariableDataType.String:
                    // UTF-8 字节 → string
                    display = Encoding.UTF8.GetString(data);
                    return true;

                default:
                    // 未知类型：以十六进制原样展示，便于调试
                    display = BitConverter.ToString(data).Replace("-", " ");
                    return true;
            }
        }
        catch {
            // 解析异常（如 BitConverter 越界）：返回失败
            display = "?";
            return false;
        }
    }
}
