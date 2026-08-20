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
/// 1) BuildReadFrame / BuildWriteFrame 为同步方法：帧构建是纯 CPU 计算，
///    无 IO，无需 Task 包装，强制 async 只产生无意义的 Task 分配。
/// 2) ReadAsync / WriteAsync 含 IO，保持 async。
/// 3) 不承载路由与并发控制，由 Host/Router 层处理。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IProtocolDriver {
    /// <summary>当前协议元信息。</summary>
    ProtocolMetadata Metadata { get; }

    /// <summary>同步构建"读"请求帧（纯 CPU，无 IO）。</summary>
    OperationResult<byte[]> BuildReadFrame(string address, int length);

    /// <summary>同步构建"写"请求帧（纯 CPU，无 IO）。</summary>
    OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload);

    /// <summary>执行一次完整读操作（含发送、响应解析）。</summary>
    Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken);

    /// <summary>执行一次完整写操作（含发送、响应校验）。</summary>
    Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken);
}
