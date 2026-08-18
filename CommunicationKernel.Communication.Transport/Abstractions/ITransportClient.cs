using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Communication.Transport.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ITransportClient.cs
/// 层级: Communication.Transport / Abstractions
/// 作用: 抽象“通讯介质客户端”的最小能力边界。
/// 说明:
/// 1) 该接口只关心连接生命周期与字节流收发，不包含协议语义。
/// 2) 上层协议驱动通过该接口访问串口/WiFi/蓝牙/TCP 等介质。
/// 3) 返回统一 <see cref="OperationResult"/> 以保证跨层错误语义一致。
/// -----------------------------------------------------------------------------
/// </summary>
public interface ITransportClient : IAsyncDisposable {
    /// <summary>
    /// 客户端实例的逻辑标识。
    /// 一般对应插件或介质实现名称，用于日志与诊断。
    /// </summary>
    string TransportId { get; }

    /// <summary>
    /// 当前客户端介质类型。
    /// </summary>
    TransportKind Kind { get; }

    /// <summary>
    /// 建立介质连接。
    /// </summary>
    Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// 发送请求并接收响应。
    /// </summary>
    Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] request, CancellationToken cancellationToken);

    /// <summary>
    /// 断开介质连接。
    /// </summary>
    Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken);
}
