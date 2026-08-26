// -----------------------------------------------------------------------------
// 文件: ReadRequestKey.cs
// 层级: Core.EngineRouter / Models
// 作用: 读合并键——同一 (路由, 地址, 长度) 的并发读合成一次 PLC I/O。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.EngineRouter.Models;

// 不同 Length 视为独立请求：跨长度合并需剪切响应字节，与当前轮询场景不符
public readonly record struct ReadRequestKey(
    RouteKey RouteKey,
    string Address,
    int Length);
