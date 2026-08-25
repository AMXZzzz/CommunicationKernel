#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/ValueParser.cs
// 层级: UI 层 — WPF 值解析工具
// 作用: TryParse 把界面字符串解析为写入值；TryParseBytes 把 gRPC 大端字节解析为显示字符串。
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Text;
using CommunicationKernel.Host.Sdk;
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
    /// 把 gRPC 返回的字节按数据类型解析为可读字符串。
    /// 解析失败时 <paramref name="display"/> 为 "?" 并返回 false。
    /// </summary>
    /// <param name="dataType">变量数据类型，决定字节数解析方式。</param>
    /// <param name="data">协议驱动返回的字节（各插件均已归一为大端）。</param>
    /// <param name="display">解析成功时的可读字符串，失败时为 "?"。</param>
    /// <param name="order">该设备的字节序，默认大端。</param>
    /// <remarks>
    /// 字节序换算本体在 <see cref="ValueCodec"/>，此处只做枚举到类型名的映射。
    /// 早先这里手写了一整套大端移位，与 Web 端的实现各写一份——
    /// 那正是「写 8 进 PLC 变 2048」那个缺陷能在一侧潜伏的土壤。
    /// </remarks>
    public static bool TryParseBytes(
        VariableDataType dataType,
        byte[] data,
        out string display,
        ByteOrder order = ByteOrder.ABCD) {

        display = "?";

        // 空数据直接返回失败
        if (data == null || data.Length == 0)
            return false;

        string codecType = ToCodecType(dataType);
        string text = ValueCodec.Decode(data, codecType, order);

        // ValueCodec 用 "(空)" 表示无数据；此处已排除空数组，理论上不会出现
        if (text == "(空)") return false;

        // Bool 的显示措辞两端历来不同：WPF 用 true/false，Web 用 ON/OFF。
        // 界面文案不属于编解码逻辑，收敛时保持各自原样，避免改动现有 WPF 界面。
        if (dataType == VariableDataType.Bool)
            display = data[0] != 0 ? "true" : "false";
        else
            display = text;

        return true;
    }

    /// <summary>
    /// 把 WPF 的 <see cref="VariableDataType"/> 映射为 <see cref="ValueCodec"/> 使用的类型名。
    /// </summary>
    /// <remarks>
    /// 两侧枚举/字符串的差异只此一处，映射集中在这里，
    /// 避免每个调用点各写一遍 ToString() 而在 String/Hex 这类边角上分叉。
    /// </remarks>
    internal static string ToCodecType(VariableDataType dataType) => dataType switch {
        VariableDataType.Bool   => "Bool",
        VariableDataType.Int16  => "Int16",
        VariableDataType.UInt16 => "UInt16",
        VariableDataType.Int32  => "Int32",
        VariableDataType.UInt32 => "UInt32",
        VariableDataType.Int64  => "Int64",
        VariableDataType.UInt64 => "UInt64",
        VariableDataType.Float  => "Float",
        VariableDataType.Double => "Double",
        // WPF 的 String 与 Web 的 Hex 是同一种「原样十六进制」语义
        VariableDataType.String => "Hex",
        _ => "Hex",
    };
}
