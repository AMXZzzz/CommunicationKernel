using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Communication.Protocol.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: IProtocolDriver.cs
/// 层级: Communication.Protocol / Abstractions
/// 作用: 定义协议驱动的统一行为契约。
/// 说明:
/// 1) 协议驱动负责“协议语义”处理（组帧/解析/读写规则）。
/// 2) 协议驱动通过 <see cref="ITransportClient"/> 使用具体通讯介质。
/// 3) 该接口不承载路由与并发控制，路由由 Host/Router 层处理。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IProtocolDriver {
    /// <summary>
    /// 当前协议元信息。
    /// </summary>
    ProtocolMetadata Metadata { get; }

    /// <summary>
    /// 构建“读”操作的协议请求帧。
    /// </summary>
    Task<OperationResult<byte[]>> BuildReadFrameAsync(string address, int length, CancellationToken cancellationToken);

    /// <summary>
    /// 构建“写”操作的协议请求帧。
    /// </summary>
    Task<OperationResult<byte[]>> BuildWriteFrameAsync(string address, byte[] payload, CancellationToken cancellationToken);

    /// <summary>
    /// 执行一次完整读操作（可包含组帧、发送、响应解析）。
    /// </summary>
    Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken);

    /// <summary>
    /// 执行一次完整写操作（可包含组帧、发送、响应校验）。
    /// </summary>
    Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken);
}
