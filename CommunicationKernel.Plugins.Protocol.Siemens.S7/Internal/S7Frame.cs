// -----------------------------------------------------------------------------
// 文件: S7Frame.cs
// 层级: Plugins / Siemens.S7 / Internal
// 作用: 西门子 S7 协议公共帧工具类（TPKT、COTP、S7Comm）。
// 说明:
// 1) TPKT（RFC 1006）= 3 字节头 + 1 字节保留 + 2 字节长度。
// 2) COTP CR/DT PDU 用于 ISO 连接建立与数据传输。
// 3) S7Comm 读写 PDU 封装在 COTP DT 数据段中。
// 4) S7-1200 与 S7-200Smart 的差异通过不同 TSAP 参数体现。
// 5) 本类内部私有，禁止向外层暴露任何协议语义。
// -----------------------------------------------------------------------------

using System;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Siemens.S7.Internal;

/// <summary>
/// 支持的 S7 数据区枚举（报文中 Area 字节含义）。
/// </summary>
internal enum S7Area : byte {
    /// <summary>过程映像输入（I）</summary>
    Inputs   = 0x81,
    /// <summary>过程映像输出（Q）</summary>
    Outputs  = 0x82,
    /// <summary>位内存（M）</summary>
    Merkers  = 0x83,
    /// <summary>数据块（DB）</summary>
    DataBlock = 0x84,
    /// <summary>计数器（C）</summary>
    Counters = 0x1C,
    /// <summary>定时器（T）</summary>
    Timers   = 0x1D,
    /// <summary>V区（仅200Smart，映射为DB1）</summary>
    V = 0x84
}

/// <summary>
/// S7 传输大小类型（Transport Size）。
/// </summary>
internal enum S7TransportSize : byte {
    Bit    = 0x01,
    Byte   = 0x02,
    Word   = 0x04,
    DWord  = 0x06
}

/// <summary>
/// 西门子 S7 协议公共帧工具，协议细节仅在此类内可见。
/// </summary>
internal static class S7Frame {
    // -------------------------------------------------------------------------
    // TPKT 常量
    // -------------------------------------------------------------------------
    private const byte TpktVersion  = 0x03;
    private const byte TpktReserved = 0x00;

    // -------------------------------------------------------------------------
    // COTP 连接请求（CR） PDU（固定 18 字节 payload）
    // -------------------------------------------------------------------------
    /// <summary>
    /// 构建 TPKT + COTP ISO 连接请求帧（ISO on TCP Connect Request）。
    /// </summary>
    /// <param name="remoteTsap">目标 TSAP（2字节，区分1200/200Smart）。</param>
    internal static byte[] BuildCotpConnectRequest(ushort remoteTsap) {
        // COTP CR PDU: LI(1) + CR(1) + DstRef(2) + SrcRef(2) + Class(1) + Options(变长)
        // 含三个 Parameter：TPDU-size、Src-TSAP、Dst-TSAP
        byte dstHi = (byte)(remoteTsap >> 8);
        byte dstLo = (byte)(remoteTsap & 0xFF);

        byte[] cotpCr = new byte[] {
            0x11,       // LI: 后续 COTP 长度（17字节）
            0xE0,       // CR: Connection Request
            0x00, 0x00, // DST-REF
            0x00, 0x01, // SRC-REF
            0x00,       // Class/Option
            // Parameter: TPDU-size = 1024
            0xC0, 0x01, 0x0A,
            // Parameter: Src-TSAP（固定0x0100）
            0xC1, 0x02, 0x01, 0x00,
            // Parameter: Dst-TSAP
            0xC2, 0x02, dstHi, dstLo
        };

        // 外套 4 字节 TPKT 头（版本 0x03 + 总长）
        return WrapTpkt(cotpCr);
    }

    // -------------------------------------------------------------------------
    // S7 Setup Communication PDU
    // -------------------------------------------------------------------------
    /// <summary>
    /// 构建 TPKT + COTP DT + S7 Setup Communication 请求。
    /// </summary>
    internal static byte[] BuildSetupCommunication() {
        // S7Comm Setup Communication Request
        byte[] s7 = new byte[] {
            0x32,       // S7 Protocol ID
            0x01,       // Job（请求）
            0x00, 0x00, // Redundancy Identification
            0x04, 0x00, // PDU Ref
            0x00, 0x08, // Parameter Length: 8
            0x00, 0x00, // Data Length: 0
            0xF0,       // Function: Setup Communication
            0x00,       // Reserved
            0x00, 0x01, // Max AMQ Calling: 1
            0x00, 0x01, // Max AMQ Called: 1
            0x01, 0xE0  // PDU Length: 480
        };

        // Setup 走 COTP DT（数据传输），再外套 TPKT
        return WrapTpktWithCotpDt(s7);
    }

    // -------------------------------------------------------------------------
    // S7 Read Var PDU
    // -------------------------------------------------------------------------
    /// <summary>
    /// 构建 S7 Read Var 请求（单 Item）。
    /// </summary>
    /// <param name="area">数据区。</param>
    /// <param name="dbNumber">DB编号（非DB区传0）。</param>
    /// <param name="byteOffset">字节偏移。</param>
    /// <param name="byteCount">读取字节数。</param>
    internal static byte[] BuildReadVar(S7Area area, ushort dbNumber, int byteOffset, int byteCount) {
        int bitAddress = byteOffset * 8; // S7 地址以位为单位

        byte[] param = new byte[] {
            0x04,       // Function: Read Var
            0x01,       // Item Count: 1
            // Item Syntax
            0x12,       // Variable Specification
            0x0A,       // Specification Length: 10
            0x10,       // Syntax ID: S7ANY
            (byte)S7TransportSize.Byte,
            (byte)(byteCount >> 8), (byte)(byteCount & 0xFF),   // Length (word count)
            (byte)(dbNumber >> 8), (byte)(dbNumber & 0xFF),      // DB Number
            (byte)area,
            (byte)(bitAddress >> 16), (byte)(bitAddress >> 8), (byte)(bitAddress & 0xFF) // Byte address (3 bytes)
        };

        // Job 类型 0x01，无 Data 段
        byte[] s7Header = BuildS7Header(jobType: 0x01, paramLen: (ushort)param.Length, dataLen: 0);
        byte[] s7 = Combine(s7Header, param);
        return WrapTpktWithCotpDt(s7);
    }

    // -------------------------------------------------------------------------
    // S7 Write Var PDU
    // -------------------------------------------------------------------------
    /// <summary>
    /// 构建 S7 Write Var 请求（单 Item）。
    /// </summary>
    internal static byte[] BuildWriteVar(S7Area area, ushort dbNumber, int byteOffset, byte[] data) {
        int bitAddress = byteOffset * 8;
        ushort dataLen = (ushort)data.Length;

        byte[] param = new byte[] {
            0x05,       // Function: Write Var
            0x01,       // Item Count: 1
            0x12, 0x0A, 0x10,
            (byte)S7TransportSize.Byte,
            (byte)(dataLen >> 8), (byte)(dataLen & 0xFF),
            (byte)(dbNumber >> 8), (byte)(dbNumber & 0xFF),
            (byte)area,
            (byte)(bitAddress >> 16), (byte)(bitAddress >> 8), (byte)(bitAddress & 0xFF)
        };

        // Data Item：TransportSize=0x04(BYTE), DataLength=bit数, 数据
        ushort bitCount = (ushort)(dataLen * 8);
        byte[] dataItem = new byte[4 + dataLen + (dataLen % 2 == 0 ? 0 : 1)];
        dataItem[0] = 0xFF;                           // ReturnCode: Success
        dataItem[1] = 0x04;                           // TransportSize: BYTE
        dataItem[2] = (byte)(bitCount >> 8);
        dataItem[3] = (byte)(bitCount & 0xFF);
        Array.Copy(data, 0, dataItem, 4, dataLen);

        byte[] s7Header = BuildS7Header(0x01, (ushort)param.Length, (ushort)dataItem.Length);
        byte[] s7 = Combine(s7Header, param, dataItem);
        return WrapTpktWithCotpDt(s7);
    }

    // -------------------------------------------------------------------------
    // 响应解析
    // -------------------------------------------------------------------------
    /// <summary>
    /// 解析 Read Var 响应，返回数据字节；失败时返回错误。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadResponse(byte[] response, int expectedBytes) {
        // 分支1：最小长度校验（TPKT(4) + COTP DT(3) + S7Header(12) + S7ParamReturn(2) + DataItem(4+data) ≥ 25）
        if (response.Length < 25) {
            return OperationResult<byte[]>.Fail("S7 read response too short", KernelErrorCode.ProtocolError);
        }

        // 分支2：S7Comm 错误类与错误码（偏移17和18）
        if (response[17] != 0x00 || response[18] != 0x00) {
            string s7Error = $"S7 error class=0x{response[17]:X2} code=0x{response[18]:X2}";
            return OperationResult<byte[]>.Fail(s7Error, KernelErrorCode.ProtocolError);
        }

        // 分支3：数据返回码（偏移21，应为0xFF）
        int dataOffset = 21;
        if (dataOffset >= response.Length || response[dataOffset] != 0xFF) {
            return OperationResult<byte[]>.Fail("S7 data item return code invalid", KernelErrorCode.ProtocolError);
        }

        // 分支4：实际数据长度（偏移23-24，单位：位）
        if (dataOffset + 3 >= response.Length) {
            return OperationResult<byte[]>.Fail("S7 data length field missing", KernelErrorCode.ProtocolError);
        }
        int bitLen = (response[dataOffset + 2] << 8) | response[dataOffset + 3];
        int byteLen = (bitLen + 7) / 8;

        // 分支5：实际数据是否足够
        int dataStart = dataOffset + 4;
        if (dataStart + byteLen > response.Length) {
            return OperationResult<byte[]>.Fail("S7 response data truncated", KernelErrorCode.ProtocolError);
        }

        // 按请求字节数裁剪，保证「请求 N 字节 → 返回 N 字节」
        byte[] result = new byte[Math.Min(byteLen, expectedBytes)];
        Array.Copy(response, dataStart, result, 0, result.Length);
        return OperationResult<byte[]>.Ok(result);
    }

    /// <summary>
    /// 解析 Write Var 响应，校验写入成功标志。
    /// </summary>
    internal static OperationResult ParseWriteResponse(byte[] response) {
        // TPKT + COTP DT + S7 头至少 22 字节才够读错误类
        if (response.Length < 22) {
            return OperationResult.Fail("S7 write response too short", KernelErrorCode.ProtocolError);
        }

        // S7Comm 错误类/错误码必须为 0
        if (response[17] != 0x00 || response[18] != 0x00) {
            return OperationResult.Fail($"S7 write error class=0x{response[17]:X2} code=0x{response[18]:X2}", KernelErrorCode.ProtocolError);
        }

        // 写响应数据返回码：偏移21，应为0xFF
        if (response.Length > 21 && response[21] != 0xFF) {
            return OperationResult.Fail($"S7 write item return code=0x{response[21]:X2}", KernelErrorCode.ProtocolError);
        }

        return OperationResult.Ok;
    }

    // -------------------------------------------------------------------------
    // 地址解析
    // -------------------------------------------------------------------------
    /// <summary>
    /// 解析地址字符串，返回 (area, dbNumber, byteOffset)。
    /// 支持格式：
    ///   DB10.DBW0 / DB10.DBB0 / DB10.DBD0（S7-1200 DB区）
    ///   M0 / MB0 / MW0 / MD0（位内存区）
    ///   I0 / IB0（输入区）
    ///   Q0 / QB0（输出区）
    ///   V0 / VW0 / VD0（S7-200Smart V区，映射为DB1）
    ///   T0（定时器）/ C0（计数器）
    /// </summary>
    internal static OperationResult<(S7Area area, ushort dbNumber, int byteOffset)> ParseAddress(string address) {
        if (string.IsNullOrWhiteSpace(address)) {
            return OperationResult<(S7Area, ushort, int)>.Fail("address is empty", KernelErrorCode.InvalidArgument);
        }

        string addr = address.Trim().ToUpperInvariant();

        try {
            // 分支：DB区（DB10.DBB0 / DB10.DBW0 / DB10.DBD0）
            if (addr.StartsWith("DB", StringComparison.Ordinal)) {
                int dotIdx = addr.IndexOf('.');
                if (dotIdx < 0) {
                    return OperationResult<(S7Area, ushort, int)>.Fail($"invalid DB address: {address}", KernelErrorCode.InvalidArgument);
                }
                ushort dbNum = ushort.Parse(addr.Substring(2, dotIdx - 2));
                string sub = addr.Substring(dotIdx + 1);
                // 分支：剥离已知前缀（DBB/DBW/DBD=3字符），或直接以裸数字作为字节偏移
                string offsetStr = sub.StartsWith("DBB", StringComparison.Ordinal)
                                || sub.StartsWith("DBW", StringComparison.Ordinal)
                                || sub.StartsWith("DBD", StringComparison.Ordinal)
                    ? sub.Substring(3)
                    : sub;   // 无前缀：直接当数字解析（如 "DB10.0"）
                int offset = ParseOffset(offsetStr);
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.DataBlock, dbNum, offset));
            }

            // 分支：V区（S7-200Smart）→ 映射为 DB1
            if (addr.StartsWith("VD", StringComparison.Ordinal)) {
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.V, 1, ParseOffset(addr.Substring(2))));
            }
            if (addr.StartsWith("VW", StringComparison.Ordinal)) {
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.V, 1, ParseOffset(addr.Substring(2))));
            }
            if (addr.StartsWith("VB", StringComparison.Ordinal) || addr[0] == 'V') {
                string numPart = addr.StartsWith("VB", StringComparison.Ordinal) ? addr.Substring(2) : addr.Substring(1);
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.V, 1, ParseOffset(numPart)));
            }

            // 分支：M区
            if (addr.StartsWith("MD", StringComparison.Ordinal)) {
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.Merkers, 0, ParseOffset(addr.Substring(2))));
            }
            if (addr.StartsWith("MW", StringComparison.Ordinal)) {
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.Merkers, 0, ParseOffset(addr.Substring(2))));
            }
            if (addr.StartsWith("MB", StringComparison.Ordinal) || addr[0] == 'M') {
                string numPart = addr.StartsWith("MB", StringComparison.Ordinal) ? addr.Substring(2) : addr.Substring(1);
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.Merkers, 0, ParseOffset(numPart)));
            }

            // 分支：I区（输入）
            if (addr.StartsWith("IB", StringComparison.Ordinal) || addr[0] == 'I') {
                string numPart = addr.StartsWith("IB", StringComparison.Ordinal) ? addr.Substring(2) : addr.Substring(1);
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.Inputs, 0, ParseOffset(numPart)));
            }

            // 分支：Q区（输出）
            if (addr.StartsWith("QB", StringComparison.Ordinal) || addr[0] == 'Q') {
                string numPart = addr.StartsWith("QB", StringComparison.Ordinal) ? addr.Substring(2) : addr.Substring(1);
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.Outputs, 0, ParseOffset(numPart)));
            }

            // 分支：T区（定时器）
            if (addr[0] == 'T') {
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.Timers, 0, ParseOffset(addr.Substring(1))));
            }

            // 分支：C区（计数器）
            if (addr[0] == 'C') {
                return OperationResult<(S7Area, ushort, int)>.Ok((S7Area.Counters, 0, ParseOffset(addr.Substring(1))));
            }

            return OperationResult<(S7Area, ushort, int)>.Fail($"unknown S7 address format: {address}", KernelErrorCode.InvalidArgument);
        } catch {
            return OperationResult<(S7Area, ushort, int)>.Fail($"address parse failed: {address}", KernelErrorCode.InvalidArgument);
        }
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------
    private static int ParseOffset(string s) => int.Parse(s);

    /// <summary>组 S7Comm 头（10 字节）。</summary>
    /// <param name="jobType">报文类型：0x01 Job / 0x03 Ack-Data。</param>
    /// <param name="paramLen">参数区长度。</param>
    /// <param name="dataLen">数据区长度。</param>
    /// <remarks>PDU 引用号固定填 0：本驱动是请求-响应同步模型，不需要靠它配对。</remarks>
    private static byte[] BuildS7Header(byte jobType, ushort paramLen, ushort dataLen) {
        return new byte[] {
            0x32,               // Protocol ID
            jobType,            // Message Type (0x01=Job, 0x03=Response)
            0x00, 0x00,         // Redundancy
            0x00, 0x01,         // PDU Ref
            (byte)(paramLen >> 8), (byte)(paramLen & 0xFF),
            (byte)(dataLen  >> 8), (byte)(dataLen  & 0xFF)
        };
    }

    /// <summary>用 TPKT 头（RFC 1006，4 字节）包裹载荷。</summary>
    /// <remarks>长度字段是<b>含头</b>的总长，且为大端——这是 TCP 上分帧的唯一依据。</remarks>
    private static byte[] WrapTpkt(byte[] payload) {
        // TPKT：[0]=0x03 [1]=0x00 [2-3]=总长（含头本身）
        ushort totalLen = (ushort)(4 + payload.Length);
        byte[] frame = new byte[totalLen];
        frame[0] = TpktVersion;
        frame[1] = TpktReserved;
        frame[2] = (byte)(totalLen >> 8);
        frame[3] = (byte)(totalLen & 0xFF);
        Array.Copy(payload, 0, frame, 4, payload.Length);
        return frame;
    }

    /// <summary>给 S7 载荷加上 COTP 数据传输头，再套 TPKT。</summary>
    /// <remarks>握手完成后的所有业务报文都走这条路径：TPKT → COTP DT → S7Comm。</remarks>
    private static byte[] WrapTpktWithCotpDt(byte[] s7Payload) {
        // COTP DT Data Header: LI=2, PDU-Type=0xF0, Last=0x80
        byte[] cotpDt = new byte[] { 0x02, 0xF0, 0x80 };
        byte[] combined = Combine(cotpDt, s7Payload);
        return WrapTpkt(combined);
    }

    /// <summary>按顺序拼接多个字节段。</summary>
    /// <remarks>一次性算出总长再拷贝，避免逐段 Concat 产生多次中间分配。</remarks>
    private static byte[] Combine(params byte[][] arrays) {
        int totalLen = 0;
        // 先累加总长再一次分配，避免多次扩容
        foreach (byte[] a in arrays) totalLen += a.Length;
        byte[] result = new byte[totalLen];
        int offset = 0;
        // 按顺序拼接 TPKT/COTP/S7 各段
        foreach (byte[] a in arrays) {
            Array.Copy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }
}
