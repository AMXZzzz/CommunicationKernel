#nullable disable

// -----------------------------------------------------------------------------
// 文件: IHostClient.cs
// 层级: 客户端层 — 抽象
// 作用: Host.App 访问契约，隔离 UI 与具体传输实现。
//
// 为什么要这层接口：
//   1) UI 的 ViewModel 与服务此前直接依赖具体类，无法在不起 gRPC 服务端的
//      前提下做任何测试。
//   2) 树莓派场景里上位机会改为进程内直连内核（SDK 形态），
//      届时换一个实现即可，UI 代码不动。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicationKernel.Host.Sdk
{
    /// <summary>
    /// Host.App 客户端契约。
    /// </summary>
    /// <remarks>
    /// 所有方法都以「返回结果」而非「抛异常」表达业务失败：
    /// 传输异常在实现内部转换为 <c>RPC_ERROR</c> / <c>TIMEOUT</c> 等错误码，
    /// 调用方无需为每次读写包 try/catch。
    /// </remarks>
    public interface IHostClient : IAsyncDisposable
    {
        // ============================================================================
        // 健康检查
        // ============================================================================

        /// <summary>健康检查。网络异常时返回 (false, "", 0) 而非抛出。</summary>
        Task<(bool Ok, string HostVersion, int RouteCount)> HealthAsync(CancellationToken ct = default);

        // ============================================================================
        // 路由生命周期
        // ============================================================================

        /// <summary>注册一条路由。</summary>
        Task<(bool Success, string ErrorCode, string ErrorMessage, string RouteId)> RegisterRouteAsync(
            string routeId,
            string protocolId,
            string transportKind,
            string address,
            int    port,
            string station,
            string serialPort      = "",
            int    baudRate        = 0,
            int    minIoIntervalMs = 100,
            CancellationToken ct   = default);

        /// <summary>注销一条路由。</summary>
        Task<(bool Success, string ErrorCode, string ErrorMessage)> RemoveRouteAsync(
            string routeId, CancellationToken ct = default);

        /// <summary>查询路由，所有参数均可为空字符串表示不过滤。</summary>
        Task<IReadOnlyList<RouteDto>> QueryRoutesAsync(
            string routeId       = "",
            string protocolId    = "",
            string transportKind = "",
            string address       = "",
            CancellationToken ct = default);

        // ============================================================================
        // 插件与宿主侧发现
        // ============================================================================

        /// <summary>
        /// 查询宿主当前加载的协议清单。
        /// </summary>
        /// <remarks>
        /// UI 必须以此为唯一协议来源——任何内置的协议列表都会随插件增减而失真，
        /// 且会掩盖「宿主一个协议都没加载」这类故障。
        /// </remarks>
        Task<IReadOnlyList<ProtocolDescriptorDto>> QueryProtocolsAsync(CancellationToken ct = default);

        /// <summary>
        /// 查询<b>宿主所在机器</b>上可用的串口。
        /// </summary>
        /// <remarks>
        /// 不是本机串口。宿主跑在树莓派时，操作员要选的是树莓派上的
        /// /dev/ttyUSB0，而不是自己 PC 上的 COM1。
        /// 返回空列表是正常情况（纯以太网现场），UI 应保留手工输入。
        /// </remarks>
        Task<IReadOnlyList<SerialPortDto>> QuerySerialPortsAsync(CancellationToken ct = default);

        // ============================================================================
        // 读写
        // ============================================================================

        /// <summary>按路由读取。<paramref name="length"/> 单位为字节。</summary>
        Task<ReadResultDto> ReadAsync(
            string routeId, string address, int length, CancellationToken ct = default);

        /// <summary>按路由写入。</summary>
        Task<WriteResultDto> WriteAsync(
            string routeId, string address, byte[] data, CancellationToken ct = default);

        // ============================================================================
        // 状态推流
        // ============================================================================

        /// <summary>
        /// 订阅路由状态流，断线自动重连，直到 <paramref name="ct"/> 取消。
        /// </summary>
        /// <param name="routeId">目标路由 ID；空字符串表示订阅全部路由。</param>
        /// <param name="onStatus">状态事件回调。</param>
        /// <param name="onDisconnected">流中断回调，用于把设备标记为离线；可为 null。</param>
        /// <param name="ct">取消令牌。</param>
        Task WatchRouteStatusAsync(
            string routeId,
            Func<RouteStatusDto, Task> onStatus,
            Func<Task> onDisconnected = null,
            CancellationToken ct = default);
    }
}
