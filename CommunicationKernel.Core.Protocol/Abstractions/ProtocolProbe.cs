// -----------------------------------------------------------------------------
// 文件: Abstractions/ProtocolProbe.cs
// 层级: Core.Protocol / Abstractions
// 作用: 把一次短读映射成链路探活结果。对端回了完整帧（含协议异常）即视为活着。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Core.Protocol.Abstractions;

/// <summary>链路探活的公共判定：传输层失败才算断，协议层失败说明 PLC 还在答。</summary>
public static class ProtocolProbe
{
    /// <summary>
    /// 读一个短地址作心跳。成功、或对端回了协议错误，都算链路活着。
    /// </summary>
    public static async Task<OperationResult> ReadAsync(
        IProtocolDriver driver,
        ITransportClient client,
        string address,
        int length,
        CancellationToken cancellationToken)
    {
        OperationResult<byte[]> read = await driver
            .ReadAsync(client, address, length, cancellationToken)
            .ConfigureAwait(false);

        if (read.Success)
            return OperationResult.Ok;

        if (read.ErrorCode is KernelErrorCode.TransportIoError
            or KernelErrorCode.TransportUnavailable
            or KernelErrorCode.Timeout
            or KernelErrorCode.Cancelled)
            return OperationResult.Fail(read.ErrorMessage, read.ErrorCode);

        return OperationResult.Ok;
    }
}
