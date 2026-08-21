#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/MesMonitor/Controls/MesDeviceCard.xaml.cs
// 层级: UI 层 — MES 监控页子控件
// 作用: 设备状态展示卡片，提供六个 DependencyProperty 供 ItemsControl DataTemplate
//       通过数据绑定驱动，同时保留 SetInfo/SetStatus 方法以兼容直接调用场景。
// 绑定链:
//   DataMonitorViewModel.MonitoredDevices
//     → ItemsControl DataTemplate
//       → DeviceName / DeviceSub / DeviceLane / IsDualLane / DeviceStatusText / DeviceStatusKind
//         → PropertyChanged 回调 → Refresh() → txtName / statusBadge 等命名元素
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunicationKernel.UI.Wpf.Core.Enums;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor.Controls {

    /// <summary>
    /// MES 监控页设备状态卡片。
    /// 通过 <see cref="DeviceName"/>、<see cref="DeviceStatusKind"/> 等六个
    /// DependencyProperty 接受外部数据绑定，内部统一由 <see cref="Refresh"/> 刷新视觉元素。
    /// </summary>
    public partial class MesDeviceCard : UserControl {

        // =========================================================================
        // DependencyProperty 注册
        // =========================================================================

        /// <summary>设备显示名称，对应卡片头部大字。</summary>
        public static readonly DependencyProperty DeviceNameProperty =
            DependencyProperty.Register(
                nameof(DeviceName), typeof(string), typeof(MesDeviceCard),
                new PropertyMetadata(string.Empty, OnPropertyChanged));

        /// <summary>副标题，通常为「协议 · IP」格式字符串。</summary>
        public static readonly DependencyProperty DeviceSubProperty =
            DependencyProperty.Register(
                nameof(DeviceSub), typeof(string), typeof(MesDeviceCard),
                new PropertyMetadata(string.Empty, OnPropertyChanged));

        /// <summary>轨道类型显示文本，"单轨" 或 "双轨"。</summary>
        public static readonly DependencyProperty DeviceLaneProperty =
            DependencyProperty.Register(
                nameof(DeviceLane), typeof(string), typeof(MesDeviceCard),
                new PropertyMetadata("单轨", OnPropertyChanged));

        /// <summary>是否为双轨；影响轨道 Badge 配色。</summary>
        public static readonly DependencyProperty IsDualLaneProperty =
            DependencyProperty.Register(
                nameof(IsDualLane), typeof(bool), typeof(MesDeviceCard),
                new PropertyMetadata(false, OnPropertyChanged));

        /// <summary>状态徽章文字，如 "RUN"、"ALARM"、"离线"。</summary>
        public static readonly DependencyProperty DeviceStatusTextProperty =
            DependencyProperty.Register(
                nameof(DeviceStatusText), typeof(string), typeof(MesDeviceCard),
                new PropertyMetadata(string.Empty, OnPropertyChanged));

        /// <summary>状态类型枚举，决定徽章颜色。</summary>
        public static readonly DependencyProperty DeviceStatusKindProperty =
            DependencyProperty.Register(
                nameof(DeviceStatusKind), typeof(DeviceStatusType), typeof(MesDeviceCard),
                new PropertyMetadata(DeviceStatusType.Offline, OnPropertyChanged));

        // =========================================================================
        // CLR 属性包装器
        // =========================================================================

        /// <inheritdoc cref="DeviceNameProperty"/>
        public string DeviceName {
            get => (string)GetValue(DeviceNameProperty);
            set => SetValue(DeviceNameProperty, value);
        }

        /// <inheritdoc cref="DeviceSubProperty"/>
        public string DeviceSub {
            get => (string)GetValue(DeviceSubProperty);
            set => SetValue(DeviceSubProperty, value);
        }

        /// <inheritdoc cref="DeviceLaneProperty"/>
        public string DeviceLane {
            get => (string)GetValue(DeviceLaneProperty);
            set => SetValue(DeviceLaneProperty, value);
        }

        /// <inheritdoc cref="IsDualLaneProperty"/>
        public bool IsDualLane {
            get => (bool)GetValue(IsDualLaneProperty);
            set => SetValue(IsDualLaneProperty, value);
        }

        /// <inheritdoc cref="DeviceStatusTextProperty"/>
        public string DeviceStatusText {
            get => (string)GetValue(DeviceStatusTextProperty);
            set => SetValue(DeviceStatusTextProperty, value);
        }

        /// <inheritdoc cref="DeviceStatusKindProperty"/>
        public DeviceStatusType DeviceStatusKind {
            get => (DeviceStatusType)GetValue(DeviceStatusKindProperty);
            set => SetValue(DeviceStatusKindProperty, value);
        }

        // =========================================================================
        // 构造函数
        // =========================================================================

        /// <summary>初始化卡片控件并完成 XAML 解析。</summary>
        public MesDeviceCard () {
            // 完成 XAML 视觉树初始化
            InitializeComponent();
        }

        // =========================================================================
        // DependencyProperty 变更回调
        // =========================================================================

        /// <summary>
        /// 任一 DependencyProperty 变更时的统一回调。
        /// 将更新委托给实例方法 <see cref="Refresh"/> 以访问命名元素。
        /// </summary>
        /// <param name="d">变更的控件实例。</param>
        /// <param name="e">变更事件参数（此处不使用）。</param>
        private static void OnPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e) {
            // 强转为 MesDeviceCard 并触发视觉刷新
            ((MesDeviceCard)d).Refresh();
        }

        // =========================================================================
        // 视觉刷新
        // =========================================================================

        /// <summary>
        /// 读取当前所有 DependencyProperty 值，更新卡片内全部命名视觉元素。
        /// 在 XAML 完成解析（InitializeComponent）后才能安全访问命名元素；
        /// 属性在构造前被赋值时命名元素为 null，此时静默跳过。
        /// </summary>
        private void Refresh () {
            // ── 名称 / 副标题 ─────────────────────────────────────────────
            if (txtName != null)
                txtName.Text = DeviceName ?? string.Empty;

            if (txtSub != null)
                txtSub.Text = DeviceSub ?? string.Empty;

            // ── 轨道 Badge ────────────────────────────────────────────────
            if (txtLane != null)
                txtLane.Text = DeviceLane ?? string.Empty;

            if (laneBadge != null) {
                // 双轨：高亮蓝底；单轨：默认色
                if (IsDualLane) {
                    laneBadge.Background = (Brush)FindResource("SF.Brush.Mes.TagDualBg");
                    if (txtLane != null)
                        txtLane.Foreground = (Brush)FindResource("SF.Brush.Mes.WidthTitle");
                } else {
                    laneBadge.Background = (Brush)FindResource("SF.Brush.Mes.TagSingleBg");
                    if (txtLane != null)
                        txtLane.Foreground = (Brush)FindResource("SF.Brush.Mes.TagSingleFg");
                }
            }

            // ── 状态 Badge ────────────────────────────────────────────────
            if (txtStatus != null)
                // 优先显示 DeviceStatusText；空时按枚举生成默认文字
                txtStatus.Text = string.IsNullOrEmpty(DeviceStatusText)
                    ? GetDefaultStatusText(DeviceStatusKind)
                    : DeviceStatusText;

            if (statusBadge != null && txtStatus != null) {
                // 根据状态类型枚举选择徽章配色
                switch (DeviceStatusKind) {
                    case DeviceStatusType.Success:
                    case DeviceStatusType.Connecting:
                        // 运行中 / 连接中：绿色活跃色
                        statusBadge.Background = (Brush)FindResource("SF.Brush.Mes.LiveBg");
                        txtStatus.Foreground   = (Brush)FindResource("SF.Brush.Mes.LiveFg");
                        break;
                    case DeviceStatusType.Warning:
                    case DeviceStatusType.Error:
                        // 告警 / 错误：红色告警色
                        statusBadge.Background = (Brush)FindResource("SF.Brush.Mes.AlarmBg");
                        txtStatus.Foreground   = (Brush)FindResource("SF.Brush.Mes.AlarmFg");
                        break;
                    default:
                        // Offline 及未知：灰色离线色
                        statusBadge.Background = (Brush)FindResource("SF.Brush.Bg.Hover");
                        txtStatus.Foreground   = (Brush)FindResource("SF.Brush.Text.Secondary");
                        break;
                }
            }
        }

        // =========================================================================
        // 兼容方法（直接调用场景）
        // =========================================================================

        /// <summary>
        /// 设置基本信息（兼容旧代码直接调用场景）。
        /// 内部写入对应 DependencyProperty，触发 <see cref="Refresh"/>。
        /// </summary>
        /// <param name="name">设备名称。</param>
        /// <param name="sub">副标题（型号/IP 等）。</param>
        /// <param name="lane">轨道标签文字，如 "单轨"/"双轨"。</param>
        /// <param name="isDual">是否双轨。</param>
        public void SetInfo (string name, string sub, string lane, bool isDual) {
            // 逐一写入 DP，PropertyChanged 回调会触发 Refresh
            DeviceName  = name  ?? string.Empty;
            DeviceSub   = sub   ?? string.Empty;
            DeviceLane  = lane  ?? string.Empty;
            IsDualLane  = isDual;
        }

        /// <summary>
        /// 设置状态徽章（兼容旧代码直接调用场景）。
        /// statusType 字符串映射到 <see cref="DeviceStatusType"/> 枚举。
        /// </summary>
        /// <param name="statusText">徽章显示文字。</param>
        /// <param name="statusType">"Success" / "Error" / 其他（离线）。</param>
        public void SetStatus (string statusText, string statusType) {
            // 文字写入 DP
            DeviceStatusText = statusText ?? string.Empty;

            // 将字符串映射为枚举值
            switch (statusType) {
                case "Success":
                    DeviceStatusKind = DeviceStatusType.Success;
                    break;
                case "Error":
                    DeviceStatusKind = DeviceStatusType.Error;
                    break;
                default:
                    // 未知状态默认为离线
                    DeviceStatusKind = DeviceStatusType.Offline;
                    break;
            }
        }

        // =========================================================================
        // 私有辅助
        // =========================================================================

        /// <summary>
        /// 根据状态枚举返回默认显示文字（DeviceStatusText 为空时使用）。
        /// </summary>
        /// <param name="kind">状态枚举值。</param>
        /// <returns>对应的中文状态文字。</returns>
        private static string GetDefaultStatusText (DeviceStatusType kind) {
            switch (kind) {
                case DeviceStatusType.Success:    return "RUN";
                case DeviceStatusType.Connecting: return "连接中";
                case DeviceStatusType.Warning:    return "警告";
                case DeviceStatusType.Error:      return "ALARM";
                default:                          return "离线";
            }
        }
    }
}
