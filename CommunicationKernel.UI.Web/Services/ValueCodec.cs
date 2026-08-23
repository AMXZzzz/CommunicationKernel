// -----------------------------------------------------------------------------
// 文件: Services/ValueCodec.cs
// 层级: UI 层 — Blazor Server
// 作用: 变量字节 ↔ 显示文本。UI 不做协议解析，只按操作员选定的数据类型解码。
// -----------------------------------------------------------------------------

using System.Globalization;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>按变量 DataType 做小端解码/编码。Hex 原样显示。</summary>
public static class ValueCodec
{
    public static int DefaultLength(string dataType) => dataType switch
    {
        "Bool" => 1,
        "Int16" or "UInt16" => 2,
        "Int32" or "UInt32" or "Float" => 4,
        "Int64" or "UInt64" or "Double" => 8,
        _ => 2,
    };

    public static string Decode(byte[] data, string dataType)
    {
        if (data is null || data.Length == 0) return "(空)";
        try
        {
            return dataType switch
            {
                "Bool" => data[0] != 0 ? "ON" : "OFF",
                "Int16" when data.Length >= 2 => BitConverter.ToInt16(data, 0).ToString(CultureInfo.InvariantCulture),
                "UInt16" when data.Length >= 2 => BitConverter.ToUInt16(data, 0).ToString(CultureInfo.InvariantCulture),
                "Int32" when data.Length >= 4 => BitConverter.ToInt32(data, 0).ToString(CultureInfo.InvariantCulture),
                "UInt32" when data.Length >= 4 => BitConverter.ToUInt32(data, 0).ToString(CultureInfo.InvariantCulture),
                "Float" when data.Length >= 4 => BitConverter.ToSingle(data, 0).ToString("G6", CultureInfo.InvariantCulture),
                "Int64" when data.Length >= 8 => BitConverter.ToInt64(data, 0).ToString(CultureInfo.InvariantCulture),
                "UInt64" when data.Length >= 8 => BitConverter.ToUInt64(data, 0).ToString(CultureInfo.InvariantCulture),
                "Double" when data.Length >= 8 => BitConverter.ToDouble(data, 0).ToString("G6", CultureInfo.InvariantCulture),
                _ => BitConverter.ToString(data).Replace('-', ' '),
            };
        }
        catch
        {
            return BitConverter.ToString(data).Replace('-', ' ');
        }
    }

    public static bool TryEncode(string text, string dataType, int length, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = string.Empty;
        string input = (text ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            error = "写入值不能为空";
            return false;
        }

        try
        {
            if (dataType == "Hex" || dataType == "String")
                return TryParseHex(input, out data, out error);

            if (dataType == "Bool")
            {
                bool on = input is "1" or "true" or "TRUE" or "on" or "ON" or "yes";
                data = new byte[] { on ? (byte)1 : (byte)0 };
                return true;
            }

            data = dataType switch
            {
                "Int16" => BitConverter.GetBytes(short.Parse(input, CultureInfo.InvariantCulture)),
                "UInt16" => BitConverter.GetBytes(ushort.Parse(input, CultureInfo.InvariantCulture)),
                "Int32" => BitConverter.GetBytes(int.Parse(input, CultureInfo.InvariantCulture)),
                "UInt32" => BitConverter.GetBytes(uint.Parse(input, CultureInfo.InvariantCulture)),
                "Float" => BitConverter.GetBytes(float.Parse(input, CultureInfo.InvariantCulture)),
                "Int64" => BitConverter.GetBytes(long.Parse(input, CultureInfo.InvariantCulture)),
                "UInt64" => BitConverter.GetBytes(ulong.Parse(input, CultureInfo.InvariantCulture)),
                "Double" => BitConverter.GetBytes(double.Parse(input, CultureInfo.InvariantCulture)),
                _ => Array.Empty<byte>(),
            };

            if (data.Length == 0)
                return TryParseHex(input, out data, out error);

            if (length > 0 && data.Length != length)
            {
                // 按声明长度截断或补零，避免协议层因 length 与负载不一致拒写
                byte[] sized = new byte[length];
                Buffer.BlockCopy(data, 0, sized, 0, Math.Min(data.Length, length));
                data = sized;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "无法按 " + dataType + " 解析: " + ex.Message;
            return false;
        }
    }

    private static bool TryParseHex(string input, out byte[] data, out string error)
    {
        error = string.Empty;
        string clean = input.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (clean.Length == 0 || clean.Length % 2 != 0)
        {
            data = Array.Empty<byte>();
            error = "十六进制长度必须为偶数，例如 00 FF";
            return false;
        }
        try
        {
            data = new byte[clean.Length / 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return true;
        }
        catch
        {
            data = Array.Empty<byte>();
            error = "不是合法十六进制";
            return false;
        }
    }
}
