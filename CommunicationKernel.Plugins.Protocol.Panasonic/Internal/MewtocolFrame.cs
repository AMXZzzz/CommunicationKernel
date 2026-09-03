// -----------------------------------------------------------------------------
// 文件: MewtocolFrame.cs
// 层级: Plugins / Panasonic / Internal
// 作用: 松下 MEWTOCOL-COM 帧构建与响应解析工具。
// 协议帧格式（ASCII over TCP）:
//   请求：% + SS + # + CommandBody + BCC + CR
//   响应：% + SS + $ + ResponseBody + BCC + CR  （成功）
//         % + SS + ! + ErrorCode   + BCC + CR  （错误）
//   SS  = 站号（2 位十六进制，01-63）
//   BCC = CommandBody 全部字符的 XOR（2 位十六进制）
// 数据字节序:
//   MEWTOCOL 数据寄存器每字低字节在前（小端），十六进制文本表示时也是低字节先出现。
//   解析时需 SwapBytes 转换为大端（.NET 标准）。
// 说明:
//   协议帧细节仅在本文件内可见，外层禁止感知任何协议语义。
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Text;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Panasonic.Internal;

/// <summary>
/// MEWTOCOL 帧工具，构建 ASCII 请求并解析 ASCII 响应。
/// </summary>
internal static class MewtocolFrame
{
    // -------------------------------------------------------------------------
    // 帧构建：读
    // -------------------------------------------------------------------------

    /// <summary>
    /// 构建读触点（RCS）请求帧字节。
    /// </summary>
    /// <param name="addr">已解析的地址（IsBit=true）。</param>
    internal static byte[] BuildReadContact(MewtocolAddressInfo addr)
    {
        // RCS + 触点编号（X/Y/R 定长文本）
        string contactStr = FormatContact(addr);
        return BuildFrame(addr.Station, "RCS" + contactStr);
    }

    /// <summary>
    /// 构建读数据区（RD）请求帧字节。
    /// </summary>
    /// <param name="addr">已解析的地址（IsBit=false）。</param>
    /// <param name="wordCount">要读取的字数（1 字=2 字节）。</param>
    internal static byte[] BuildReadData(MewtocolAddressInfo addr, int wordCount)
    {
        // 至少读 1 个字，避免构出空范围
        if (wordCount < 1) wordCount = 1;
        string range = FormatDataRange(addr, wordCount);
        return BuildFrame(addr.Station, "RD" + range);
    }

    // -------------------------------------------------------------------------
    // 帧构建：写
    // -------------------------------------------------------------------------

    /// <summary>
    /// 构建写触点（WCS）请求帧字节。
    /// </summary>
    internal static byte[] BuildWriteContact(MewtocolAddressInfo addr, bool value)
    {
        // WCS + 触点编号 + 0/1
        string contactStr = FormatContact(addr);
        return BuildFrame(addr.Station, "WCS" + contactStr + (value ? "1" : "0"));
    }

    /// <summary>
    /// 构建写数据区（WD）请求帧字节。
    /// </summary>
    /// <param name="addr">已解析的地址（IsBit=false）。</param>
    /// <param name="words">要写入的字数组（大端，网络字节序）。</param>
    internal static byte[] BuildWriteData(MewtocolAddressInfo addr, ushort[] words)
    {
        string range = FormatDataRange(addr, words.Length);
        var sb = new StringBuilder("WD");
        sb.Append(range);
        foreach (ushort w in words)
            sb.Append(SwapBytes(w).ToString("X4"));    // 低字节先出
        return BuildFrame(addr.Station, sb.ToString());
    }

    // -------------------------------------------------------------------------
    // 响应解析
    // -------------------------------------------------------------------------

    /// <summary>
    /// 解析读触点响应，返回布尔字节（0x00 或 0x01）。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadContactResponse(byte[] responseBytes)
    {
        string resp = Decode(responseBytes);

        // 先验 BCC 再解释内容：帧若已被干扰损坏，里面的错误码同样不可信
        OperationResult bccCheck = VerifyBcc(resp);
        if (!bccCheck.Success)
            return OperationResult<byte[]>.Fail(bccCheck.ErrorMessage, bccCheck.ErrorCode);

        OperationResult errCheck = CheckError(resp);
        if (!errCheck.Success)
            return OperationResult<byte[]>.Fail(errCheck.ErrorMessage, errCheck.ErrorCode);

        // 响应体：%SS$RCSS1<BCC>CR  其中 SS1 中最后一位是 0 或 1
        int idx = resp.IndexOf("$RC", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return OperationResult<byte[]>.Fail(
                $"contact response missing '$RC': {resp}", KernelErrorCode.ProtocolError);

        int p = idx + 3;
        // 跳过可能的空白，找到 0 或 1
        while (p < resp.Length && resp[p] is ' ' or '\r') p++;

        if (p >= resp.Length)
            return OperationResult<byte[]>.Fail(
                "contact response has no value after '$RC'", KernelErrorCode.ProtocolError);

        bool value = resp[p] == '1';
        return OperationResult<byte[]>.Ok(new byte[] { value ? (byte)1 : (byte)0 });
    }

    /// <summary>
    /// 解析读数据区响应，返回原始大端字节数组（每2字节=1个寄存器，高字节在前）。
    /// </summary>
    /// <param name="responseBytes">响应字节。</param>
    /// <param name="wordCount">期望字数。</param>
    internal static OperationResult<byte[]> ParseReadDataResponse(byte[] responseBytes, int wordCount)
    {
        string resp = Decode(responseBytes);

        // 先验 BCC 再解释内容：帧若已被干扰损坏，里面的错误码同样不可信
        OperationResult bccCheck = VerifyBcc(resp);
        if (!bccCheck.Success)
            return OperationResult<byte[]>.Fail(bccCheck.ErrorMessage, bccCheck.ErrorCode);

        OperationResult errCheck = CheckError(resp);
        if (!errCheck.Success)
            return OperationResult<byte[]>.Fail(errCheck.ErrorMessage, errCheck.ErrorCode);

        // 响应体：%SS$RDXXXX...XXXX<BCC>CR
        int idx = resp.IndexOf("$RD", StringComparison.OrdinalIgnoreCase);
        string dataStr = idx >= 0 ? resp[(idx + 3)..] : resp;

        // 提取十六进制字符（忽略 BCC 和 CR）
        var hex = new StringBuilder();
        foreach (char c in dataStr)
        {
            if (c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f'))
                hex.Append(c);
            if (hex.Length >= wordCount * 4 + 4) break; // 多读 4 chars 作为余量（BCC 是 1 字节 = 2 hex chars）
        }

        string h = hex.ToString();
        int need = wordCount * 4;
        // 截取到 need 长度：丢弃末尾 BCC（2 hex chars）及其他尾部字符
        if (h.Length > need) h = h[..need];

        if (h.Length < need)
            return OperationResult<byte[]>.Fail(
                $"RD response data too short (got {h.Length} hex chars, need {need}): {resp}",
                KernelErrorCode.ProtocolError);

        // 每字4个十六进制字符，MEWTOCOL 低字节先出 → SwapBytes 转大端
        byte[] result = new byte[wordCount * 2];
        for (int i = 0; i < wordCount; i++)
        {
            ushort raw = ushort.Parse(h.Substring(i * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ushort big = SwapBytes(raw);   // 转为大端
            result[i * 2]     = (byte)(big >> 8);
            result[i * 2 + 1] = (byte)(big & 0xFF);
        }
        return OperationResult<byte[]>.Ok(result);
    }

    /// <summary>
    /// 解析写响应（WCS/WD）：先验 BCC，再看错误标志。
    /// </summary>
    internal static OperationResult ParseWriteResponse(byte[] responseBytes)
    {
        string resp = Decode(responseBytes);
        OperationResult bcc = VerifyBcc(resp);
        return bcc.Success ? CheckError(resp) : bcc;
    }

    // -------------------------------------------------------------------------
    // BCC 计算（供外部 Ping 使用）
    // -------------------------------------------------------------------------

    /// <summary>
    /// 构建 Ping 探针帧（读 DT00000 一个字）。
    /// </summary>
    internal static byte[] BuildPing(byte station)
        => BuildReadData(
            new MewtocolAddressInfo(station, MewtocolArea.DT, 0, -1, false),
            1);

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    /// <summary>构建完整 MEWTOCOL 帧：% + SS + # + CommandBody + BCC + CR。</summary>
    /// <remarks>
    /// <b>BCC 的计算范围含帧头 '%'。</b>规范原文是「从帧头 % 起、到 BCC 之前为止
    /// 全部 ASCII 字符的水平奇偶校验（XOR）」——'%' 是被算进去的。
    /// <para>
    /// 这里曾经漏掉 '%'，只对 <c>SS + # + 命令体</c> 求 XOR。
    /// 后果是每一帧的校验和都恰好差一个 0x25，PLC 一律以
    /// <c>%01!40</c>（error 40: BCC error）拒收，任何读写都做不成。
    /// 而且这个错误在自测里发现不了——我们自己发、自己算，两边同样漏掉 '%'，
    /// 只有接上真 PLC 才暴露。
    /// </para>
    /// </remarks>
    private static byte[] BuildFrame(byte station, string commandBody)
    {
        // 先拼出待校验的完整前缀（含 '%'），再对它整体求 BCC
        string stationHex = station.ToString("X2");
        string checkedPart = "%" + stationHex + "#" + commandBody;
        string bcc = CalcBcc(checkedPart);

        return Encoding.ASCII.GetBytes(checkedPart + bcc + "\r");
    }

    /// <summary>
    /// 计算 BCC：对传入字符串的全部字符求 XOR，输出两位大写十六进制。
    /// </summary>
    /// <param name="checkedPart">
    /// 待校验部分，<b>必须含帧头 '%'</b>，且不含 BCC 与结尾 CR。
    /// </param>
    internal static string CalcBcc(string checkedPart)
    {
        byte x = 0;
        foreach (char c in checkedPart) x ^= (byte)c;
        return x.ToString("X2");
    }

    /// <summary>
    /// 校验响应帧的 BCC。
    /// </summary>
    /// <remarks>
    /// <b>不校验的后果是静默读到错值。</b>此前解析响应时把 BCC 当尾部垃圾直接丢掉
    /// （代码里写的是"忽略 BCC 和 CR"），于是 RS-485 线上被噪声打翻一位的响应，
    /// 会被当作有效数据接受、标成成功送到界面上。而现场恰恰是长线缆、
    /// 变频器旁边这类环境——校验和存在的全部意义就是拦住这种帧。
    /// <para>
    /// 校验范围与 <see cref="CalcBcc"/> 一致：从帧头 <c>%</c> 起、到 BCC 之前，含 <c>%</c>。
    /// </para>
    /// </remarks>
    /// <param name="resp">已解码的响应文本，可能带结尾 CR。</param>
    /// <returns>校验通过、或帧形态不足以校验时返回成功；确实不匹配时返回协议错误。</returns>
    private static OperationResult VerifyBcc(string resp)
    {
        if (string.IsNullOrEmpty(resp)) return OperationResult.Ok;

        // 去掉结尾的 CR / LF / 空白，BCC 是它们之前的最后两个字符
        string body = resp.TrimEnd('\r', '\n', ' ', '\0');

        // % + SS + 至少一个内容字符 + BCC(2) 才谈得上校验
        if (body.Length < 6 || body[0] != '%') return OperationResult.Ok;

        string bcc = body[^2..];

        // 规范允许用 ** / !! 表示本帧不做校验
        if (bcc is "**" or "!!") return OperationResult.Ok;

        // 末两位不是十六进制：属于"认不出格式"，交给后续内容解析给更准确的报错，
        // 而不是在这里含糊地报一句校验失败
        if (!IsHexPair(bcc)) return OperationResult.Ok;

        string expected = CalcBcc(body[..^2]);
        if (string.Equals(expected, bcc, StringComparison.OrdinalIgnoreCase))
            return OperationResult.Ok;

        return OperationResult.Fail(
            $"MEWTOCOL BCC 校验失败：帧内 {bcc}，按内容应为 {expected}。" +
            "多为串口干扰或接线问题，可降低波特率或缩短线缆后重试。",
            KernelErrorCode.ProtocolError);
    }

    /// <summary>判断字符串是否为两位十六进制数字。</summary>
    /// <param name="s">待判断的字符串。</param>
    /// <returns>长度为 2 且两位都是 0-9/A-F/a-f 时为 true。</returns>
    private static bool IsHexPair(string s) =>
        s.Length == 2 && Uri.IsHexDigit(s[0]) && Uri.IsHexDigit(s[1]);

    /// <summary>
    /// 检查响应是否包含错误标志 '!'。
    /// 错误格式：%SS!NN&lt;BCC&gt;CR，NN 为 2 位错误码。
    /// </summary>
    private static OperationResult CheckError(string resp)
    {
        if (string.IsNullOrEmpty(resp))
            return OperationResult.Fail("empty MEWTOCOL response", KernelErrorCode.ProtocolError);

        int bang = resp.IndexOf('!');
        if (bang >= 0)
        {
            // 取出 '!' 后两位错误码（如 23 = 地址越界）
            string errCode = resp.Length >= bang + 3
                ? resp.Substring(bang + 1, 2)
                : resp[(bang + 1)..];
            string errDesc = MapErrorCode(errCode.Trim());
            return OperationResult.Fail(
                $"MEWTOCOL error {errCode}: {errDesc}", KernelErrorCode.ProtocolError);
        }
        return OperationResult.Ok;
    }

    /// <summary>格式化触点地址（RCS/WCS 使用）。</summary>
    private static string FormatContact(MewtocolAddressInfo addr)
    {
        return addr.Area switch
        {
            MewtocolArea.X => "X" + addr.Index.ToString("D5"),
            MewtocolArea.Y => "Y" + addr.Index.ToString("D5"),
            MewtocolArea.R when addr.BitIndex >= 0
                => "R" + addr.Index.ToString("D3") + addr.BitIndex.ToString("X"),
            MewtocolArea.R
                => "R" + addr.Index.ToString("D5"),
            _ => throw new ArgumentException($"area {addr.Area} is not a contact area")
        };
    }

    /// <summary>
    /// 格式化数据范围（RD/WD 使用）：区号一次 + 起始 5 位 + 结束 5 位。
    /// </summary>
    /// <remarks>
    /// 官方帧是 <c>RDD0040000401</c>（DT400–DT401），不是 <c>RDD00400D00401</c>。
    /// 区号插两次会让结束地址含字母，从站回 <c>!41</c> 格式错误。
    /// </remarks>
    private static string FormatDataRange(MewtocolAddressInfo addr, int wordCount)
    {
        char code  = addr.Area == MewtocolArea.WR ? 'W' : 'D';
        int  start = addr.Index;
        int  end   = start + wordCount - 1;
        return $"{code}{start:D5}{end:D5}";
    }

    /// <summary>低字节/高字节互换（MEWTOCOL 字节序转换）。</summary>
    private static ushort SwapBytes(ushort value)
        => (ushort)((value >> 8) | (value << 8));

    /// <summary>将响应字节解码为 ASCII 字符串。</summary>
    private static string Decode(byte[] bytes)
        => bytes is null or { Length: 0 }
            ? string.Empty
            : Encoding.ASCII.GetString(bytes).Trim('\0');

    /// <summary>映射 MEWTOCOL-COM 错误码（手册 !NN）。</summary>
    private static string MapErrorCode(string code) => code.ToUpperInvariant() switch
    {
        "26" => "Unit number setting error",
        "40" => "BCC error",
        "41" => "Format error",
        "42" => "Command not supported",
        "43" => "Multiframe process error",
        "60" => "Parameter error",
        "61" => "Data error (area, number, size or range)",
        "62" => "Registration overflow",
        "63" => "PLC mode error",
        "64" => "External memory error",
        "65" => "Protection error",
        "66" => "Address error",
        _ => $"Unknown error code {code}"
    };
}
