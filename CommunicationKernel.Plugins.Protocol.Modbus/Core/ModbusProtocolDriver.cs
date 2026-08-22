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
        OperationResult<ModbusExchange> plan = PlanRead(address, length);
        return plan.Success
            ? OperationResult<byte[]>.Ok(plan.Value.Framed.Frame)
            : OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);
    }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload) {
        OperationResult<ModbusExchange> plan = PlanWrite(address, payload);
        return plan.Success
            ? OperationResult<byte[]>.Ok(plan.Value.Framed.Frame)
            : OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client, string address, int length, CancellationToken cancellationToken) {

        OperationResult<ModbusExchange> plan = PlanRead(address, length);
        if (!plan.Success)
            return OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);

        OperationResult<byte[]> pdu = await ExchangeAsync(client, plan.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!pdu.Success) return pdu;

        return ModbusPdu.ParseReadResponse(pdu.Value, plan.Value.Request);
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(
        ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {

        OperationResult<ModbusExchange> plan = PlanWrite(address, payload);
        if (!plan.Success)
            return OperationResult.Fail(plan.ErrorMessage, plan.ErrorCode);

        OperationResult<byte[]> pdu = await ExchangeAsync(client, plan.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!pdu.Success)
            return OperationResult.Fail(pdu.ErrorMessage, pdu.ErrorCode);

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

        OperationResult<byte[]> response = await client
            .SendAndReceiveAsync(exchange.Framed.Frame, _envelope.FrameProbe, cancellationToken)
            .ConfigureAwait(false);

        return response.Success
            ? _envelope.Unwrap(response.Value, exchange.Framed)
            : response;
    }

    private OperationResult<ModbusExchange> PlanRead(string address, int length) {
        OperationResult<ModbusAddressInfo> addr = ModbusAddress.Parse(address, _defaultUnitId);
        if (!addr.Success)
            return OperationResult<ModbusExchange>.Fail(addr.ErrorMessage, addr.ErrorCode);

        ModbusAddressInfo a = addr.Value;

        // 数据区仅由地址决定，绝不受读取长度影响
        OperationResult<(byte[] Pdu, ushort Quantity)> pdu =
            ModbusPdu.BuildReadRequest(a.Area, a.RegisterAddress, length);
        if (!pdu.Success)
            return OperationResult<ModbusExchange>.Fail(pdu.ErrorMessage, pdu.ErrorCode);

        ModbusFramedRequest framed = _envelope.Wrap(a.UnitId, pdu.Value.Pdu);
        var request = new ModbusRequestContext(a.UnitId, pdu.Value.Pdu[0], length);

        return OperationResult<ModbusExchange>.Ok(new ModbusExchange(framed, request));
    }

    private OperationResult<ModbusExchange> PlanWrite(string address, byte[] payload) {
        OperationResult<ModbusAddressInfo> addr = ModbusAddress.Parse(address, _defaultUnitId);
        if (!addr.Success)
            return OperationResult<ModbusExchange>.Fail(addr.ErrorMessage, addr.ErrorCode);

        ModbusAddressInfo a = addr.Value;

        OperationResult<byte[]> pdu = ModbusPdu.BuildWriteRequest(a.Area, a.RegisterAddress, payload);
        if (!pdu.Success)
            return OperationResult<ModbusExchange>.Fail(pdu.ErrorMessage, pdu.ErrorCode);

        ModbusFramedRequest framed = _envelope.Wrap(a.UnitId, pdu.Value);
        var request = new ModbusRequestContext(a.UnitId, pdu.Value[0], 0);

        return OperationResult<ModbusExchange>.Ok(new ModbusExchange(framed, request));
    }
}
