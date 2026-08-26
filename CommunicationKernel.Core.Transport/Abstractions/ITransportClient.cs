using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;

// -----------------------------------------------------------------------------
// 文件: ITransportClient.cs
// 层级: Core.Transport / Abstractions
// 作用: 抽象“通讯介质客户端”的最小能力边界。
// 说明:
//   1) 该接口只关心连接生命周期与字节流收发，不包含协议语义。
//   2) 上层协议驱动通过该接口访问串口/WiFi/蓝牙/TCP 等介质。
//   3) 返回统一 OperationResult 以保证跨层错误语义一致。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Transport.Abstractions;

/// <summary>
/// 通讯介质客户端：连接生命周期与字节流收发，不含协议语义。
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
    /// 连接当前是否仍然可用。<b>不产生任何协议流量。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在的理由：路由状态此前只在「注册成功」与「每次读写」时更新。
    /// 一条注册后没人读写的路由，即使 PLC 早已断电，界面仍会一直显示在线——
    /// 显示「在线」而实际断开，比显示离线危险得多。
    /// </para>
    /// <para>
    /// <b>能测出什么，测不出什么。</b>本属性做的是介质层的廉价探测
    /// （TCP 查套接字对端是否已关闭，串口查端口是否仍打开），
    /// 因此能立刻发现「对端进程退出 / 主动断开」这类带 FIN/RST 的断链；
    /// 但拔网线、掉电这种半开连接不会产生任何报文，仅靠本属性发现不了——
    /// 那要靠 TCP keepalive 或真正发一帧出去。
    /// 换言之：返回 false 一定是断了；返回 true 只代表「没有证据表明断了」。
    /// </para>
    /// <para>
    /// 实现须保证不抛异常：探测失败一律视为不可用。
    /// </para>
    /// </remarks>
    bool IsConnectionAlive { get; }

    /// <summary>
    /// 建立介质连接。
    /// </summary>
    Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// 发送请求并接收<b>一个完整帧</b>的响应。
    /// </summary>
    /// <param name="request">待发送的完整请求帧。</param>
    /// <param name="tryGetFrameLength">
    /// 由协议驱动提供的帧完整性判定回调。传输层不理解协议，
    /// 必须依赖它决定何时停止读取，禁止用时序静默猜测帧边界。
    /// 详见 <see cref="TryGetFrameLength"/>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 实现须保证：返回的字节恰为一帧，多读到的下一帧数据必须留在缓冲区中
    /// 供下次调用使用，不得丢弃——丢弃会导致请求与响应永久错位。
    /// </remarks>
    Task<OperationResult<byte[]>> SendAndReceiveAsync(
        byte[] request,
        TryGetFrameLength tryGetFrameLength,
        CancellationToken cancellationToken);

    /// <summary>
    /// 断开介质连接。
    /// </summary>
    Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken);
}
