// -----------------------------------------------------------------------------
// 文件: RouteKey.cs
// 层级: Core.EngineRouter / Models
// 作用: 路由唯一键——协议 + 介质 + 地址 + 端口 + 站号，标识一条物理连接。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Transport.Abstractions;

namespace CommunicationKernel.Core.EngineRouter.Models;

// 同一物理设备只允许一条路由；键冲突时注册被拒绝，避免两套 I/O 争用同一 socket/串口
//
// 相等性由 record struct 自动生成，逐字段比较这 5 个成员，与下面 ToString() 的
// 文本形式无关。换言之：本类型从不依赖任何分隔符来保证唯一性，
// 也从不以字符串形式充当字典键——查找一律用 RouteKey 值本身。
public readonly record struct RouteKey(
    string ProtocolId,
    TransportKind TransportKind,
    string Address,
    int Port,
    string? Station) {

    // 仅供日志与诊断阅读，不参与相等性比较，也不作为任何集合的键。
    // 用 '|' 分隔纯粹是为了让日志一眼能断出字段边界。
    public override string ToString() =>
        $"{ProtocolId}|{TransportKind}|{Address}:{Port}|{Station}";
}
