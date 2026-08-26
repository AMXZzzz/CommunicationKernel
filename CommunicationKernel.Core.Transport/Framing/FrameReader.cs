using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

// -----------------------------------------------------------------------------
// 文件: FrameReader.cs
// 层级: Core.Transport / Framing
// 作用: 从任意 Stream 上按协议给出的帧长读取恰好一整帧。
// 归属: 有状态的具体实现（持有残包缓冲），故不放在 Abstractions——那里只留接口与
//       委托，以保证「引用 Abstractions 不会拖入实现」这一判断始终成立。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Transport.Framing;

/// <summary>
/// 从任意 <see cref="Stream"/> 上按协议给出的帧长读取恰好一整帧。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要抽出来。</b>TCP 与串口两个插件此前各有一份逐行雷同的读循环：
/// 残留字节处理、首字节/后续字节两级超时、取消与超时的区分、
/// 上限保护、归还缓冲前清零。同一处缺陷要改两遍，
/// 而本项目已经栽过一次「同类缺陷只改了一个插件」的跟头。
/// </para>
/// <para>
/// 抽出来还有一个直接收益：串口的分帧行为终于可测。
/// 真串口需要虚拟串口对才能测，而 <see cref="Stream"/> 只要一个内存流。
/// </para>
/// <para>
/// <b>帧边界一律由协议决定，本类不猜。</b>
/// 早期实现靠"静默若干毫秒"判帧尾，在 TCP 分片、USB 转串口、
/// TCP 转串口透传网关上都会截断或粘连——那些环境里字节到达时序
/// 已被驱动重排，静默窗口不再可靠。
/// </para>
/// </remarks>
public sealed class FrameReader {

    // ============================================================================
    // 分帧参数与残留状态
    // ============================================================================

    private readonly int _maxResponseBytes;
    private readonly int _firstByteTimeoutMs;
    private readonly int _subsequentByteTimeoutMs;
    private readonly string _mediumName;

    /// <summary>上一次读取中超出该帧的字节。下次读取优先消费。</summary>
    private byte[]? _residual;

    /// <summary><see cref="_residual"/> 中的有效字节数。</summary>
    private int _residualLength;

    /// <param name="maxResponseBytes">单帧上限（字节），超出视为协议错误。</param>
    /// <param name="firstByteTimeoutMs">等待响应首字节的超时（毫秒）。</param>
    /// <param name="subsequentByteTimeoutMs">
    /// 帧已开始接收后等待后续字节的超时（毫秒）。
    /// 这是"帧不完整"的兜底阈值，<b>不是</b>"帧已结束"的判定。
    /// </param>
    /// <param name="mediumName">出现在错误信息里的介质名，如 "TCP"、"串口"。</param>
    public FrameReader(
        int maxResponseBytes,
        int firstByteTimeoutMs,
        int subsequentByteTimeoutMs,
        string mediumName) {

        // 冻结分帧参数：单帧上限、两级超时、介质名（写入错误文案以便区分 TCP / 串口）
        _maxResponseBytes        = maxResponseBytes;
        _firstByteTimeoutMs      = firstByteTimeoutMs;
        _subsequentByteTimeoutMs = subsequentByteTimeoutMs;
        _mediumName              = mediumName;
    }

    // ============================================================================
    // 残留处理
    // ============================================================================

    /// <summary>
    /// 丢弃跨请求残留的字节。发送新请求前必须调用。
    /// </summary>
    /// <remarks>
    /// 残留的作用域是「一次帧读取之内」——组帧时多读到的字节必须留着，
    /// 丢了会把下一帧截断。但跨请求就不同了：路由层的 I/O 门保证同一时刻
    /// 只有一个在途请求，所以一次响应读完后还留在缓冲里的字节，
    /// 只可能是重复响应，或早先超时请求的迟到响应。
    /// 把它当成本次请求的响应会让请求/响应<b>永久错位一格</b>——
    /// 之后每一次读到的都是上一次的数据，且不报任何错。
    /// </remarks>
    // 只清残留长度：发送新请求前丢掉迟到/重复响应，防止请求与响应永久错位
    public void DiscardResidual() => _residualLength = 0;

    // ============================================================================
    // 读帧
    // ============================================================================

    /// <summary>
    /// 从 <paramref name="stream"/> 读取恰好一个完整帧。
    /// </summary>
    /// <param name="stream">已连接的数据流。</param>
    /// <param name="tryGetFrameLength">协议提供的帧长判定回调。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    /// <returns>
    /// 成功时返回恰好一帧的字节；超出本帧的字节留在内部供下次调用消费。
    /// 失败时错误码区分 <see cref="KernelErrorCode.Cancelled"/>（外部取消）、
    /// <see cref="KernelErrorCode.Timeout"/>（帧不完整）、
    /// <see cref="KernelErrorCode.ProtocolError"/>（帧长非法或超限）与
    /// <see cref="KernelErrorCode.TransportIoError"/>（远端关闭）。
    /// </returns>
    public async Task<OperationResult<byte[]>> ReadFrameAsync(
        Stream stream, TryGetFrameLength tryGetFrameLength, CancellationToken cancellationToken) {

        // 从共享池租一块上限大小的缓冲，避免每次读帧 new byte[]
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_maxResponseBytes);
        int    total  = 0;

        try {
            // 先消费上一次多读到的残留字节
            if (_residualLength > 0) {
                // 把残留拷到本次缓冲头部，当作已经到达的响应前缀
                Buffer.BlockCopy(_residual!, 0, buffer, 0, _residualLength);
                total = _residualLength;
                // 计数归零：残留已移交，避免下次再被消费一次
                _residualLength = 0;
            }

            // 循环直到凑满一帧、超时、取消或协议判定非法
            while (true) {
                // ── 用已有字节尝试判定帧长 ──
                if (total > 0 && tryGetFrameLength(buffer.AsSpan(0, total), out int frameLength)) {
                    // 协议认不出帧头（功能码非法、起始符不对等），属协议错误而非传输中断
                    if (frameLength <= 0)
                        return OperationResult<byte[]>.Fail(
                            "协议判定响应帧非法（无法识别的帧头）", KernelErrorCode.ProtocolError);

                    // PLC 声明的帧长超过内核单帧上限，防止恶意/损坏报文撑爆内存
                    if (frameLength > _maxResponseBytes)
                        return OperationResult<byte[]>.Fail(
                            $"响应帧声明 {frameLength} 字节，超出单帧上限 {_maxResponseBytes}",
                            KernelErrorCode.ProtocolError);

                    if (total >= frameLength) {
                        // 已读满一帧：截取本帧，余下留作残留
                        byte[] frame = new byte[frameLength];
                        // 只拷本帧长度，多出的字节交给 SaveResidual
                        Buffer.BlockCopy(buffer, 0, frame, 0, frameLength);
                        SaveResidual(buffer, frameLength, total - frameLength);
                        return OperationResult<byte[]>.Ok(frame);
                    }
                }

                // 缓冲已满仍无法成帧：协议回调一直返回 false，或帧长尚未出现
                if (total >= _maxResponseBytes)
                    return OperationResult<byte[]>.Fail(
                        $"响应超过单帧上限 {_maxResponseBytes} 字节仍未成帧",
                        KernelErrorCode.ProtocolError);

                // ── 继续读取；首字节用长超时，后续字节用短超时 ──
                // 尚未收到任何字节：给 PLC 更长的响应时间；帧已开始则用短超时防半帧挂起
                int timeoutMs = total == 0 ? _firstByteTimeoutMs : _subsequentByteTimeoutMs;
                // 把外部取消与本段超时绑在一起，任一触发都中断 ReadAsync
                using CancellationTokenSource readCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(timeoutMs);

                int read;
                try {
                    // 从当前位置继续填缓冲，最多读到单帧上限
                    read = await stream
                        .ReadAsync(buffer, total, _maxResponseBytes - total, readCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    // 区分外部取消与内部超时：前者不应触发上层重连。
                    // 上层的重连判据包含 TransportIoError 与 Timeout，
                    // 若把用户主动取消也报成那两者，批量停止轮询会形成重连风暴。
                    if (cancellationToken.IsCancellationRequested)
                        return OperationResult<byte[]>.Fail(
                            $"{_mediumName}读取被取消", KernelErrorCode.Cancelled);

                    // 内部超时：首字节没来或半帧之后静默，报 Timeout 供上层决定是否重连
                    return OperationResult<byte[]>.Fail(
                        total == 0
                            ? $"等待响应首字节超时（{_firstByteTimeoutMs} ms）"
                            : $"响应帧不完整：已收 {total} 字节，后续字节等待超时（{_subsequentByteTimeoutMs} ms）",
                        KernelErrorCode.Timeout);
                }

                // Read 返回 0 表示对端关了连接（PLC 掉线、网关断开）
                if (read == 0)
                    return OperationResult<byte[]>.Fail(
                        $"{_mediumName}连接已被远端关闭", KernelErrorCode.TransportIoError);

                // 累加已读字节，下一轮再交给协议判定帧长
                total += read;
            }
        }
        finally {
            // 归还前清零：缓冲区来自共享池，残留报文可能被其他消费方读到
            Array.Clear(buffer, 0, Math.Min(total, buffer.Length));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>保存超出本帧的字节，供下次 <see cref="ReadFrameAsync"/> 优先消费。</summary>
    private void SaveResidual(byte[] source, int offset, int length) {
        // 没有多余字节：清掉残留计数，避免下次误把旧数据当新帧前缀
        if (length <= 0) {
            _residualLength = 0;
            return;
        }

        // 惰性分配残留缓冲，大小与单帧上限相同，足够放下任何截断余量
        _residual ??= new byte[_maxResponseBytes];
        // 把本帧之后的字节挪到残留区，供下一次 ReadFrameAsync 优先消费
        Buffer.BlockCopy(source, offset, _residual, 0, length);
        _residualLength = length;
    }
}
