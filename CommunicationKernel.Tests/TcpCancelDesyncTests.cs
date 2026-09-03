// -----------------------------------------------------------------------------
// 文件: TcpCancelDesyncTests.cs
// 层级: 测试
// 作用: 钉住「读被取消之后，连接不得失步」。
//
// 来源（120 台 PLC 满负荷压测里抓到的）：
//   事务 ID 不匹配：请求 52066，响应 52065（响应错位或迟到）
//   响应比请求晚一个——读到的是<b>上一次</b>请求的响应。
//
// 成因：
//   请求已经发出、响应还没读，此时读取被取消。那个响应随后到达，
//   静静躺在 socket 的内核接收缓冲里。下一次请求发出后去读，
//   先读到的就是它。
//
//   发送前调的 DiscardResidual() 清的是 FrameReader 自己的残留缓冲，
//   <b>够不着内核缓冲</b>。串口侧有 DiscardInBuffer() 能清，TCP 侧没有对应动作。
//
// 为什么必须修（而不是"很罕见，算了"）：
//   · Modbus TCP 有事务 ID，失步会被认出来——但 ProtocolError 不在重连列表里，
//     没有自愈路径，这条路由会一直错下去；
//   · <b>MEWTOCOL 没有事务 ID</b>。同样的失步在那边不会报任何错，
//     只会让每个寄存器值都变成"上一次读到的那个"——
//     帧本身是完整的、BCC 也是对的，校验和拦不住这种错。
//
// 取消在现场很常见：切换页面、停止轮询、超时、上位机点取消。
// -----------------------------------------------------------------------------

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Plugins.Transport.Tcp;

namespace CommunicationKernel.Tests;

[TestClass]
public class TcpCancelDesyncTests {

    /// <summary>
    /// 一次读被取消后，下一次读必须拿到<b>自己</b>的响应。
    /// </summary>
    /// <remarks>
    /// 用 Modbus TCP 的事务 ID 当"这次响应属于谁"的标签：
    /// 第一次请求用事务 ID 1（响应被延迟，读取中途取消），
    /// 第二次请求用事务 ID 2。若第二次读回来的是 1，就说明连接已经失步。
    /// </remarks>
    [TestMethod]
    public async Task ReadCancelled_NextReadMustNotGetStaleResponse() {
        using var server = new DelayedEchoServer(responseDelayMs: 400);
        server.Start();

        await using ITransportClient client = new TcpTransportFactory().CreateClient();
        OperationResult connect = await client.ConnectAsync(
            new TransportEndpoint { Kind = TransportKind.Tcp, Address = "127.0.0.1", Port = server.Port },
            CancellationToken.None);
        Assert.IsTrue(connect.Success, connect.ErrorMessage);

        // ── 第一次：发出去，但等不到响应就取消 ──
        using (var cts = new CancellationTokenSource(100)) {
            try {
                await client.SendAndReceiveAsync(BuildRequest(txId: 1), Probe, cts.Token);
            } catch (OperationCanceledException) {
                // 预期：取消
            }
        }

        // 给被取消那次的响应足够时间抵达并躺进内核缓冲
        await Task.Delay(600);

        // ── 第二次：正常的一次读 ──
        OperationResult<byte[]> second =
            await client.SendAndReceiveAsync(BuildRequest(txId: 2), Probe, CancellationToken.None);

        // 正确行为有两种，都可接受，但绝不能是"成功地返回上一次的响应"：
        //   1) 明确失败并要求重连（传输层发现自己可能失步）；
        //   2) 成功且拿到本次请求的响应（缓冲被清干净了）。
        if (second.Success)
        {
            ushort echoedTxId = BinaryPrimitives.ReadUInt16BigEndian(second.Value.AsSpan(0, 2));
            Assert.AreEqual(2, echoedTxId,
                "读到了上一次请求的响应，连接已失步。" +
                "在没有事务 ID 的协议（如 MEWTOCOL）上，这会变成静默的错值——" +
                "帧完整、校验也对，没有任何迹象。");
        }
        else
        {
            Assert.AreEqual(KernelErrorCode.TransportIoError, second.ErrorCode,
                "失步后必须报成 IO 错误，因为只有它在上层的重连判据里；" +
                "报成别的错误码会让这条路由再也自愈不了。");
        }
    }

    /// <summary>
    /// 失步之后重连一次，必须恢复正常。
    /// </summary>
    /// <remarks>
    /// 失步标记若没随重连清掉，这条路由会永远以"可能失步"失败，
    /// 现场表现是设备再也连不上——比原来的错位更糟。
    /// </remarks>
    [TestMethod]
    public async Task AfterDesync_ReconnectRestoresService() {
        using var server = new DelayedEchoServer(responseDelayMs: 400);
        server.Start();

        var endpoint = new TransportEndpoint {
            Kind = TransportKind.Tcp, Address = "127.0.0.1", Port = server.Port,
        };

        await using ITransportClient client = new TcpTransportFactory().CreateClient();
        Assert.IsTrue((await client.ConnectAsync(endpoint, CancellationToken.None)).Success);

        // 制造失步
        using (var cts = new CancellationTokenSource(100)) {
            try { await client.SendAndReceiveAsync(BuildRequest(1), Probe, cts.Token); }
            catch (OperationCanceledException) { }
        }
        await Task.Delay(600);

        // 上层遇到 IO 错误时的动作：先断后连
        await client.DisconnectAsync(CancellationToken.None);
        Assert.IsTrue((await client.ConnectAsync(endpoint, CancellationToken.None)).Success);

        OperationResult<byte[]> after =
            await client.SendAndReceiveAsync(BuildRequest(7), Probe, CancellationToken.None);

        Assert.IsTrue(after.Success, "重连之后仍然失败：" + after.ErrorMessage);
        Assert.AreEqual(7, BinaryPrimitives.ReadUInt16BigEndian(after.Value.AsSpan(0, 2)),
            "重连之后拿到的仍不是本次请求的响应");
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    /// <summary>组一个最小的 Modbus TCP 读请求，事务 ID 用于标识本次请求。</summary>
    private static byte[] BuildRequest(ushort txId) {
        byte[] frame = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), txId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);      // 协议 ID
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), 6);      // 长度
        frame[6] = 1;                                                       // 单元 ID
        frame[7] = 0x03;                                                    // 读保持寄存器
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), 0);      // 起始地址
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10, 2), 1);     // 数量
        return frame;
    }

    /// <summary>响应固定 11 字节：MBAP(7) + FC(1) + 字节数(1) + 一个寄存器(2)。</summary>
    private static bool Probe(ReadOnlySpan<byte> received, out int totalLength) {
        totalLength = 11;
        return true;
    }

    /// <summary>延迟应答的回环从站，用来制造「取消时响应仍在路上」。</summary>
    private sealed class DelayedEchoServer : IDisposable {
        private readonly TcpListener _listener;
        private readonly int _delayMs;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public DelayedEchoServer(int responseDelayMs) {
            _delayMs = responseDelayMs;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start() => _ = Task.Run(AcceptAsync);

        private async Task AcceptAsync() {
            try {
                while (!_cts.IsCancellationRequested) {
                    TcpClient c = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => ServeAsync(c));
                }
            } catch { /* 停止监听时正常退出 */ }
        }

        private async Task ServeAsync(TcpClient client) {
            using (client) {
                try {
                    NetworkStream s = client.GetStream();
                    byte[] buf = new byte[64];
                    while (!_cts.IsCancellationRequested) {
                        int got = 0;
                        while (got < 12) {
                            int n = await s.ReadAsync(buf.AsMemory(got, 12 - got), _cts.Token);
                            if (n == 0) return;
                            got += n;
                        }

                        // 慢应答：制造「客户端已取消，响应才到」的时序
                        await Task.Delay(_delayMs, _cts.Token);

                        byte[] resp = new byte[11];
                        Array.Copy(buf, 0, resp, 0, 4);                     // 原样回传事务 ID 与协议 ID
                        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), 5);
                        resp[6] = buf[6];
                        resp[7] = 0x03;
                        resp[8] = 2;
                        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(9, 2), 0xBEEF);
                        await s.WriteAsync(resp, _cts.Token);
                    }
                } catch { /* 连接断开或停止，属正常 */ }
            }
        }

        public void Dispose() {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
