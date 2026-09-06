#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Models/DeviceServiceEvents.cs
// 层级: UI 层 — WPF 核心模型
// 作用: IDeviceService 对外发布的事件载体。
//
// 为什么需要这几个类型：
//   服务层此前直接持有 ObservableCollection<DeviceInfo>，并在内部用
//   Application.Current.Dispatcher 切回 UI 线程改集合。那样做有三个问题：
//     1. 违反「服务只发布事件、切线程是订阅方的责任」——服务被钉死在 WPF 上，
//        无法在无 Application 的环境（单元测试、控制台宿主）里实例化；
//     2. 静默失败：Application.Current 在应用退出途中为 null，
//        InvokeAsync 那一行会被跳过，界面停在旧状态且没有任何日志；
//     3. 服务与界面共用同一批 DeviceInfo 实例，后台线程改属性、
//        UI 线程读属性，靠 WPF 的绑定层兜底，不是真的线程安全。
//
//   改为发事件后，服务只产出<b>数据</b>，由 UI 侧的 DeviceListModel
//   在 UI 线程上把它落到 ObservableCollection。
// -----------------------------------------------------------------------------

using CommunicationKernel.UI.Wpf.Core.Enums;

namespace CommunicationKernel.UI.Wpf.Core.Models
{
    /// <summary>
    /// 设备列表快照中的一项：一台设备的当前应有形态。
    /// </summary>
    /// <remarks>
    /// <see cref="Device"/> 是服务<b>新构造</b>的实例，尚未被任何绑定引用，
    /// 因此可以安全地跨线程传递。订阅方若已持有同 Id 的实例，
    /// 应把字段拷贝过去而不是替换实例——替换会打断 DataGrid 的选中项与滚动位置。
    /// </remarks>
    public sealed class DeviceListItem
    {
        /// <summary>设备的当前应有形态（新实例）。</summary>
        public DeviceInfo Device { get; set; }

        /// <summary>
        /// 宿主侧当前是否存在这条路由。
        /// </summary>
        /// <remarks>
        /// 为 false 表示「本地有配置、宿主没有」——通常是宿主重启丢了内存路由。
        /// 这种设备必须保留在界面上并标为离线，等下一次读写触发对账自动补注册；
        /// 此前的实现是直接删掉，导致宿主一重启界面上的设备全部消失，只能手工重录。
        /// <para>
        /// 订阅方据此决定要不要覆盖已有条目的连接状态：宿主侧有这条路由时，
        /// 真实状态由 WatchRouteStatus 流推送，列表刷新<b>不得</b>越权改写。
        /// </para>
        /// </remarks>
        public bool PresentOnHost { get; set; }
    }

    /// <summary>一台设备的连接状态变化。</summary>
    /// <remarks>
    /// 只带 Id 与状态，不带 DeviceInfo 引用：让订阅方自己在 UI 线程上按 Id 查找，
    /// 避免服务持有界面正在绑定的对象。
    /// </remarks>
    public sealed class DeviceStatusUpdate
    {
        /// <summary>路由 Id。</summary>
        public string RouteId { get; set; }

        /// <summary>是否已连接。</summary>
        public bool IsConnected { get; set; }

        /// <summary>供卡片显示的状态类型。</summary>
        public DeviceStatusType Status { get; set; }
    }
}
