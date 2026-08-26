#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Interfaces/IRouteReconciler.cs
// 层级: UI 层 — WPF 核心接口
// 作用: 按需把本地设备配置重新推给 EngineHostingServiceApp，使丢失的内存路由恢复可用。
// -----------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace CommunicationKernel.UI.Wpf.Core.Interfaces
{
    /// <summary>
    /// 路由对账器：确保某条路由在 EngineHostingServiceApp 侧确实存在。
    /// </summary>
    /// <remarks>
    /// <para>
    /// EngineHostingServiceApp 的路由是纯内存对象，进程重启即全部丢失。此时上位机侧
    /// 所有读写都会收到 <c>RouteNotFound</c>，并且不会自行恢复——
    /// 轮询循环只会一直退避重试同一个必然失败的请求。
    /// </para>
    /// <para>
    /// 实现方须自行处理两件事，否则会比不重注册更糟：
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>并发合并</b>——一台设备上通常挂着几十个变量，宿主重启会让它们
    ///     在同一瞬间全部收到 RouteNotFound。若逐个发起注册，宿主会在一秒内
    ///     收到几十次同一条路由的 RegisterRoute。
    ///   </item>
    ///   <item>
    ///     <b>重试节流</b>——PLC 拔线时重注册同样会失败。没有最小间隔的话，
    ///     每个轮询周期都会再打一次，把失败放大成持续的请求风暴。
    ///   </item>
    /// </list>
    /// </remarks>
    public interface IRouteReconciler
    {
        /// <summary>
        /// 确保指定路由在 EngineHostingServiceApp 侧存在，必要时用本地留存的配置重新注册。
        /// </summary>
        /// <param name="routeId">路由 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>
        /// 路由现已可用返回 true；本地没有该设备的配置、处于节流窗口内、
        /// 或重新注册失败均返回 false。
        /// </returns>
        Task<bool> EnsureRouteAsync(string routeId, CancellationToken ct);
    }
}
