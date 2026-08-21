// 文件: Core/Interfaces/IDeviceService.cs
// 层级: UI层 — 核心接口
// 作用: 定义设备管理服务的抽象接口，供 DevicePageViewModel 依赖注入使用。
//       具体实现（GrpcDeviceService）封装 gRPC RegisterRoute / QueryRoutes / WatchRouteStatus。

using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Core.Interfaces
{
    /// <summary>
    /// 设备管理服务接口。
    /// 提供设备列表的增删改查，以及连接/断开的生命周期管理。
    /// </summary>
    public interface IDeviceService
    {
        /// <summary>
        /// 当前设备列表，ObservableCollection 会在条目增删时自动通知 WPF 列表控件刷新。
        /// 所有对此集合的修改均须在 UI 线程执行。
        /// </summary>
        ObservableCollection<DeviceInfo> Devices { get; }

        /// <summary>
        /// 从 gRPC 后端加载（刷新）设备列表。
        /// 调用 QueryRoutesAsync 并用返回结果重建 Devices 集合。
        /// 此方法在内部切回 UI 线程更新集合。
        /// </summary>
        void Load();

        /// <summary>
        /// 添加新设备并向 gRPC 后端注册路由。
        /// 完成后自动调用 Load() 刷新列表。
        /// </summary>
        /// <param name="info">包含连接参数的设备信息对象。</param>
        void Add(DeviceInfo info);

        /// <summary>
        /// 更新已有设备（重新注册路由以应用新参数）。
        /// 完成后自动调用 Load() 刷新列表。
        /// </summary>
        /// <param name="info">已修改的设备信息对象，Id 须与现有设备匹配。</param>
        void Update(DeviceInfo info);

        /// <summary>
        /// 从本地 Devices 集合中移除指定设备。
        /// 注意：当前 gRPC API 无删除路由接口，仅从本地列表移除。
        /// </summary>
        /// <param name="id">要移除的设备路由 ID。</param>
        void Remove(string id);

        /// <summary>
        /// 启动对指定设备的状态监听，异步更新 DeviceInfo 的连接状态。
        /// 内部启动 WatchRouteStatus 流，状态变更时更新对应 DeviceInfo 的属性。
        /// </summary>
        /// <param name="id">目标设备的路由 ID。</param>
        /// <param name="ct">取消令牌，取消后停止监听。</param>
        Task ConnectAsync(string id, CancellationToken ct);

        /// <summary>
        /// 断开对指定设备的状态监听，将设备状态置为离线。
        /// </summary>
        /// <param name="id">目标设备的路由 ID。</param>
        void Disconnect(string id);
    }
}
