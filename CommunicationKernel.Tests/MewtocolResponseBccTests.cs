// -----------------------------------------------------------------------------
// 文件: MewtocolResponseBccTests.cs
// 层级: 测试
// 作用: 锁住 MEWTOCOL「响应」侧的 BCC 校验。
//
// 背景：
//   发送侧的 BCC 之前算错过（漏了帧头 '%'），已由 MewtocolBccTests 钉住。
//   但接收侧此前<b>完全不校验</b>——解析代码把 BCC 当作尾部垃圾直接丢掉
//   （注释原文："忽略 BCC 和 CR"）。
//
//   后果：RS-485 线上被电气噪声打翻一位的响应会被当成有效数据接受，
//   界面上显示一个错误的寄存器值，还标着"成功"。而现场恰恰是长线缆、
//   变频器旁边这类环境——校验和存在的全部意义就是拦住这种帧。
//
//   这类故障在自测里同样发现不了：不接真设备就没有噪声，
//   所以必须用<b>人为篡改过的合成帧</b>把行为钉死。
// -----------------------------------------------------------------------------

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Plugins.Protocol.Panasonic;

namespace CommunicationKernel.Tests;

[TestClass]
public class MewtocolResponseBccTests {

    /// <summary>按站号 1 造一个 MEWTOCOL 驱动。</summary>
    private static IProtocolDriver Driver() =>
        new MewtocolProtocolDriverFactory().CreateDriver(
            new ProtocolDriverContext { Station = "1" });

    /// <summary>对给定文本求 MEWTOCOL BCC（含帧头 '%'，与协议一致）。</summary>
    private static string Bcc(string checkedPart) {
        byte x = 0;
        foreach (char c in checkedPart) x ^= (byte)c;
        return x.ToString("X2");
    }

    /// <summary>拼一个带正确 BCC 的完整响应帧。</summary>
    /// <param name="body">从 '%' 起、到 BCC 之前的部分。</param>
    private static byte[] FrameWithGoodBcc(string body) =>
        Encoding.ASCII.GetBytes(body + Bcc(body) + "\r");

    /// <summary>拼一个 BCC 被写错的响应帧（模拟线路干扰）。</summary>
    private static byte[] FrameWithBadBcc(string body) {
        string good = Bcc(body);
        // 翻转一位得到一个必然不同、但仍是合法十六进制的 BCC
        string bad = ((byte)(Convert.ToByte(good, 16) ^ 0x01)).ToString("X2");
        return Encoding.ASCII.GetBytes(body + bad + "\r");
    }

    /// <summary>读 DT100 一个字，响应由替身直接给出。</summary>
    private static Task<OperationResult<byte[]>> ReadWith(byte[] cannedResponse) =>
        Driver().ReadAsync(new CannedTransportClient(cannedResponse), "DT100", 2, CancellationToken.None);

    // =========================================================================
    // 正常帧必须放行
    // =========================================================================

    [TestMethod]
    public async Task GoodBcc_IsAccepted() {
        // %01$RD + 一个字的数据（低字节先出，1234 → 高低交换后为 0x3412）
        OperationResult<byte[]> r = await ReadWith(FrameWithGoodBcc("%01$RD1234"));

        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.HasCount(2, r.Value, "读 2 字节应返回 2 字节");
    }

    // =========================================================================
    // 被干扰的帧必须拒绝——这是本文件存在的理由
    // =========================================================================

    [TestMethod]
    public async Task BadBcc_IsRejected() {
        OperationResult<byte[]> r = await ReadWith(FrameWithBadBcc("%01$RD1234"));

        Assert.IsFalse(r.Success,
            "BCC 不匹配的响应必须拒绝：接受它等于把线路噪声当成真实寄存器值");
        Assert.AreEqual(KernelErrorCode.ProtocolError, r.ErrorCode);
    }

    [TestMethod]
    public async Task BadBcc_OnErrorFrame_IsAlsoRejected() {
        // 帧本身声称是错误响应（!42），但 BCC 也不对：
        // 说明整帧都不可信，不应把里面的错误码当真
        OperationResult<byte[]> r = await ReadWith(FrameWithBadBcc("%01!42"));

        Assert.IsFalse(r.Success);
        Assert.Contains("BCC", r.ErrorMessage,
            "应报成 BCC 问题，而不是把损坏帧里的错误码当成设备的真实答复");
    }

    // =========================================================================
    // 规范允许的「不校验」写法必须放行
    // =========================================================================

    [TestMethod]
    public async Task DoubleStar_MeansChecksumDisabled_IsAccepted() {
        // MEWTOCOL 允许用 ** 占位表示本帧不做校验，不能当成校验失败
        byte[] frame = Encoding.ASCII.GetBytes("%01$RD1234**\r");
        OperationResult<byte[]> r = await ReadWith(frame);

        Assert.IsTrue(r.Success, "** 表示本帧不校验，必须放行：" + r.ErrorMessage);
    }

    // =========================================================================
    // 正常的错误响应仍要能被识别
    // =========================================================================

    [TestMethod]
    public async Task GoodBcc_ErrorFrame_ReportsDeviceError() {
        // BCC 正确的 !42 错误帧：应当报设备错误，而不是 BCC 问题
        OperationResult<byte[]> r = await ReadWith(FrameWithGoodBcc("%01!42"));

        Assert.IsFalse(r.Success);
        Assert.Contains("42", r.ErrorMessage, "应把设备返回的错误码 42 报上来");
        Assert.DoesNotContain("BCC 校验失败", r.ErrorMessage,
            "校验和是对的，不该报成 BCC 问题");
    }

    // =========================================================================
    // 替身
    // =========================================================================

    /// <summary>只回放一段预置响应的传输替身，不涉及真实 I/O。</summary>
    private sealed class CannedTransportClient : ITransportClient {
        private readonly byte[] _response;

        public CannedTransportClient(byte[] response) => _response = response;

        public string TransportId => "canned";
        public TransportKind Kind => TransportKind.Custom;
        public bool IsConnectionAlive => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        /// <summary>直接返回预置字节，跳过分帧——本测试要验的是分帧之后的解析。</summary>
        public Task<OperationResult<byte[]>> SendAndReceiveAsync(
            byte[] request, TryGetFrameLength tryGetFrameLength, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult<byte[]>.Ok(_response));
    }
}
