// -----------------------------------------------------------------------------
// 文件: FrameReaderTests.cs
// 层级: 测试
// 作用: 直接测试 TCP 与串口共用的分帧读取器。
//
// 为什么值得单独测：
//   串口的分帧行为原本测不到——真串口需要虚拟串口对，CI 上没有。
//   把读循环抽成基于 Stream 的组件后，一个内存流就够了，
//   而这份实现正是两个传输插件实际运行的那一份。
//
// TcpTransportFramingTests 走真实 socket，验证的是"接进去也确实对"；
// 本文件用可编排的流，覆盖真实链路上难以稳定构造的时序边界。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Communication.Transport.Framing;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Tests;

// 可编排内存流上的分帧契约：切断、粘连、残留、非法帧长、错误分类
[TestClass]
public class FrameReaderTests {

    /// <summary>测试用分帧规则：首字节即为整帧长度。</summary>
    private static bool LengthPrefixed(ReadOnlySpan<byte> received, out int totalLength) {
        if (received.Length == 0) { totalLength = 0; return false; }
        totalLength = received[0];
        return true;
    }

    private static FrameReader NewReader(string medium = "串口")
        => new(maxResponseBytes: 1024,
               firstByteTimeoutMs: 300,
               subsequentByteTimeoutMs: 200,
               mediumName: medium);

    // =========================================================================
    // 分片重组
    // =========================================================================

    // 逐字节到达必须拼成完整帧；旧实现「读到数据就当帧结束」在这里必然截断
    [TestMethod]
    public async Task ReassemblesFrame_DeliveredOneByteAtATime() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 低波特率串口上每个字节都可能单独到达。
        // 旧的"读到数据就当帧结束"策略在这里必然截断。
        byte[] frame = { 0x05, 0x11, 0x22, 0x33, 0x44 };
        var stream = new ScriptedStream(SplitIntoChunks(frame, chunkSize: 1));

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result =
            await NewReader().ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(result.Success, result.ErrorMessage);
        CollectionAssert.AreEqual(frame, result.Value);
    }

    // 半帧后设备沉默必须报「帧不完整」超时，不得把半帧当成功响应
    [TestMethod]
    public async Task ReportsTimeout_WhenFrameNeverCompletes() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 半帧后设备不再发送：必须报"帧不完整"，
        // 而不是把半帧当成一次成功响应交给上层解析
        var stream = new ScriptedStream(new[] { new byte[] { 0x08, 0xAA, 0xBB } }, hangAfterScript: true);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result =
            await NewReader().ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.Timeout, result.ErrorCode);
        StringAssert.Contains(result.ErrorMessage, "不完整");
    }

    // 首字节都没等到：错误信息必须指向「等待响应首字节超时」，与「帧不完整」区分开
    [TestMethod]
    public async Task ReportsTimeout_WhenNoResponseAtAll() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 首字节都没等到：错误信息应指向"等待响应首字节超时"，
        // 与"帧不完整"区分开——两者的排查方向完全不同
        var stream = new ScriptedStream(Array.Empty<byte[]>(), hangAfterScript: true);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result =
            await NewReader().ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.Timeout, result.ErrorCode);
        StringAssert.Contains(result.ErrorMessage, "首字节");
    }

    // =========================================================================
    // 粘连与残留
    // =========================================================================

    // 一次读取拿到两帧：第一次只返回第一帧，多出的字节留给第二次
    [TestMethod]
    public async Task SplitsGluedFrames_AndConsumesRemainderOnNextRead() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 一次读取拿到两帧。第一次调用只能返回第一帧，
        // 多出的字节必须留给第二次——丢弃会让第二次读空，
        // 错位会让第二次拿到拼接后的垃圾。
        byte[] first  = { 0x03, 0xC1, 0xC2 };
        byte[] second = { 0x04, 0xD1, 0xD2, 0xD3 };

        var stream = new ScriptedStream(new[] { Concat(first, second) }, hangAfterScript: true);
        FrameReader reader = NewReader();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> r1 = await reader.ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r1.Success, r1.ErrorMessage);
        CollectionAssert.AreEqual(first, r1.Value);

        // 第二帧完全来自残留，不需要再从流里读一个字节
        OperationResult<byte[]> r2 = await reader.ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);
        Assert.IsTrue(r2.Success, r2.ErrorMessage);
        CollectionAssert.AreEqual(second, r2.Value);
    }

    // 跨请求残留若不丢弃，之后每一次读到的都是上一次的数据
    [TestMethod]
    public async Task DiscardResidual_DropsStaleBytes_SoNextReadIsNotOffByOne() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 跨请求残留（重复响应 / 迟到响应）若不丢弃，
        // 之后每一次读到的都是上一次的数据，请求与响应永久错位一格且不报错。
        byte[] response = { 0x03, 0xE1, 0xE2 };
        byte[] stale    = { 0x03, 0x99, 0x99 };
        byte[] fresh    = { 0x03, 0xF1, 0xF2 };

        var stream = new ScriptedStream(new[] { Concat(response, stale), fresh }, hangAfterScript: true);
        FrameReader reader = NewReader();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> r1 = await reader.ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);
        CollectionAssert.AreEqual(response, r1.Value);

        // 发新请求前丢弃残留
        reader.DiscardResidual();

        OperationResult<byte[]> r2 = await reader.ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r2.Success, r2.ErrorMessage);
        CollectionAssert.AreEqual(fresh, r2.Value,
            "读到了 0x99 开头的陈旧帧，说明 DiscardResidual 没有生效");
    }

    // =========================================================================
    // 非法帧长
    // =========================================================================

    // 协议声称的帧长超出上限必须立刻以 ProtocolError 拒绝，而不是读到内存耗尽
    [TestMethod]
    public async Task RejectsFrameLength_ExceedingLimit() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 协议声称的帧长超出单帧上限：必须立刻以 ProtocolError 拒绝，
        // 而不是一直读到内存耗尽
        var stream = new ScriptedStream(new[] { new byte[] { 0x01, 0x02 } }, hangAfterScript: true);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await NewReader().ReadFrameAsync(
            stream, (ReadOnlySpan<byte> _, out int len) => { len = 99_999; return true; },
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.ProtocolError, result.ErrorCode);
        StringAssert.Contains(result.ErrorMessage, "99999");
    }

    // 帧长 0 意味着协议无法识别帧头；放行会让上层拿到空数组当成合法空响应
    [TestMethod]
    public async Task RejectsNonPositiveFrameLength() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 帧长 0 意味着协议无法识别帧头；放行会让上层拿到空数组
        // 并当成一次合法的空响应
        var stream = new ScriptedStream(new[] { new byte[] { 0xFF } }, hangAfterScript: true);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await NewReader().ReadFrameAsync(
            stream, (ReadOnlySpan<byte> _, out int len) => { len = 0; return true; },
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.ProtocolError, result.ErrorCode);
    }

    // 协议一直判不出帧长（帧头永远对不上），读到上限必须停下
    [TestMethod]
    public async Task RejectsStream_ThatNeverForms_AFrame() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 协议一直判不出帧长（例如帧头永远对不上），读到上限必须停下
        var reader = new FrameReader(
            maxResponseBytes: 16, firstByteTimeoutMs: 300,
            subsequentByteTimeoutMs: 200, mediumName: "串口");

        var stream = new ScriptedStream(new[] { new byte[32] }, hangAfterScript: true);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await reader.ReadFrameAsync(
            stream, (ReadOnlySpan<byte> _, out int len) => { len = 0; return false; },
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.ProtocolError, result.ErrorCode);
        StringAssert.Contains(result.ErrorMessage, "仍未成帧");
    }

    // =========================================================================
    // 错误分类
    // =========================================================================

    // 取消必须与超时区分：误报 Timeout 会让批量停止形成重连风暴
    [TestMethod]
    public async Task MapsExternalCancellation_ToCancelled_NotTimeout() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 取消必须与超时区分：上层的重连判据包含 Timeout / TransportIoError，
        // 用户主动停轮询若被报成那两者，批量停止会形成重连风暴
        var stream = new ScriptedStream(Array.Empty<byte[]>(), hangAfterScript: true);
        using var cts = new CancellationTokenSource(100);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result =
            await NewReader().ReadFrameAsync(stream, LengthPrefixed, cts.Token);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.Cancelled, result.ErrorCode);
    }

    // 远端在帧发完前关闭：真正的链路故障，必须触发上层重连
    [TestMethod]
    public async Task MapsStreamClose_ToTransportIoError() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 远端在帧发完前关闭：真正的链路故障，必须触发上层重连
        var stream = new ScriptedStream(new[] { new byte[] { 0x08, 0xAA } }, hangAfterScript: false);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result =
            await NewReader().ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.TransportIoError, result.ErrorCode);
    }

    // 错误信息必须点名介质：现场同时接串口与以太网时才能定位是哪一路
    [TestMethod]
    public async Task ErrorMessage_NamesTheMedium() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 现场同时接着串口与以太网设备时，错误信息必须指明是哪一路出的问题
        var stream = new ScriptedStream(Array.Empty<byte[]>(), hangAfterScript: false);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await NewReader("串口")
            .ReadFrameAsync(stream, LengthPrefixed, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        StringAssert.Contains(result.ErrorMessage, "串口");
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static byte[] Concat(params byte[][] parts) {
        int total = 0;
        foreach (byte[] p in parts) total += p.Length;

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] p in parts) {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    private static byte[][] SplitIntoChunks(byte[] data, int chunkSize) {
        var chunks = new List<byte[]>();
        for (int i = 0; i < data.Length; i += chunkSize) {
            int len = Math.Min(chunkSize, data.Length - i);
            byte[] chunk = new byte[len];
            Buffer.BlockCopy(data, i, chunk, 0, len);
            chunks.Add(chunk);
        }
        return chunks.ToArray();
    }

    /// <summary>
    /// 按脚本逐块交付数据的流：每次 ReadAsync 只返回一块，
    /// 精确复现"字节分批到达"的时序。
    /// </summary>
    private sealed class ScriptedStream : Stream {

        private readonly Queue<byte[]> _chunks;
        private readonly bool _hangAfterScript;

        /// <param name="chunks">每次 ReadAsync 交付的数据块。</param>
        /// <param name="hangAfterScript">
        /// 脚本放完之后的行为：true 表示永远不再返回（模拟设备沉默，触发超时）；
        /// false 表示返回 0（模拟远端关闭连接）。
        /// 两者对应完全不同的错误码，必须能分别构造。
        /// </param>
        internal ScriptedStream(IEnumerable<byte[]> chunks, bool hangAfterScript = false) {
            _chunks = new Queue<byte[]>(chunks);
            _hangAfterScript = hangAfterScript;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) {

            if (_chunks.Count > 0) {
                byte[] chunk = _chunks.Dequeue();

                // 真实流不会往装不下的缓冲里塞数据：只交付放得下的部分，
                // 余下重新入队等下一次读取。缺了这一步，
                // "缓冲区已满仍未成帧"这条路径就构造不出来。
                int take = Math.Min(chunk.Length, buffer.Length);
                chunk.AsMemory(0, take).CopyTo(buffer);

                if (take < chunk.Length) {
                    byte[] rest = new byte[chunk.Length - take];
                    Buffer.BlockCopy(chunk, take, rest, 0, rest.Length);
                    _chunks.Enqueue(rest);
                }

                return take;
            }

            if (!_hangAfterScript) return 0;   // 远端关闭

            // 沉默：一直等到调用方的超时或取消令牌触发
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;   // 不可达
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("仅支持异步读取");

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
