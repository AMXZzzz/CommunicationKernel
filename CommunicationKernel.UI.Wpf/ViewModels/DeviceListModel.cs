#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/DeviceListModel.cs
// 层级: UI 层 — WPF 呈现层模型
// 作用: 订阅 IDeviceService 的事件，在 UI 线程上维护供界面绑定的设备集合。
//
// 这是本项目<b>唯一</b>允许为设备列表调用 Dispatcher 的地方。
//   纪律 4：服务只发布事件，切线程是订阅方的责任。
//   服务层（GrpcDeviceService）此前自己持有 ObservableCollection 并在 9 处
//   调 Application.Current.Dispatcher，那让服务离开 WPF 就无法实例化，
//   而且 Application.Current 在退出途中为 null 时会静默丢弃更新。
//
// 为什么是"模型"而不是 ViewModel：
//   它不面向某一个页面，而是被设备页、MES 监控页、变量页三处共享的一份
//   设备列表状态。做成单例注入，三个 ViewModel 订阅同一个集合，
//   避免各自维护一份而出现"设备页显示 5 台、变量页只有 3 台"。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.ViewModels
{
    /// <summary>
    /// 供界面绑定的设备集合，内容由 <see cref="IDeviceService"/> 的事件驱动。
    /// </summary>
    public sealed class DeviceListModel
    {
        /// <summary>设备服务，事件来源。</summary>
        private readonly IDeviceService _service;

        /// <summary>
        /// 当前设备列表。<b>只允许在 UI 线程上读写。</b>
        /// </summary>
        /// <remarks>
        /// ObservableCollection 的 CollectionChanged 若在非 UI 线程触发，
        /// WPF 的 CollectionView 会抛 NotSupportedException（而且是在绑定层深处抛，
        /// 堆栈里看不到是哪次修改导致的）。本类的所有写入都经 <see cref="OnUi"/>。
        /// </remarks>
        public ObservableCollection<DeviceInfo> Devices { get; }
            = new ObservableCollection<DeviceInfo>();

        /// <summary>
        /// 后台操作失败，已切到 UI 线程，订阅方可直接弹框。
        /// </summary>
        public event Action<string> OperationFailed;

        /// <param name="service">设备服务。</param>
        public DeviceListModel(IDeviceService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            // 先用快照做初始填充，再订阅事件。
            //
            // 顺序不能反：宿主没起来时 Load() 会失败且不发事件，
            // 若等事件才填列表，界面就是空的——操作员看不出
            // 「设备还在、只是宿主连不上」，只会以为配置丢了。
            //
            // 构造发生在 DI 建容器时（UI 线程），可直接写集合
            foreach (DeviceListItem item in _service.Snapshot())
                Devices.Add(item.Device);

            _service.DevicesChanged      += OnDevicesChanged;
            _service.DeviceUpserted      += OnDeviceUpserted;
            _service.DeviceRemoved       += OnDeviceRemoved;
            _service.DeviceStatusChanged += OnStatusChanged;
            _service.OperationFailed     += OnOperationFailed;
        }

        // =====================================================================
        // 事件处理：一律先切 UI 线程，再动集合
        // =====================================================================

        /// <summary>整表刷新：按 Id 做增量比对，不整体替换。</summary>
        /// <remarks>
        /// 增量而非 Clear + AddRange，是因为整体替换会打断 DataGrid 的选中项、
        /// 滚动位置与展开状态；操作员正在看某台设备时列表一刷新就跳回顶部。
        /// </remarks>
        private void OnDevicesChanged(IReadOnlyList<DeviceListItem> items)
        {
            if (items == null) return;

            OnUi(() =>
            {
                var wanted = new HashSet<string>(
                    items.Where(i => i?.Device?.Id != null).Select(i => i.Device.Id),
                    StringComparer.OrdinalIgnoreCase);

                // 1. 移除快照里没有的：两个来源都查不到，属于已失效的临时条目
                for (int i = Devices.Count - 1; i >= 0; i--)
                {
                    if (!wanted.Contains(Devices[i].Id))
                        Devices.RemoveAt(i);
                }

                // 2. 更新或新增
                foreach (DeviceListItem item in items)
                {
                    if (item?.Device?.Id == null) continue;
                    ApplyItem_OnUi(item);
                }
            });
        }

        /// <summary>单台加入或更新，不影响列表其余部分。</summary>
        private void OnDeviceUpserted(DeviceListItem item)
        {
            if (item?.Device?.Id == null) return;
            OnUi(() => ApplyItem_OnUi(item));
        }

        /// <summary>单台移除。</summary>
        private void OnDeviceRemoved(string routeId)
        {
            if (string.IsNullOrWhiteSpace(routeId)) return;

            OnUi(() =>
            {
                DeviceInfo target = Find_OnUi(routeId);
                if (target != null) Devices.Remove(target);
            });
        }

        /// <summary>连接状态变化。</summary>
        private void OnStatusChanged(DeviceStatusUpdate update)
        {
            if (update?.RouteId == null) return;

            OnUi(() =>
            {
                // 设备可能已被删除——状态流的最后一条常常晚于删除动作到达
                DeviceInfo dev = Find_OnUi(update.RouteId);
                if (dev == null) return;

                dev.IsConnected = update.IsConnected;
                dev.StatusType  = update.Status;
            });
        }

        /// <summary>把服务层的失败提示切到 UI 线程后再转发。</summary>
        private void OnOperationFailed(string message)
            => OnUi(() => OperationFailed?.Invoke(message));

        // =====================================================================
        // 私有辅助（均须在 UI 线程调用，故以 _OnUi 结尾）
        // =====================================================================

        /// <summary>把一项快照落到集合：已存在则拷字段，不存在则新增。</summary>
        /// <remarks>
        /// 已存在时<b>拷字段而非替换实例</b>：替换会让所有指向旧实例的绑定失效，
        /// 表现是卡片闪一下、选中项丢失。
        /// </remarks>
        private void ApplyItem_OnUi(DeviceListItem item)
        {
            DeviceInfo incoming = item.Device;
            DeviceInfo existing = Find_OnUi(incoming.Id);

            if (existing == null)
            {
                Devices.Add(incoming);
                return;
            }

            // 元数据以快照为准
            existing.Name              = incoming.Name;
            existing.Model             = incoming.Model;
            existing.IsDualLane        = incoming.IsDualLane;
            existing.Protocol          = incoming.Protocol;
            existing.Ip                = incoming.Ip;
            existing.Port              = incoming.Port;
            existing.Station           = incoming.Station;
            existing.StationNo         = incoming.StationNo;
            existing.SerialPort        = incoming.SerialPort;
            existing.BaudRate          = incoming.BaudRate;
            existing.TransportKind     = incoming.TransportKind;
            existing.MinIoIntervalMs   = incoming.MinIoIntervalMs;
            existing.ByteOrder         = incoming.ByteOrder;
            existing.ExtraSettingsJson = incoming.ExtraSettingsJson;

            // 连接状态只在「宿主侧已经没有这条路由」时才由列表刷新改写。
            //
            // 宿主侧仍有路由时，真实状态由 WatchRouteStatus 流推送，
            // 列表刷新越权改写会把一台正在正常通讯的设备显示成离线，
            // 直到下一个状态事件才恢复——现场看到的是绿灯无故闪断。
            if (!item.PresentOnHost)
            {
                existing.IsConnected = false;
                existing.StatusType  = Core.Enums.DeviceStatusType.Offline;
            }
        }

        /// <summary>按路由 Id 查找；未找到返回 null（可能刚被删除）。</summary>
        private DeviceInfo Find_OnUi(string id)
        {
            foreach (DeviceInfo d in Devices)
            {
                if (d != null && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        /// <summary>
        /// 把动作放到 UI 线程执行；已在 UI 线程则就地执行。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 就地执行这一支是必要的：构造与部分同步路径本就在 UI 线程上，
        /// 一律 InvokeAsync 会把修改推迟到下一轮消息循环，
        /// 调用方紧接着读集合就会读到旧值。
        /// </para>
        /// <para>
        /// Application.Current 在应用退出途中变为 null，此时静默丢弃是正确的：
        /// 界面都没了，更新它没有意义。这与服务层此前的静默丢弃不同——
        /// 那里丢的是错误提示与状态，属于真丢数据。
        /// </para>
        /// </remarks>
        private static void OnUi(Action action)
        {
            Application app = Application.Current;
            if (app == null) return;

            Dispatcher dispatcher = app.Dispatcher;
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.InvokeAsync(action);
        }
    }
}
