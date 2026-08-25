// -----------------------------------------------------------------------------
// 文件: ModbusProtocolDriver.cs
// 层级: 插件层 / 协议
// 作用: 三种 Modbus 变体共用的驱动：解析地址、组 PDU、经 Envelope 封装后与传输层交换。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// 三种 Modbus 变体共用的协议驱动。
/// </summary>
/// <remarks>
/// <para>
/// 驱动本身与外层封装、与传输介质均无关：
/// 封装差异由注入的 <see cref="ModbusEnvelope"/> 承担，
/// 介质差异由 <see cref="ITransportClient"/> 承担。
/// 因此同一份驱动可服务 Modbus TCP、RS-485 上的 RTU、
/// 以及经 TCP 转串口透传装置的 RTU。
/// </para>
/// <para>
/// <b>length 单位为字节</b>，与 <c>byte[]</c> 返回值一致，
/// 并由 <see cref="ModbusPdu.ParseReadResponse"/> 裁剪保证「请求 N 字节 → 返回 N 字节」。
/// </para>
/// </remarks>
public sealed class ModbusProtocolDriver : IProtocolDriver {

    private readonly ModbusEnvelope _envelope;

    /// <summary>本路由的默认从站号，来自设备级站号配置。</summary>
    private readonly byte _defaultUnitId;

    /// <param name="metadata">所属插件的协议元信息。</param>
    /// <param name="envelope">外层封装策略（MBAP / CRC16 / ASCII-LRC）。</param>
    /// <param name="defaultUnitId">地址未带站号前缀时使用的从站号。</param>
    public ModbusProtocolDriver(ProtocolMetadata metadata, ModbusEnvelope envelope, byte defaultUnitId) {
        Metadata       = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _envelope      = envelope ?? throw new ArgumentNullException(nameof(envelope));
        _defaultUnitId = defaultUnitId;
    }

    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildReadFrame(string address, int length) {
        // 只组帧不发送，供诊断/预览使用
        OperationResult<ModbusExchange> plan = PlanRead(address, length);
        return plan.Success
            ? OperationResult<byte[]>.Ok(plan.Value.Framed.Frame)
            : OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);
    }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload) {
        // 只组帧不发送，供诊断/预览使用
        OperationResult<ModbusExchange> plan = PlanWrite(address, payload);
        return plan.Success
            ? OperationResult<byte[]>.Ok(plan.Value.Framed.Frame)
            : OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client, string address, int length, CancellationToken cancellationToken) {

        OperationResult<ModbusExchange> plan = PlanRead(address, length);
        // 地址非法或超限时直接失败，不发帧
        if (!plan.Success)
            return OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);

        // 发送完整帧，按 Envelope 的帧探测收齐响应，再剥外层得到 PDU
        OperationResult<byte[]> pdu = await ExchangeAsync(client, plan.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!pdu.Success) return pdu;

        // 校验功能码/异常码，裁剪到请求的字节数
        return ModbusPdu.ParseReadResponse(pdu.Value, plan.Value.Request);
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(
        ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {

        OperationResult<ModbusExchange> plan = PlanWrite(address, payload);
        // 地址非法、只读区或 payload 不合法时直接失败，不发帧
        if (!plan.Success)
            return OperationResult.Fail(plan.ErrorMessage, plan.ErrorCode);

        OperationResult<byte[]> pdu = await ExchangeAsync(client, plan.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!pdu.Success)
            return OperationResult.Fail(pdu.ErrorMessage, pdu.ErrorCode);

        // 写响应只做功能码配对与异常检测，不返回数据
        return ModbusPdu.ParseWriteResponse(pdu.Value, plan.Value.Request);
    }

    // -------------------------------------------------------------------------
    // 内部
    // -------------------------------------------------------------------------

    /// <summary>一次完整往返的计划：已封装的请求 + 响应校验上下文。</summary>
    private readonly record struct ModbusExchange(ModbusFramedRequest Framed, ModbusRequestContext Request);

    /// <summary>发送并接收一帧，剥离外层封装后返回响应 PDU。</summary>
    private async Task<OperationResult<byte[]>> ExchangeAsync(
        ITransportClient client, ModbusExchange exchange, CancellationToken cancellationToken) {

        // 帧边界由 Envelope.FrameProbe 决定（MBAP 长度 / RTU 推定 / ASCII CRLF）
        OperationResult<byte[]> response = await client
            .SendAndReceiveAsync(exchange.Framed.Frame, _envelope.FrameProbe, cancellationToken)
            .ConfigureAwait(false);

        return response.Success
            ? _envelope.Unwrap(response.Value, exchange.Framed)
            : response;
    }

    /// <summary>
    /// 规划一次读：解析地址 → 组 PDU → 按介质封帧。
    /// </summary>
    /// <remarks>
    /// <b>数据区只由地址决定，绝不受读取长度影响。</b>
    /// 曾有过按长度推断功能码的写法，结果读 1 个字节的线圈会被当成寄存器读。
    /// 组帧前先做全部校验，避免把畸形 PDU 发到总线上——
    /// 畸形帧在 RTU 上会让从站丢弃整帧却不响应，表现为超时而非报错。
    /// </remarks>
    private OperationResult<ModbusExchange> PlanRead(string address, int length) {
        OperationResult<ModbusAddressInfo> addr = ModbusAddress.Parse(address, _defaultUnitId);
        // 站号/区号/偏移任一非法则中止，避免发出畸形 PDU
        if (!addr.Success)
            return OperationResult<ModbusExchange>.Fail(addr.ErrorMessage, addr.ErrorCode);

        ModbusAddressInfo a = addr.Value;

        // 数据区仅由地址决定，绝不受读取长度影响
        OperationResult<(byte[] Pdu, ushort Quantity)> pdu =
            ModbusPdu.BuildReadRequest(a.Area, a.RegisterAddress, length);
        // 超限（FC03 最多 125 寄存器 / FC01 最多 2000 位）时拒绝
        if (!pdu.Success)
            return OperationResult<ModbusExchange>.Fail(pdu.ErrorMessage, pdu.ErrorCode);

        // 按 TCP/RTU/ASCII 各自封装；请求上下文供响应配对使用
        ModbusFramedRequest framed = _envelope.Wrap(a.UnitId, pdu.Value.Pdu);
        var request = new ModbusRequestContext(a.UnitId, pdu.Value.Pdu[0], length);

        return OperationResult<ModbusExchange>.Ok(new ModbusExchange(framed, request));
    }

    /// <summary>
    /// 规划一次写：解析地址 → 按数据区选功能码 → 组 PDU → 按介质封帧。
    /// </summary>
    /// <remarks>
    /// 与读路径同构，同样在组帧前完成全部校验。
    /// 写是有副作用的操作，畸形帧的代价比读高得多——
    /// 从站可能把它解释成一次对错误地址的合法写入。
    /// </remarks>
    private OperationResult<ModbusExchange> PlanWrite(string address, byte[] payload) {
        OperationResult<ModbusAddressInfo> addr = ModbusAddress.Parse(address, _defaultUnitId);
        // 写路径同样先解析地址，非法则不组帧
        if (!addr.Success)
            return OperationResult<ModbusExchange>.Fail(addr.ErrorMessage, addr.ErrorCode);

        ModbusAddressInfo a = addr.Value;

        // 按数据区选择 FC05 / FC06 / FC10
        OperationResult<byte[]> pdu = ModbusPdu.BuildWriteRequest(a.Area, a.RegisterAddress, payload);
        // 只读区、空 payload、奇数字节或超 FC10 上限时拒绝
        if (!pdu.Success)
            return OperationResult<ModbusExchange>.Fail(pdu.ErrorMessage, pdu.ErrorCode);

        ModbusFramedRequest framed = _envelope.Wrap(a.UnitId, pdu.Value);
        var request = new ModbusRequestContext(a.UnitId, pdu.Value[0], 0);

        return OperationResult<ModbusExchange>.Ok(new ModbusExchange(framed, request));
    }
}
