#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Interfaces/IDeviceService.cs
// 层级: UI 层 — WPF 核心接口
// 作用: 设备管理抽象；实现封装 gRPC 路由生命周期，只发布事件，不碰界面。
//
// 本接口<b>不暴露 ObservableCollection</b>，也不承诺任何线程。
//   此前 Devices 是 ObservableCollection<DeviceInfo>，服务内部用
//   Application.Current.Dispatcher 把修改切回 UI 线程。那等于把 WPF 的
//   线程模型焊死在服务层：服务离开 WPF 进程就无法实例化，
//   且 Application.Current 在退出途中为 null 时会静默丢弃更新。
//
//   现在的分工是：
//     服务  —— 只产出数据（DeviceListItem / DeviceStatusUpdate），在任意线程发事件；
//     订阅方 —— 负责切到 UI 线程并落到自己的 ObservableCollection。
//   见 ViewModels/DeviceListModel.cs，那是本项目唯一的订阅实现。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Core.Interfaces
{
    /// <summary>
    /// 设备管理服务接口。
    /// 提供设备的增删改、连接/断开生命周期，并以事件发布列表与状态变化。
    /// </summary>
    /// <remarks>
    /// <b>所有事件都可能在后台线程触发。</b>订阅方若要更新界面，
    /// 必须自行切回 UI 线程——这是刻意的：切线程属于呈现层职责，
    /// 放在服务里会让服务无法脱离 WPF 使用，也无法被单元测试覆盖。
    /// </remarks>
    public interface IDeviceService
    {
        /// <summary>
        /// 后台操作（注册 / 更新 / 删除路由）失败时触发，参数为面向操作员的错误描述。
        /// </summary>
        /// <remarks>
        /// Add / Update / Remove 都是即发即忘的异步操作，失败无法通过返回值感知——
        /// 订阅此事件是唯一能让用户看到失败原因的途径。
        /// <b>在后台线程触发</b>，订阅方需自行切回 UI 线程再弹提示。
        /// </remarks>
        event Action<string> OperationFailed;

        /// <summary>
        /// 设备列表整体刷新：参数是「当前应有的设备集合」。
        /// </summary>
        /// <remarks>
        /// 由 <see cref="Load"/> 与宿主对账后触发。订阅方应按 Id 与自己现有的集合做增量比对：
        /// 存在则拷贝元数据、不存在则新增、参数里没有的则移除。
        /// 直接整表替换会打断 DataGrid 的选中项与滚动位置。
        /// </remarks>
        event Action<IReadOnlyList<DeviceListItem>> DevicesChanged;

        /// <summary>单台设备加入或更新（不影响列表其余部分）。</summary>
        /// <remarks>
        /// 用于「配置已保存、但宿主侧注册失败」这一路径：设备必须当场出现在界面上，
        /// 否则操作员看到「什么都没发生」，只会以为没保存成功而反复重录同一台设备。
        /// </remarks>
        event Action<DeviceListItem> DeviceUpserted;

        /// <summary>单台设备已从宿主与本地配置中删除，参数为路由 Id。</summary>
        event Action<string> DeviceRemoved;

        /// <summary>单台设备的连接状态变化，由 WatchRouteStatus 流驱动。</summary>
        event Action<DeviceStatusUpdate> DeviceStatusChanged;

        /// <summary>
        /// 取当前设备列表快照。
        /// </summary>
        /// <remarks>
        /// 供订阅方在订阅之前做一次初始填充，避免错过订阅前已发生的变化。
        /// 返回的是新构造的实例，调用方可以自由持有与修改。
        /// </remarks>
        IReadOnlyList<DeviceListItem> Snapshot();

        /// <summary>
        /// 从 gRPC 后端拉取路由列表，与本地配置合并后经
        /// <see cref="DevicesChanged"/> 发布。立即返回，实际工作在后台线程。
        /// </summary>
        void Load();

        /// <summary>
        /// 添加新设备并向 gRPC 后端注册路由，成功后自动触发一次 <see cref="Load"/>。
        /// </summary>
        /// <param name="info">包含连接参数的设备信息对象。</param>
        void Add(DeviceInfo info);

        /// <summary>
        /// 更新已有设备（先注销再注册以应用新参数），成功后自动触发一次 <see cref="Load"/>。
        /// </summary>
        /// <param name="info">已修改的设备信息对象，Id 须与现有设备匹配。</param>
        void Update(DeviceInfo info);

        /// <summary>
        /// 删除设备：注销宿主侧路由、清除本地配置，随后触发 <see cref="DeviceRemoved"/>。
        /// </summary>
        /// <remarks>宿主侧注销失败时不会删除本地条目，两侧状态不允许分叉。</remarks>
        /// <param name="id">要移除的设备路由 ID。</param>
        void Remove(string id);

        /// <summary>
        /// 启动对指定设备的状态监听，状态变化经 <see cref="DeviceStatusChanged"/> 发布。
        /// </summary>
        /// <param name="id">目标设备的路由 ID。</param>
        /// <param name="ct">取消令牌，取消后停止监听。</param>
        Task ConnectAsync(string id, CancellationToken ct);

        /// <summary>断开状态监听，并发布一次离线状态。</summary>
        /// <param name="id">目标设备的路由 ID。</param>
        void Disconnect(string id);
    }
}
