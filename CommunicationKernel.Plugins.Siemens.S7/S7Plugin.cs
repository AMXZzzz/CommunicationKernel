// -----------------------------------------------------------------------------
// 文件: S7Plugin.cs
// 层级: Plugins / Siemens.S7
// 作用: 西门子 S7 合并协议插件。
// 说明:
// 1) 提供 S7-1200 和 S7-200Smart 两个独立 ProtocolDriverFactory。
// 2) 两者共享 S7Frame 内部帧工具，差异在于 TSAP 连接参数。
//    - S7-1200：Remote TSAP = 0x0300（Rack0, Slot0）
//    - S7-200Smart：Remote TSAP = 0x0200（V区映射为DB1）
// 3) 协议解析全部在本 DLL 内部，外层禁止感知任何协议语义。
// 4) 连接握手（COTP CR + S7 Setup Communication）在首次 ReadAsync/WriteAsync
//    时自动完成；宿主层不感知握手过程。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugins.Siemens.S7.Internal;

namespace CommunicationKernel.Plugins.Siemens.S7;

// =============================================================================
// S7-1200 Factory
// =============================================================================

/// <summary>
/// S7-1200 协议驱动工厂（TSAP = 0x0300, Rack0 / Slot0）。
/// </summary>
public sealed class SiemensS7_1200ProtocolDriverFactory : IProtocolDriverFactory {
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId       = "siemens-s7-1200",
        DisplayName      = "Siemens S7-1200 (ISO on TCP)",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver() =>
        new SiemensS7ProtocolDriver(Metadata, remoteTsap: 0x0300);
}

// =============================================================================
// S7-200Smart Factory
// =============================================================================

/// <summary>
/// S7-200Smart 协议驱动工厂（TSAP = 0x0200, V区映射为DB1）。
/// </summary>
public sealed class SiemensS7_200SmartProtocolDriverFactory : IProtocolDriverFactory {
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId       = "siemens-s7-200smart",
        DisplayName      = "Siemens S7-200Smart (ISO on TCP)",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver() =>
        new SiemensS7ProtocolDriver(Metadata, remoteTsap: 0x0200);
}

// =============================================================================
// 共用驱动实现（1200 与 200Smart 差异仅在 TSAP）
// =============================================================================

/// <summary>
/// 西门子 S7 协议驱动，支持 S7-1200 / S7-200Smart 两种机型。
/// </summary>
internal sealed class SiemensS7ProtocolDriver : IProtocolDriver {

    // -------------------------------------------------------------------------
    // 字段
    // -------------------------------------------------------------------------
    private readonly ushort _remoteTsap;

    /// <summary>标识当前连接是否已完成 COTP 握手与 S7 Setup Communication。</summary>
    private int _handshakeDone; // 0=未完成, 1=已完成（Interlocked）

    // -------------------------------------------------------------------------
    // 构造
    // -------------------------------------------------------------------------
    internal SiemensS7ProtocolDriver(ProtocolMetadata metadata, ushort remoteTsap) {
        Metadata     = metadata;
        _remoteTsap  = remoteTsap;
    }

    // -------------------------------------------------------------------------
    // IProtocolDriver
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; }

    /// <inheritdoc />
    public Task<OperationResult<byte[]>> BuildReadFrameAsync(
        string address, int length, CancellationToken cancellationToken) {

        // 解析地址 → 构建 S7 Read Var 请求帧
        OperationResult<(S7Area area, ushort dbNumber, int byteOffset)> addrResult =
            S7Frame.ParseAddress(address);

        if (!addrResult.Success) {
            return Task.FromResult(OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode));
        }

        (S7Area area, ushort dbNumber, int byteOffset) = addrResult.Value;
        byte[] frame = S7Frame.BuildReadVar(area, dbNumber, byteOffset, length);
        return Task.FromResult(OperationResult<byte[]>.Ok(frame));
    }

    /// <inheritdoc />
    public Task<OperationResult<byte[]>> BuildWriteFrameAsync(
        string address, byte[] payload, CancellationToken cancellationToken) {

        OperationResult<(S7Area area, ushort dbNumber, int byteOffset)> addrResult =
            S7Frame.ParseAddress(address);

        if (!addrResult.Success) {
            return Task.FromResult(OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode));
        }

        (S7Area area, ushort dbNumber, int byteOffset) = addrResult.Value;
        byte[] frame = S7Frame.BuildWriteVar(area, dbNumber, byteOffset, payload);
        return Task.FromResult(OperationResult<byte[]>.Ok(frame));
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client,
        string address,
        int length,
        CancellationToken cancellationToken) {

        // 分支1：确保握手完成
        OperationResult handshake = await EnsureHandshakeAsync(client, cancellationToken).ConfigureAwait(false);
        if (!handshake.Success) {
            return OperationResult<byte[]>.Fail(handshake.ErrorMessage, handshake.ErrorCode);
        }

        // 分支2：构建请求帧
        OperationResult<byte[]> buildResult = await BuildReadFrameAsync(address, length, cancellationToken).ConfigureAwait(false);
        if (!buildResult.Success) {
            return buildResult;
        }

        // 分支3：发送并接收响应
        OperationResult<byte[]> response = await client.SendAndReceiveAsync(buildResult.Value, cancellationToken).ConfigureAwait(false);
        if (!response.Success) {
            return response;
        }

        // 分支4：解析响应
        return S7Frame.ParseReadResponse(response.Value, length);
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(
        ITransportClient client,
        string address,
        byte[] payload,
        CancellationToken cancellationToken) {

        // 分支1：确保握手完成
        OperationResult handshake = await EnsureHandshakeAsync(client, cancellationToken).ConfigureAwait(false);
        if (!handshake.Success) return handshake;

        // 分支2：构建写入帧
        OperationResult<byte[]> buildResult = await BuildWriteFrameAsync(address, payload, cancellationToken).ConfigureAwait(false);
        if (!buildResult.Success) {
            return OperationResult.Fail(buildResult.ErrorMessage, buildResult.ErrorCode);
        }

        // 分支3：发送并接收响应
        OperationResult<byte[]> response = await client.SendAndReceiveAsync(buildResult.Value, cancellationToken).ConfigureAwait(false);
        if (!response.Success) {
            return OperationResult.Fail(response.ErrorMessage, response.ErrorCode);
        }

        // 分支4：解析写响应
        return S7Frame.ParseWriteResponse(response.Value);
    }

    // -------------------------------------------------------------------------
    // 握手（COTP CR → S7 Setup Communication）
    // -------------------------------------------------------------------------

    /// <summary>
    /// 首次调用时执行 ISO on TCP 连接握手（COTP CR + S7 Setup Communication）。
    /// 后续调用直接跳过（Interlocked 防止并发重复握手）。
    /// </summary>
    private async Task<OperationResult> EnsureHandshakeAsync(
        ITransportClient client,
        CancellationToken cancellationToken) {

        // 使用 CAS 保证只执行一次握手
        if (Interlocked.CompareExchange(ref _handshakeDone, 1, 0) != 0) {
            return OperationResult.Ok;
        }

        // Step-1: 发送 COTP Connection Request（ISO on TCP）
        byte[] cotpCr = S7Frame.BuildCotpConnectRequest(_remoteTsap);
        OperationResult<byte[]> cotpResp = await client.SendAndReceiveAsync(cotpCr, cancellationToken).ConfigureAwait(false);
        if (!cotpResp.Success) {
            // 握手失败，重置标志允许下次重试
            Interlocked.Exchange(ref _handshakeDone, 0);
            return OperationResult.Fail($"COTP connect failed: {cotpResp.ErrorMessage}", KernelErrorCode.TransportIoError);
        }

        // Step-1 校验：COTP CC（Connection Confirm）PDU-Type = 0xD0
        if (cotpResp.Value.Length < 6 || cotpResp.Value[5] != 0xD0) {
            Interlocked.Exchange(ref _handshakeDone, 0);
            return OperationResult.Fail("COTP connect response is not CC (0xD0)", KernelErrorCode.ProtocolError);
        }

        // Step-2: 发送 S7 Setup Communication
        byte[] setupReq = S7Frame.BuildSetupCommunication();
        OperationResult<byte[]> setupResp = await client.SendAndReceiveAsync(setupReq, cancellationToken).ConfigureAwait(false);
        if (!setupResp.Success) {
            Interlocked.Exchange(ref _handshakeDone, 0);
            return OperationResult.Fail($"S7 setup communication failed: {setupResp.ErrorMessage}", KernelErrorCode.TransportIoError);
        }

        // Step-2 校验：S7 响应消息类型 = 0x03（Ack Data），Function = 0xF0
        // 校验：[8]=0x03(ACK响应类型)，[17-18]=0x00(无错误)，[19]=0xF0(Setup Communication功能码)
        if (setupResp.Value.Length < 21
            || setupResp.Value[8]  != 0x03
            || setupResp.Value[17] != 0x00   // error class must be 0
            || setupResp.Value[18] != 0x00   // error code must be 0
            || setupResp.Value[19] != 0xF0)  // function: Setup Communication
        {
            Interlocked.Exchange(ref _handshakeDone, 0);
            string detail = setupResp.Value.Length >= 19
                ? $"type=0x{setupResp.Value[8]:X2} errClass=0x{setupResp.Value[17]:X2} errCode=0x{setupResp.Value[18]:X2}"
                : "response too short";
            return OperationResult.Fail($"S7 setup communication response invalid: {detail}", KernelErrorCode.ProtocolError);
        }

        return OperationResult.Ok;
    }
}
