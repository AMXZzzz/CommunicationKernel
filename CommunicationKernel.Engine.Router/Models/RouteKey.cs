// -----------------------------------------------------------------------------
// 文件: RouteKey.cs
// 层级: Engine.Router / Models
// 作用: 路由唯一键——协议 + 介质 + 地址 + 端口 + 站号，标识一条物理连接。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Transport.Abstractions;

namespace CommunicationKernel.Engine.Router.Models;

// 同一物理设备只允许一条路由；键冲突时注册被拒绝，避免两套 I/O 争用同一 socket/串口
public readonly record struct RouteKey(
    string ProtocolId,
    TransportKind TransportKind,
    string Address,
    int Port,
    string? Station) {

    // 日志与诊断用的稳定文本；'|' 为分隔符，输入字段不得包含该字符
    public override string ToString() =>
        $"{ProtocolId}|{TransportKind}|{Address}:{Port}|{Station}";
}
