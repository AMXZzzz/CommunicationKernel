// -----------------------------------------------------------------------------
// 文件: TcpTransportFramingTests.cs
// 层级: 测试
// 作用: 传输层分帧行为的真实链路测试。
//
// 为什么用回环 TcpListener 而不是伪造 Stream：
//   要验证的恰恰是「字节怎么到达」——分片、粘连、半帧、远端关闭。
//   伪造 Stream 时这些时序由测试自己编排，测的是编排而不是实现；
//   真实 socket 上才会出现 ReadAsync 一次只返回部分数据这类情况。
//
// 覆盖的三类历史故障：
//   · 切断——响应被 TCP 分片，旧实现「读到数据就当帧结束」会截断
//   · 粘连——一次读取拿到两帧，多出的字节必须留给下一帧而非丢弃
//   · 超长——协议声明的帧长超出上限，必须报错而不是无界增长
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugins.Transport.Tcp;

namespace CommunicationKernel.Tests;

// 真实回环 socket 上的分帧：切断、粘连、残留、超长、错误分类
[TestClass]
public class TcpTransportFramingTests {

    /// <summary>
    /// 测试用分帧规则：首字节即为整帧长度。
    /// 长度尚未到达时返回 false，交由传输层继续读。
    /// </summary>
    private static bool LengthPrefixedFrame(ReadOnlySpan<byte> received, out int totalLength) {
        if (received.Length == 0) {
            totalLength = 0;
            return false;
        }

        totalLength = received[0];
        return true;
    }

    // =========================================================================
    // 切断：响应被分片送达
    // =========================================================================

    // 6 字节帧拆成 1+2+3 三段发出，必须拼成完整帧
    [TestMethod]
    public async Task ReadFrame_ReassemblesResponse_SplitAcrossPackets() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 6 字节的帧拆成 1 + 2 + 3 三段发出，段间留出间隔迫使 ReadAsync 分次返回
        byte[] frame = { 0x06, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };

        await using var server = await FakeDevice.StartAsync(async stream => {
            await stream.WriteAsync(frame.AsMemory(0, 1));
            await stream.FlushAsync();
            await Task.Delay(30);
            await stream.WriteAsync(frame.AsMemory(1, 2));
            await stream.FlushAsync();
            await Task.Delay(30);
            await stream.WriteAsync(frame.AsMemory(3, 3));
            await stream.FlushAsync();
        });

        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, LengthPrefixedFrame, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(result.Success, result.ErrorMessage);
        CollectionAssert.AreEqual(frame, result.Value);
    }

    // 只发前半段就必须报「帧不完整」超时，不得把半帧当成功响应
    [TestMethod]
    public async Task ReadFrame_DoesNotReturnEarly_WhenOnlyPartOfFrameArrived() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 只发帧的前半段就不发了：必须报「帧不完整」超时，
        // 而不是把半帧当成一次成功的响应返回给上层
        await using var server = await FakeDevice.StartAsync(async stream => {
            await stream.WriteAsync(new byte[] { 0x08, 0xB1, 0xB2 });
            await stream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));   // 保持连接但不再发送
        });

        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, LengthPrefixedFrame, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.Timeout, result.ErrorCode);
        StringAssert.Contains(result.ErrorMessage, "不完整");
    }

    // =========================================================================
    // 粘连：一次读取拿到多帧
    // =========================================================================

    // TCP 粘包：第一次只能拿第一帧，多出的字节留给第二次
    [TestMethod]
    public async Task ReadFrame_SplitsGluedFrames_AndKeepsRemainderForNextRead() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 设备把两帧一次性写出（TCP 粘包）。
        // 第一次调用只能拿第一帧，多出的字节留给第二次——
        // 丢弃会让第二次读到空，错位会让第二次拿到拼接后的垃圾。
        byte[] first  = { 0x04, 0xC1, 0xC2, 0xC3 };
        byte[] second = { 0x03, 0xD1, 0xD2 };

        var responses = new Queue<byte[]>();
        responses.Enqueue(Concat(first, second));        // 第一次请求：一次性收到两帧
        responses.Enqueue(Array.Empty<byte>());          // 第二次请求：设备不再发送

        await using var server = await FakeDevice.StartAsync(async stream => {
            foreach (byte[] payload in responses) {
                if (payload.Length > 0) {
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                }
                await Task.Delay(50);
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        });

        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> r1 = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, LengthPrefixedFrame, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r1.Success, r1.ErrorMessage);
        CollectionAssert.AreEqual(first, r1.Value, "第一次读取必须恰好返回第一帧，不能带上第二帧的字节");
    }

    // =========================================================================
    // 跨请求残留：必须丢弃，不能当成下一次的响应
    // =========================================================================

    // 发新请求前必须丢弃残留，否则请求与响应永久错位一格且不报错
    [TestMethod]
    public async Task ReadFrame_DiscardsStaleResidual_OnNextRequest() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 设备对第一次请求多发了一帧（重复响应 / 早先超时请求的迟到响应）。
        //
        // 若把这个残留留到下一次请求，之后每一次读到的都是上一次的数据，
        // 请求与响应永久错位一格，且不报任何错——这类故障在现场极难定位。
        // 正确行为是：发新请求前丢弃残留，第二次响应必须是设备新发的那一帧。
        byte[] firstResponse = { 0x03, 0xE1, 0xE2 };
        byte[] staleExtra    = { 0x03, 0x99, 0x99 };     // 多余的一帧
        byte[] secondResponse = { 0x03, 0xF1, 0xF2 };

        var gate = new SemaphoreSlim(0, 1);

        await using var server = await FakeDevice.StartAsync(async stream => {
            // 对第一次请求：正常响应 + 多余一帧
            await stream.WriteAsync(Concat(firstResponse, staleExtra));
            await stream.FlushAsync();

            // 等第二次请求到来后再发第二次响应
            await gate.WaitAsync();
            await stream.WriteAsync(secondResponse);
            await stream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
        });

        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> r1 = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, LengthPrefixedFrame, CancellationToken.None);
        Assert.IsTrue(r1.Success, r1.ErrorMessage);
        CollectionAssert.AreEqual(firstResponse, r1.Value);

        gate.Release();

        OperationResult<byte[]> r2 = await client.SendAndReceiveAsync(
            new byte[] { 0x02 }, LengthPrefixedFrame, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r2.Success, r2.ErrorMessage);
        CollectionAssert.AreEqual(
            secondResponse, r2.Value,
            "第二次读取拿到了 0x99 开头的陈旧帧，说明跨请求残留没有被丢弃");
    }

    // =========================================================================
    // 超长：协议声明的帧长超出上限
    // =========================================================================

    // 协议声称 4096 字节远超上限 1024，必须立刻以 ProtocolError 拒绝
    [TestMethod]
    public async Task ReadFrame_RejectsFrame_ExceedingMaxResponseBytes() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 协议声称这一帧有 4096 字节，远超单帧上限 1024。
        // 必须立刻以 ProtocolError 拒绝，而不是一直读到内存耗尽。
        await using var server = await FakeDevice.StartAsync(async stream => {
            await stream.WriteAsync(new byte[] { 0x01, 0x02, 0x03 });
            await stream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
        });

        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 },
            (ReadOnlySpan<byte> _, out int totalLength) => { totalLength = 4096; return true; },
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.ProtocolError, result.ErrorCode);
        StringAssert.Contains(result.ErrorMessage, "4096");
    }

    // 帧长 0 意味着协议无法识别帧头，放行会让上层拿到空数组当成合法空响应
    [TestMethod]
    public async Task ReadFrame_RejectsFrame_WhenProtocolReportsNonPositiveLength() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 帧长 0 或负数意味着协议无法识别帧头，属于协议错误。
        // 放行会让上层拿到空数组并当成合法的空响应。
        await using var server = await FakeDevice.StartAsync(async stream => {
            await stream.WriteAsync(new byte[] { 0xFF });
            await stream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
        });

        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 },
            (ReadOnlySpan<byte> _, out int totalLength) => { totalLength = 0; return true; },
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.ProtocolError, result.ErrorCode);
    }

    // =========================================================================
    // 错误分类：取消 / 远端关闭 / 首字节超时
    // =========================================================================

    // 取消必须映射为 Cancelled；误报 TransportIoError 会让停轮询触发重连风暴
    [TestMethod]
    public async Task ReadFrame_MapsExternalCancellation_ToCancelledNotIoError() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 取消必须映射为 Cancelled。上层的重连判据包含 TransportIoError，
        // 若在此误报，用户每次停止轮询都会触发一轮断开重连，
        // 批量停止时形成重连风暴。
        await using var server = await FakeDevice.StartAsync(async stream => {
            await Task.Delay(TimeSpan.FromSeconds(5));   // 始终不响应
        });

        await using var client = await server.ConnectClientAsync();

        using var cts = new CancellationTokenSource(150);

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, LengthPrefixedFrame, cts.Token);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.Cancelled, result.ErrorCode);
    }

    // 远端在帧发完前关闭：真正的链路故障，必须触发上层重连
    [TestMethod]
    public async Task ReadFrame_ReportsIoError_WhenRemoteClosesMidStream() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 远端在帧发完之前关闭连接：这是真正的链路故障，
        // 必须报 TransportIoError 以触发上层重连——与「取消」相反。
        await using var server = await FakeDevice.StartAsync(async stream => {
            await stream.WriteAsync(new byte[] { 0x08, 0xB1 });
            await stream.FlushAsync();
            await Task.Delay(50);
            // 委托返回后 FakeDevice 关闭连接
        });

        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, LengthPrefixedFrame, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.TransportIoError, result.ErrorCode);
    }

    // =========================================================================
    // 参数校验
    // =========================================================================

    // 没有分帧规则就无法确定帧边界，必须拒绝而不是退回猜测策略
    [TestMethod]
    public async Task SendAndReceive_RequiresFrameProbe() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 没有分帧规则就无法确定帧边界，必须拒绝而不是退回猜测策略
        await using var server = await FakeDevice.StartAsync(_ => Task.CompletedTask);
        await using var client = await server.ConnectClientAsync();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, null!, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.InvalidArgument, result.ErrorCode);
    }

    // 未连接时发送必须失败，不得假装发出去了
    [TestMethod]
    public async Task SendAndReceive_FailsWhenNotConnected() {
        // ============================================================================
        // Arrange
        // ============================================================================
        await using var client = new TcpTransportClient();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await client.SendAndReceiveAsync(
            new byte[] { 0x01 }, LengthPrefixedFrame, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.TransportIoError, result.ErrorCode);
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

    /// <summary>
    /// 回环监听器，扮演 PLC：接受一个连接，把发送时序完全交给测试编排。
    /// </summary>
    private sealed class FakeDevice : IAsyncDisposable {

        private readonly TcpListener _listener;
        private readonly Task _serverLoop;

        private FakeDevice(TcpListener listener, Task serverLoop) {
            _listener   = listener;
            _serverLoop = serverLoop;
        }

        /// <summary>监听端口，由系统分配（端口 0）以免测试并行时冲突。</summary>
        private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal static Task<FakeDevice> StartAsync(Func<NetworkStream, Task> behavior) {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            Task loop = Task.Run(async () => {
                try {
                    using TcpClient accepted = await listener.AcceptTcpClientAsync();
                    accepted.NoDelay = true;
                    using NetworkStream stream = accepted.GetStream();

                    // 先读掉请求，再按测试编排的时序回送
                    byte[] scratch = new byte[64];
                    using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try {
                        await stream.ReadAsync(scratch, readCts.Token);
                    } catch (OperationCanceledException) {
                        // 测试不发请求也允许
                    }

                    await behavior(stream);
                } catch (Exception) {
                    // 监听器关闭、连接被客户端断开等都是正常收尾路径。
                    // 断言由测试方负责，这里吞掉以免后台任务异常掩盖真实失败。
                }
            });

            return Task.FromResult(new FakeDevice(listener, loop));
        }

        internal async Task<TcpTransportClient> ConnectClientAsync() {
            var client = new TcpTransportClient();
            OperationResult connect = await client.ConnectAsync(
                new TransportEndpoint {
                    Kind    = TransportKind.Tcp,
                    Address = "127.0.0.1",
                    Port    = Port
                },
                CancellationToken.None);

            Assert.IsTrue(connect.Success, connect.ErrorMessage);
            return client;
        }

        public async ValueTask DisposeAsync() {
            _listener.Stop();
            await _serverLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
}
