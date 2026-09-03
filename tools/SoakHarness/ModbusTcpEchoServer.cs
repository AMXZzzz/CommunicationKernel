// -----------------------------------------------------------------------------
// 文件: tools/SoakHarness/ModbusTcpEchoServer.cs
// 层级: 压测工具（不属于产品代码）
// 作用: 极简 Modbus TCP 从站，供长时压测在本机回环上全速对打。
//
// 为什么要它:
//   压测若用假的 ITransportClient，就绕过了真实 socket、真实分帧、真实 CRC——
//   而那几层恰恰是长时运行最容易出问题的地方（句柄泄漏、粘包、缓冲增长）。
//   本从站让引擎走完整链路，同时不依赖任何真实 PLC，也不占用外部网络。
//
// 刻意保持简陋:
//   只实现读保持寄存器(0x03)与写单个寄存器(0x06)，其余功能码回异常码 0x01。
//   目标是"稳定地当靶子"，不是做一个完整的 Modbus 从站实现。
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace CommunicationKernel.SoakHarness;

/// <summary>本机回环上的极简 Modbus TCP 从站。</summary>
internal sealed class ModbusTcpEchoServer : IAsyncDisposable
{
    /// <summary>寄存器区大小（个）。够压测用，不追求真实设备容量。</summary>
    private const int RegisterCount = 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ushort[] _registers = new ushort[RegisterCount];
    private Task? _acceptLoop;

    /// <summary>已服务的请求总数，用于核对压测确实打到了从站。</summary>
    private long _served;

    /// <summary>已服务请求数。</summary>
    public long Served => Interlocked.Read(ref _served);

    /// <summary>实际监听端口（构造时传 0 则由系统分配）。</summary>
    public int Port { get; }

    /// <param name="port">监听端口；0 表示由系统分配空闲端口。</param>
    public ModbusTcpEchoServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        // 预置可辨识的数据：读回来全是 0 的话，分不清"读到了"和"没读到"
        for (int i = 0; i < _registers.Length; i++)
            _registers[i] = (ushort)(i + 1);
    }

    /// <summary>开始接受连接。</summary>
    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    /// <summary>接受循环：每条连接一个独立处理任务。</summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            // 不 await：一条连接的处理不应挡住后续接受
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    /// <summary>处理一条连接上的所有请求，直到对端断开。</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            // 压测会频繁建连断连，禁用 Nagle 以免把延迟测成协议问题
            client.NoDelay = true;

            try
            {
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[512];

                while (!ct.IsCancellationRequested)
                {
                    // MBAP 头固定 7 字节：[事务ID(2)][协议ID(2)][长度(2)][单元ID(1)]
                    if (!await ReadExactAsync(stream, buffer, 0, 7, ct).ConfigureAwait(false))
                        return;   // 对端正常断开

                    int pduLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2)) - 1;
                    if (pduLen <= 0 || pduLen > buffer.Length - 7) return;   // 畸形帧：断开了事

                    if (!await ReadExactAsync(stream, buffer, 7, pduLen, ct).ConfigureAwait(false))
                        return;

                    byte[] response = BuildResponse(buffer, pduLen);
                    await stream.WriteAsync(response, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _served);
                }
            }
            catch (Exception)
            {
                // 压测期间连接被强行关闭是常态（路由搅动），不打日志以免刷屏
            }
        }
    }

    /// <summary>按请求组响应帧。</summary>
    private byte[] BuildResponse(byte[] req, int pduLen)
    {
        ushort txId = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
        byte unitId = req[6];
        byte fc = req[7];

        byte[] pdu = fc switch
        {
            0x03 => BuildReadResponse(req),
            0x06 => req.AsSpan(7, pduLen).ToArray(),   // 写单个寄存器：原样回显
            _ => new byte[] { (byte)(fc | 0x80), 0x01 },   // 其余：非法功能码
        };

        // 组 MBAP：长度字段 = 单元ID(1) + PDU
        byte[] frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), txId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame, 7);
        return frame;
    }

    /// <summary>组读保持寄存器响应。</summary>
    private byte[] BuildReadResponse(byte[] req)
    {
        int start = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(8, 2));
        int count = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(10, 2));

        // 越界：回异常码 0x02（非法数据地址），与真实从站一致
        if (count <= 0 || count > 125 || start + count > RegisterCount)
            return new byte[] { 0x83, 0x02 };

        byte[] pdu = new byte[2 + count * 2];
        pdu[0] = 0x03;
        pdu[1] = (byte)(count * 2);
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + i * 2, 2), _registers[start + i]);
        return pdu;
    }

    /// <summary>读满指定字节数；对端关闭时返回 false。</summary>
    private static async Task<bool> ReadExactAsync(
        NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset + got, count - got), ct)
                                .ConfigureAwait(false);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>停止监听并等待接受循环收尾。</summary>
    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { }
        }
        _cts.Dispose();
    }
}
