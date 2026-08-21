using System;
using System.Globalization;
using CommunicationDebuggingTools.Core.Enums;

namespace CommunicationDebuggingTools.Business.Variable {

    /// <summary>界面字符串 → 写入值。</summary>
    public static class ValueParser {

        public static bool TryParse (
            VariableDataType dataType,
            string raw,
            out object value,
            out string error) {
            string text = (raw ?? string.Empty).Trim();
            value = null;
            error = null;

            switch (dataType) {
                case VariableDataType.Bool:
                    if (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("ON", StringComparison.OrdinalIgnoreCase)) {
                        value = true;
                        return true;
                    }
                    if (text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("OFF", StringComparison.OrdinalIgnoreCase)) {
                        value = false;
                        return true;
                    }
                    error = "Bool 仅支持 true/false、1/0、ON/OFF";
                    return false;

                case VariableDataType.Int16:
                    if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short i16)) {
                        value = i16;
                        return true;
                    }
                    error = "Int16 格式不正确";
                    return false;

                case VariableDataType.UInt16:
                    if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort u16)) {
                        value = u16;
                        return true;
                    }
                    error = "UInt16 格式不正确";
                    return false;

                case VariableDataType.Int32:
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32)) {
                        value = i32;
                        return true;
                    }
                    error = "Int32 格式不正确";
                    return false;

                case VariableDataType.UInt32:
                    if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u32)) {
                        value = u32;
                        return true;
                    }
                    error = "UInt32 格式不正确";
                    return false;

                case VariableDataType.Int64:
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64)) {
                        value = i64;
                        return true;
                    }
                    error = "Int64 格式不正确";
                    return false;

                case VariableDataType.UInt64:
                    if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u64)) {
                        value = u64;
                        return true;
                    }
                    error = "UInt64 格式不正确";
                    return false;

                case VariableDataType.Float:
                    if (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                            CultureInfo.InvariantCulture, out float f)) {
                        value = f;
                        return true;
                    }
                    error = "Float 格式不正确";
                    return false;

                case VariableDataType.Double:
                    if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                            CultureInfo.InvariantCulture, out double d)) {
                        value = d;
                        return true;
                    }
                    error = "Double 格式不正确";
                    return false;

                case VariableDataType.String:
                    value = text;
                    return true;

                default:
                    value = text;
                    return true;
            }
        }
    }
}