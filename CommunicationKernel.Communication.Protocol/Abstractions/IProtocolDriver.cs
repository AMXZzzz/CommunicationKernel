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
///
/// ★ length 的单位一律是「字节」，不是寄存器、不是位、不是字。★
///
///   这是全系统唯一的读长度语义，UI 与引擎按字节传，插件自行换算到
///   本协议的计数单位：
///     · Modbus 寄存器区 → 向上取整到 16 位寄存器：quantity = (length + 1) / 2
///     · Modbus 位区     → 每字节 8 位：quantity = length * 8
///     · MEWTOCOL 字区   → 同样按 2 字节一个字换算
///     · S7              → 本就按字节寻址，直接透传
///
///   为什么要在接口上写死：曾经出现过 length==1 被当成「1 个寄存器」的实现，
///   于是上层要 1 字节、下层返回 2 字节，且各协议插件的理解还各不相同。
///   这类错位不会抛异常，只会让数值悄悄偏移一个字节。
///
///   由此推出的一条硬性约束：<b>length 为奇数时不得静默向下取整</b>。
///   要么按上述规则向上取整并把多读到的字节裁掉，要么明确报错；
///   返回比请求更少的字节属于契约违约。
///   CrossPluginLengthSemanticsTests 对所有协议插件逐一验证这两条。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IProtocolDriver {
    /// <summary>当前协议元信息。</summary>
    ProtocolMetadata Metadata { get; }

    /// <summary>
    /// 同步构建"读"请求帧（纯 CPU，无 IO）。
    /// </summary>
    /// <param name="address">协议自有的地址表达，由插件解析。</param>
    /// <param name="length">
    /// 期望读取的<b>字节</b>数，必须大于 0。
    /// 详见接口文档中关于单位的说明——这里不是寄存器数，也不是位数。
    /// </param>
    OperationResult<byte[]> BuildReadFrame(string address, int length);

    /// <summary>同步构建"写"请求帧（纯 CPU，无 IO）。</summary>
    /// <param name="address">协议自有的地址表达，由插件解析。</param>
    /// <param name="payload">待写入的原始字节，长度即写入字节数。</param>
    OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload);

    /// <summary>
    /// 执行一次完整读操作（含发送、响应解析）。
    /// </summary>
    /// <param name="length">
    /// 期望读取的<b>字节</b>数。成功时返回的数组长度必须恰好等于该值——
    /// 协议内部若因对齐多读了字节，须在返回前裁掉。
    /// </param>
    Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken);

    /// <summary>执行一次完整写操作（含发送、响应校验）。</summary>
    Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken);
}
