// -----------------------------------------------------------------------------
// 文件: S7Limits.cs
// 层级: Plugins / Siemens.S7 / Internal
// 作用: S7 单帧容量上限，供构帧前的长度校验使用。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Plugins.Siemens.S7.Internal;

/// <summary>S7 协议的单帧容量上限。</summary>
internal static class S7Limits {

    /// <summary>
    /// 握手时向 PLC 申请的 PDU 长度（字节）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="S7Frame"/> 的 Setup Communication 帧中写死的 0x01E0 一致。
    /// 两处必须同步修改，否则校验会放行 PLC 实际承载不了的请求。
    /// </remarks>
    internal const int NegotiatedPduBytes = 480;

    /// <summary>
    /// 单次读写的最大有效载荷（字节）。
    /// </summary>
    /// <remarks>
    /// PDU 里除数据外还要装 S7 头（10 字节）与读响应的 Item 头（4 字节）等，
    /// 合计约 18 字节；余下才是数据。取保守值而非贴边，
    /// 因为不同固件的开销略有出入。
    ///
    /// 校验的真正意义不在于精确贴合 PLC 能力，而在于拦住溢出：
    /// Item 的 Length 字段只有 16 位，请求 100000 字节会被静默截断成 34464，
    /// 帧结构完全合法，却读回一段与请求无关的数据。
    /// </remarks>
    internal const int MaxPayloadBytes = NegotiatedPduBytes - 18;
}
