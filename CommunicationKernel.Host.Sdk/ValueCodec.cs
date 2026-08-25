// -----------------------------------------------------------------------------
// 文件: ValueCodec.cs
// 层级: 客户端层 — 所有 UI 共用
// 作用: 变量字节 ↔ 显示文本。UI 不做协议解析，只按操作员选定的数据类型与字节序换算。
//
// 为什么放在 Host.Sdk 而不是各 UI 各写一份：
//   字节序换算是纯粹的数值逻辑，与界面框架无关，但错了就是「写下去的数不对」。
//   Web 端曾单独实现过一份，用 BitConverter 直接编解码（x86 即小端），
//   写 8 进 PLC 变成 2048；WPF 那份是手写大端、恰好正确。
//   两份实现同源不同命，正是本项目在 Modbus×3、两份 gRPC 客户端、
//   两份分帧循环上已经栽过的同一个跟头。收到这里统一，并由单元测试锁住。
// -----------------------------------------------------------------------------

using System.Globalization;

namespace CommunicationKernel.Host.Sdk;

/// <summary>
/// 多字节数值在寄存器里的排列方式。
/// </summary>
/// <remarks>
/// <para>
/// 命名沿用工控圈的通行叫法：把一个 32 位值的四个字节记作 A B C D
/// （A 为最高位字节），枚举名就是它们在寄存器里的实际先后顺序。
/// </para>
/// <para>
/// 为什么要做成<b>按设备</b>可配：字节序既取决于协议，也取决于厂商实现。
/// Modbus 规范只规定了 16 位寄存器内部是大端，跨寄存器的 32 位值怎么摆
/// 完全没规定，于是同样是 Modbus，不同品牌的变频器/PLC 可能是 ABCD，
/// 也可能是 CDAB。写死任何一种都会在换品牌时读出乱码。
/// </para>
/// </remarks>
public enum ByteOrder
{
    /// <summary>大端（高字节在前、高字在前）。Modbus / S7 的标准排列，绝大多数设备用这个。</summary>
    ABCD = 0,

    /// <summary>字交换：字内大端，但低字在前。西门子部分模块、不少变频器用这个。</summary>
    CDAB = 1,

    /// <summary>字节交换：字序不变，字内小端。</summary>
    BADC = 2,

    /// <summary>小端（完全反序）。</summary>
    DCBA = 3,
}

/// <summary>
/// 按变量 DataType 与设备字节序做编解码。Hex 原样显示。
/// </summary>
/// <remarks>
/// <b>基准是大端。</b>三个协议插件出来的字节都已经是大端：
/// Modbus 寄存器本就是网络序，S7 原生大端，MEWTOCOL 插件内部
/// 已用 SwapBytes 把低字节先出的 ASCII 转成了大端。
/// 因此 <see cref="ByteOrder.ABCD"/> 才是正确默认值。
///
/// 此前这里用 BitConverter 直接编解码（x86 上即小端），
/// 写 8 会在 PLC 里变成 2048（0x0008 ↔ 0x0800）。
/// 更隐蔽的是读路径同样按小端解，两个错误互相抵消——
/// 自己读自己看着完全正常，只有拿真设备比对才暴露。
/// </remarks>
public static class ValueCodec
{
    /// <summary>某数据类型默认占用的字节数，供「读取字节数」自动填充。</summary>
    /// <param name="dataType">Bool / Int16 / UInt16 / Int32 / UInt32 / Float / Int64 / UInt64 / Double。</param>
    public static int DefaultLength(string dataType) => dataType switch
    {
        "Bool" => 1,
        "Int16" or "UInt16" => 2,
        "Int32" or "UInt32" or "Float" => 4,
        "Int64" or "UInt64" or "Double" => 8,
        _ => 2,
    };

    /// <summary>把 UI 里存的字符串解析成枚举；无法识别时回落到大端。</summary>
    public static ByteOrder ParseOrder(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out ByteOrder order) ? order : ByteOrder.ABCD;

    /// <summary>把设备返回的字节解释成显示文本。</summary>
    /// <param name="data">协议驱动返回的原始字节（各插件均已归一为大端）。</param>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="order">该设备的字节序，默认大端。</param>
    /// <returns>显示文本；无法按类型解释时回落为十六进制串。</returns>
    public static string Decode(byte[] data, string dataType, ByteOrder order = ByteOrder.ABCD)
    {
        if (data is null || data.Length == 0) return "(空)";
        try
        {
            // Bool 与 Hex 不参与字节序换算：前者只看首字节，后者要的就是原始排列
            if (dataType == "Bool") return data[0] != 0 ? "ON" : "OFF";

            byte[] host = ToHostOrder(data, dataType, order);

            return dataType switch
            {
                "Int16" when host.Length >= 2 => BitConverter.ToInt16(host, 0).ToString(CultureInfo.InvariantCulture),
                "UInt16" when host.Length >= 2 => BitConverter.ToUInt16(host, 0).ToString(CultureInfo.InvariantCulture),
                "Int32" when host.Length >= 4 => BitConverter.ToInt32(host, 0).ToString(CultureInfo.InvariantCulture),
                "UInt32" when host.Length >= 4 => BitConverter.ToUInt32(host, 0).ToString(CultureInfo.InvariantCulture),
                "Float" when host.Length >= 4 => BitConverter.ToSingle(host, 0).ToString("G6", CultureInfo.InvariantCulture),
                "Int64" when host.Length >= 8 => BitConverter.ToInt64(host, 0).ToString(CultureInfo.InvariantCulture),
                "UInt64" when host.Length >= 8 => BitConverter.ToUInt64(host, 0).ToString(CultureInfo.InvariantCulture),
                "Double" when host.Length >= 8 => BitConverter.ToDouble(host, 0).ToString("G6", CultureInfo.InvariantCulture),
                _ => BitConverter.ToString(data).Replace('-', ' '),
            };
        }
        catch
        {
            return BitConverter.ToString(data).Replace('-', ' ');
        }
    }

    /// <summary>把界面输入的文本编成写入设备的字节。</summary>
    /// <param name="text">操作员输入的文本。</param>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="length">声明的字节长度；大于 0 时按此截断或补零。</param>
    /// <param name="data">编码结果。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="order">该设备的字节序，默认大端。</param>
    public static bool TryEncode(
        string text, string dataType, int length, out byte[] data, out string error,
        ByteOrder order = ByteOrder.ABCD)
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

            // 文本 → 强类型值，随后统一交给 TryEncodeValue 做字节序换算。
            // 两条入口（文本 / 强类型值）必须共用同一段换算，
            // 否则又会退化成两份实现各自漂移。
            object? boxed = dataType switch
            {
                "Int16" => short.Parse(input, CultureInfo.InvariantCulture),
                "UInt16" => ushort.Parse(input, CultureInfo.InvariantCulture),
                "Int32" => int.Parse(input, CultureInfo.InvariantCulture),
                "UInt32" => uint.Parse(input, CultureInfo.InvariantCulture),
                "Float" => float.Parse(input, CultureInfo.InvariantCulture),
                "Int64" => long.Parse(input, CultureInfo.InvariantCulture),
                "UInt64" => ulong.Parse(input, CultureInfo.InvariantCulture),
                "Double" => double.Parse(input, CultureInfo.InvariantCulture),
                _ => null,
            };

            if (boxed is null)
                return TryParseHex(input, out data, out error);

            return TryEncodeValue(boxed, dataType, length, out data, out error, order);
        }
        catch (Exception ex)
        {
            error = "无法按 " + dataType + " 解析: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把已经是强类型的值编成设备字节序。
    /// </summary>
    /// <remarks>
    /// 供 WPF 那条「界面文本 → object → 字节」的管线复用：
    /// 它在 ViewModel 层就把文本解析成了强类型值，
    /// 到写入时只差字节序换算这一步，不该再退回字符串重新解析一遍
    /// （Float/Double 往返字符串会丢精度）。
    /// </remarks>
    /// <param name="value">已解析的强类型值。</param>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="length">声明的字节长度；大于 0 时按此截断或补零。</param>
    /// <param name="data">编码结果。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="order">该设备的字节序，默认大端。</param>
    public static bool TryEncodeValue(
        object value, string dataType, int length, out byte[] data, out string error,
        ByteOrder order = ByteOrder.ABCD)
    {
        data = Array.Empty<byte>();
        error = string.Empty;

        try
        {
            if (dataType == "Bool")
            {
                data = new byte[] { Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? (byte)1 : (byte)0 };
                return true;
            }

            // BitConverter 产出的是本机序（x86 即小端）
            byte[] host = dataType switch
            {
                "Int16" => BitConverter.GetBytes(Convert.ToInt16(value, CultureInfo.InvariantCulture)),
                "UInt16" => BitConverter.GetBytes(Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
                "Int32" => BitConverter.GetBytes(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
                "UInt32" => BitConverter.GetBytes(Convert.ToUInt32(value, CultureInfo.InvariantCulture)),
                "Float" => BitConverter.GetBytes(Convert.ToSingle(value, CultureInfo.InvariantCulture)),
                "Int64" => BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                "UInt64" => BitConverter.GetBytes(Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
                "Double" => BitConverter.GetBytes(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                _ => Array.Empty<byte>(),
            };

            if (host.Length == 0)
            {
                error = "不支持的数据类型: " + dataType;
                return false;
            }

            // 本机序 → 设备序。ToHostOrder 是自反的（换一次过去、换两次回来），
            // 所以编码方向复用同一个变换即可。
            data = ToHostOrder(host, dataType, order);

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
            error = "无法按 " + dataType + " 编码: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 在「设备排列」与「本机排列」之间换算。
    /// </summary>
    /// <remarks>
    /// 变换是自反的：同一个 order 施加两次会回到原样，
    /// 因此读（设备→本机）与写（本机→设备）共用这一个方法。
    /// </remarks>
    private static byte[] ToHostOrder(byte[] data, string dataType, ByteOrder order)
    {
        int width = DefaultLength(dataType);
        if (width < 2 || data.Length < width) return data;

        byte[] work = new byte[data.Length];
        Buffer.BlockCopy(data, 0, work, 0, data.Length);

        // 第一步：按 order 把设备排列整理成大端
        switch (order)
        {
            case ByteOrder.ABCD:
                break;                                  // 已是大端
            case ByteOrder.BADC:
                SwapAdjacentBytes(work, width);         // 字内交换
                break;
            case ByteOrder.CDAB:
                SwapAdjacentWords(work, width);         // 字间交换
                break;
            case ByteOrder.DCBA:
                Array.Reverse(work, 0, width);          // 完全反序
                break;
        }

        // 第二步：大端 → 本机序。BitConverter 按本机序解读，
        // 在小端机器上必须再翻一次；大端机器（部分 ARM 配置）则原样。
        if (BitConverter.IsLittleEndian)
            Array.Reverse(work, 0, width);

        return work;
    }

    /// <summary>两两交换字节（AB CD → BA DC）。</summary>
    private static void SwapAdjacentBytes(byte[] buffer, int width)
    {
        for (int i = 0; i + 1 < width; i += 2)
            (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
    }

    /// <summary>两两交换 16 位字（AB CD → CD AB）。宽度不足两字时无操作。</summary>
    private static void SwapAdjacentWords(byte[] buffer, int width)
    {
        for (int i = 0; i + 3 < width; i += 4)
        {
            (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
            (buffer[i + 1], buffer[i + 3]) = (buffer[i + 3], buffer[i + 1]);
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
