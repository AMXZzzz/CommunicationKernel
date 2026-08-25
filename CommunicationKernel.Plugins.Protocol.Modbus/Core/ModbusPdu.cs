// -----------------------------------------------------------------------------
// 文件: ModbusPdu.cs
// 层级: 插件层 / 协议
// 作用: 与介质无关的 Modbus PDU 构建、写校验与响应解析。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// 一次 Modbus 请求的上下文，用于校验响应是否与请求配对。
/// </summary>
/// <param name="UnitId">请求的从站地址。</param>
/// <param name="FunctionCode">请求的功能码。</param>
/// <param name="ExpectedByteCount">期望的数据字节数；写请求为 0。</param>
public readonly record struct ModbusRequestContext(
    byte UnitId,
    byte FunctionCode,
    int ExpectedByteCount);

/// <summary>
/// 与传输介质无关的 Modbus PDU 构建与解析。
/// </summary>
/// <remarks>
/// PDU = 功能码 + 数据，不含任何外层封装：
/// TCP 在其前面加 MBAP 头，RTU 加从站号与 CRC16，ASCII 转十六进制文本并加 LRC。
/// 三种介质共用本文件的全部语义，保证协议行为一致。
/// <para>
/// <b>length 的单位统一为字节</b>，与 <c>byte[]</c> 返回值一致。
/// 历史实现中 TCP/MEWTOCOL/S7 按字节而 RTU/ASCII 按寄存器数，
/// 同一变量换协议后读回长度直接翻倍，且不报错。
/// </para>
/// </remarks>
public static class ModbusPdu {

    // =========================================================================
    // 读请求
    // =========================================================================

    /// <summary>
    /// 构建读请求 PDU。
    /// </summary>
    /// <param name="area">数据区，决定功能码。</param>
    /// <param name="startAddress">区内 0 基起始地址。</param>
    /// <param name="byteCount">期望读取的<b>字节</b>数。</param>
    /// <returns>PDU 字节（功能码 + 起始地址 + 数量）与配套的请求上下文。</returns>
    public static OperationResult<(byte[] Pdu, ushort Quantity)> BuildReadRequest(
        ModbusDataArea area, ushort startAddress, int byteCount) {

        // 0 或负数无法构成合法 Quantity 字段
        if (byteCount <= 0)
            return FailTuple($"读取长度必须大于 0，实际 {byteCount}");

        // 位区按位计数，寄存器区按 16 位寄存器计数
        int quantity;
        if (area.IsBitArea()) {
            // 1 字节承载 8 个位
            quantity = byteCount * 8;
            if (quantity > ModbusLimits.MaxReadBits)
                return FailTuple(
                    $"{area.DisplayName()} 单次最多读取 {ModbusLimits.MaxReadBits} 位，" +
                    $"请求 {byteCount} 字节合 {quantity} 位");
        } else {
            // 向上取整到整寄存器（奇数字节多读一个寄存器再裁剪）
            quantity = (byteCount + 1) / 2;
            if (quantity > ModbusLimits.MaxReadRegisters)
                return FailTuple(
                    $"{area.DisplayName()} 单次最多读取 {ModbusLimits.MaxReadRegisters} 个寄存器，" +
                    $"请求 {byteCount} 字节合 {quantity} 个");
        }

        // 起始地址 + 数量不能越过 65535，否则 PDU 的 16 位地址字段溢出
        if (startAddress + quantity - 1 > ushort.MaxValue)
            return FailTuple($"起始地址 {startAddress} 加数量 {quantity} 超出 65535 地址空间");

        // PDU：[FC][起始地址 2B 大端][数量 2B 大端]
        byte[] pdu = new byte[5];
        pdu[0] = area.ReadFunctionCode();
        WriteUInt16(pdu, 1, startAddress);
        WriteUInt16(pdu, 3, (ushort)quantity);

        return OperationResult<(byte[], ushort)>.Ok((pdu, (ushort)quantity));
    }

    // =========================================================================
    // 写请求
    // =========================================================================

    /// <summary>
    /// 校验写入 payload 是否合法。三个 Modbus 插件共用同一套校验。
    /// </summary>
    /// <remarks>
    /// 历史实现中 TCP 有非空校验而 RTU/ASCII 没有，空 payload 会构造出
    /// "写 0 个寄存器" 的畸形 FC10 帧并真实发送到设备。
    /// </remarks>
    public static OperationResult ValidateRegisterPayload(byte[]? payload) {
        // 空 payload 会构出“写 0 个寄存器”的畸形 FC10
        if (payload is null || payload.Length == 0)
            return OperationResult.Fail("写入数据为空", KernelErrorCode.InvalidArgument);

        // 寄存器是 16 位，奇数字节无法对齐到寄存器边界。
        // 绝不静默补零：末尾补 0x00 会把低字节挪到高字节位，
        // 写 0x05 变成写 0x0500（放大 256 倍）。
        if (payload.Length % 2 != 0)
            return OperationResult.Fail(
                $"寄存器写入长度必须为偶数字节，实际 {payload.Length} 字节", KernelErrorCode.InvalidArgument);

        // FC10 的字节数字段只有 1 字节，超 246 会静默截断
        if (payload.Length > ModbusLimits.MaxWriteBytes)
            return OperationResult.Fail(
                $"单次最多写入 {ModbusLimits.MaxWriteBytes} 字节（{ModbusLimits.MaxWriteRegisters} 个寄存器），" +
                $"实际 {payload.Length} 字节", KernelErrorCode.InvalidArgument);

        return OperationResult.Ok;
    }

    /// <summary>构建 FC05 写单线圈 PDU。ON = 0xFF00，OFF = 0x0000。</summary>
    public static byte[] BuildWriteSingleCoil(ushort address, bool value) {
        byte[] pdu = new byte[5];
        pdu[0] = ModbusFunctionCode.WriteSingleCoil;
        WriteUInt16(pdu, 1, address);
        // 规范规定 ON=0xFF00、OFF=0x0000，其他值设备行为未定义
        pdu[3] = value ? (byte)0xFF : (byte)0x00;
        pdu[4] = 0x00;
        return pdu;
    }

    /// <summary>构建 FC06 写单寄存器 PDU。</summary>
    public static byte[] BuildWriteSingleRegister(ushort address, ushort value) {
        byte[] pdu = new byte[5];
        pdu[0] = ModbusFunctionCode.WriteSingleRegister;
        WriteUInt16(pdu, 1, address);
        WriteUInt16(pdu, 3, value);
        return pdu;
    }

    /// <summary>
    /// 构建 FC10 写多寄存器 PDU。
    /// 调用前必须已通过 <see cref="ValidateRegisterPayload"/>。
    /// </summary>
    public static byte[] BuildWriteMultipleRegisters(ushort address, byte[] payload) {
        int registerCount = payload.Length / 2;

        // [FC][地址 2B][数量 2B][字节数 1B][数据...]
        byte[] pdu = new byte[6 + payload.Length];
        pdu[0] = ModbusFunctionCode.WriteMultipleRegisters;
        WriteUInt16(pdu, 1, address);
        WriteUInt16(pdu, 3, (ushort)registerCount);
        pdu[5] = (byte)payload.Length;   // 已由 ValidateRegisterPayload 保证 ≤ 246，不会截断
        Buffer.BlockCopy(payload, 0, pdu, 6, payload.Length);
        return pdu;
    }

    /// <summary>
    /// 依据数据区与 payload 选择写功能码并构建 PDU。
    /// </summary>
    public static OperationResult<byte[]> BuildWriteRequest(
        ModbusDataArea area, ushort address, byte[]? payload) {

        // 离散输入 / 输入寄存器是只读区，设备会回 Illegal Function
        if (!area.IsWritable())
            return OperationResult<byte[]>.Fail(
                $"{area.DisplayName()} 为只读数据区，不支持写入", KernelErrorCode.InvalidArgument);

        if (payload is null || payload.Length == 0)
            return OperationResult<byte[]>.Fail("写入数据为空", KernelErrorCode.InvalidArgument);

        // 位区：单个字节的最低位即线圈状态
        if (area.IsBitArea())
            return OperationResult<byte[]>.Ok(BuildWriteSingleCoil(address, payload[0] != 0));

        OperationResult validation = ValidateRegisterPayload(payload);
        if (!validation.Success)
            return OperationResult<byte[]>.Fail(validation.ErrorMessage, validation.ErrorCode);

        // 恰好一个寄存器用 FC06，多寄存器用 FC10
        return OperationResult<byte[]>.Ok(
            payload.Length == 2
                ? BuildWriteSingleRegister(address, (ushort)((payload[0] << 8) | payload[1]))
                : BuildWriteMultipleRegisters(address, payload));
    }

    // =========================================================================
    // 响应解析
    // =========================================================================

    /// <summary>
    /// 校验响应 PDU 与请求是否配对，并检测异常响应。
    /// </summary>
    /// <param name="pdu">已剥离外层封装的响应 PDU。</param>
    /// <param name="request">发出该请求时的上下文。</param>
    /// <remarks>
    /// 异常帧检测必须<b>先于</b>正常响应的最小长度检查：
    /// 异常 PDU 只有 2 字节（FC|0x80 + 异常码），若先按正常响应的最小长度校验，
    /// 所有异常都会被报成 "response too short"，真正的
    /// "Illegal Data Address" 永远看不到——对调试工具而言这恰好抹掉了最关键的诊断信息。
    /// </remarks>
    public static OperationResult ValidateResponsePdu(byte[]? pdu, ModbusRequestContext request) {
        // 异常 PDU 也至少 2 字节：功能码 + 异常码
        if (pdu is null || pdu.Length < 2)
            return OperationResult.Fail("Modbus 响应过短，不足以构成 PDU", KernelErrorCode.ProtocolError);

        byte responseFc = pdu[0];

        // ── 分支 1：异常响应（最高位置 1） ──
        if ((responseFc & ModbusFunctionCode.ExceptionMask) != 0) {
            byte baseFc = (byte)(responseFc & ~ModbusFunctionCode.ExceptionMask);
            byte exCode = pdu[1];

            // 异常功能码去掉 0x80 后必须等于请求功能码，否则是错位帧
            if (baseFc != request.FunctionCode)
                return OperationResult.Fail(
                    $"响应功能码不匹配：请求 0x{request.FunctionCode:X2}，异常响应指向 0x{baseFc:X2}",
                    KernelErrorCode.ProtocolError);

            return OperationResult.Fail(
                $"Modbus 异常 0x{exCode:X2}：{MapExceptionCode(exCode)}", KernelErrorCode.ProtocolError);
        }

        // ── 分支 2：功能码必须与请求一致 ──
        if (responseFc != request.FunctionCode)
            return OperationResult.Fail(
                $"响应功能码不匹配：请求 0x{request.FunctionCode:X2}，响应 0x{responseFc:X2}",
                KernelErrorCode.ProtocolError);

        return OperationResult.Ok;
    }

    /// <summary>
    /// 解析读响应 PDU，返回数据字节。
    /// </summary>
    /// <param name="pdu">已剥离外层封装的响应 PDU。</param>
    /// <param name="request">请求上下文，用于配对校验与长度裁剪。</param>
    public static OperationResult<byte[]> ParseReadResponse(byte[]? pdu, ModbusRequestContext request) {
        OperationResult validation = ValidateResponsePdu(pdu, request);
        if (!validation.Success)
            return OperationResult<byte[]>.Fail(validation.ErrorMessage, validation.ErrorCode);

        // 读响应 PDU：[FC][ByteCount][Data...]
        if (pdu!.Length < 2)
            return OperationResult<byte[]>.Fail("读响应缺少字节数字段", KernelErrorCode.ProtocolError);

        int byteCount = pdu[1];
        if (pdu.Length < 2 + byteCount)
            return OperationResult<byte[]>.Fail(
                $"读响应数据被截断：声明 {byteCount} 字节，实际 {pdu.Length - 2} 字节",
                KernelErrorCode.ProtocolError);

        byte[] data = new byte[byteCount];
        Buffer.BlockCopy(pdu, 2, data, 0, byteCount);

        // 请求方按字节请求，此处裁剪到其期望长度，保证「请求 N 字节 → 返回 N 字节」
        if (request.ExpectedByteCount > 0 && data.Length > request.ExpectedByteCount) {
            byte[] trimmed = new byte[request.ExpectedByteCount];
            Buffer.BlockCopy(data, 0, trimmed, 0, request.ExpectedByteCount);
            data = trimmed;
        }

        return OperationResult<byte[]>.Ok(data);
    }

    /// <summary>解析写响应 PDU，仅做配对与异常校验。</summary>
    public static OperationResult ParseWriteResponse(byte[]? pdu, ModbusRequestContext request)
        => ValidateResponsePdu(pdu, request);

    /// <summary>将 Modbus 异常码映射为可读描述。</summary>
    public static string MapExceptionCode(byte code) => code switch {
        0x01 => "非法功能码 (Illegal Function)",
        0x02 => "非法数据地址 (Illegal Data Address)",
        0x03 => "非法数据值 (Illegal Data Value)",
        0x04 => "从站设备故障 (Slave Device Failure)",
        0x05 => "已确认，处理中 (Acknowledge)",
        0x06 => "从站设备忙 (Slave Device Busy)",
        0x08 => "存储奇偶校验错 (Memory Parity Error)",
        0x0A => "网关路径不可用 (Gateway Path Unavailable)",
        0x0B => "网关目标设备无响应 (Gateway Target Device Failed to Respond)",
        _    => $"未知异常码 0x{code:X2}"
    };

    /// <summary>按大端序写入 16 位值。</summary>
    public static void WriteUInt16(byte[] buffer, int offset, ushort value) {
        buffer[offset]     = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    /// <summary>构造「帧 + 期望响应长度」失败结果的简写。</summary>
    private static OperationResult<(byte[], ushort)> FailTuple(string message)
        => OperationResult<(byte[], ushort)>.Fail(message, KernelErrorCode.InvalidArgument);
}
