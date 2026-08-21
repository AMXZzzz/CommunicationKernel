#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/MesMonitor/DataMonitorPage.xaml.cs
// 层级: UI 层 — MES 数据监控页 code-behind
// 作用: 接受 DataMonitorViewModel 注入，设置 DataContext，
//       串联告警面板弹窗的显示/隐藏切换逻辑（无业务代码）。
// 调用链:
//   App.xaml.cs → DI → DataMonitorPage(DataMonitorViewModel)
//     → DataContext = vm
//       → XAML ItemsControl ← MonitoredDevices（自动绑定刷新）
//     → lineControl.AlarmClickedEvent → alarmPopup 显示 → alarmPanel/alarmDetail 切换
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using CommunicationKernel.UI.Wpf.ViewModels;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor {

    /// <summary>
    /// MES 数据监控首页 code-behind。
    /// 设备卡片由 <see cref="DataMonitorViewModel.MonitoredDevices"/> 动态驱动，
    /// 不再包含任何硬编码设备数据。
    /// </summary>
    public partial class DataMonitorPage : Page {

        // -------------------------------------------------------------------------
        // 私有字段
        // -------------------------------------------------------------------------

        /// <summary>页面 ViewModel，持有实时设备状态集合。</summary>
        private readonly DataMonitorViewModel _vm;

        // -------------------------------------------------------------------------
        // 构造函数
        // -------------------------------------------------------------------------

        /// <param name="viewModel">由 DI 注入的 MES 监控 ViewModel。</param>
        public DataMonitorPage (DataMonitorViewModel viewModel) {
            // 保存 ViewModel 引用
            _vm = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));

            // 初始化 XAML 视觉树
            InitializeComponent();

            // 将 ViewModel 挂载到 DataContext，驱动 ItemsControl 等绑定控件
            DataContext = _vm;

            // 串联告警面板弹窗事件
            WireAlarmEvents();
        }

        // -------------------------------------------------------------------------
        // 告警弹窗事件串联
        // -------------------------------------------------------------------------

        /// <summary>
        /// 将产线控制条（lineControl）与告警弹窗（alarmPopup）的显示/关闭、
        /// 以及告警列表（alarmPanel）与详情（alarmDetail）的切换事件全部串联。
        /// </summary>
        private void WireAlarmEvents () {
            // 点击产线控制条的告警入口：打开告警弹窗，默认显示列表面板
            lineControl.AlarmClickedEvent += () => {
                alarmPanel.Visibility  = Visibility.Visible;
                alarmDetail.Visibility = Visibility.Collapsed;

                // 将弹窗居中显示在主窗口上
                alarmPopup.PlacementTarget = Window.GetWindow(this);
                alarmPopup.Placement =
                    System.Windows.Controls.Primitives.PlacementMode.Center;
                alarmPopup.IsOpen = true;
            };

            // 列表面板关闭请求：关闭整个告警弹窗
            alarmPanel.CloseRequested += () => {
                alarmPopup.IsOpen = false;
            };

            // 列表面板查看详情：切换到详情子面板
            alarmPanel.DetailRequested += () => {
                alarmPanel.Visibility  = Visibility.Collapsed;
                alarmDetail.Visibility = Visibility.Visible;
            };

            // 详情面板返回请求：切换回列表子面板
            alarmDetail.BackRequested += () => {
                alarmDetail.Visibility = Visibility.Collapsed;
                alarmPanel.Visibility  = Visibility.Visible;
            };

            // 详情面板关闭请求：关闭整个告警弹窗
            alarmDetail.CloseRequested += () => {
                alarmPopup.IsOpen = false;
            };
        }
    }
}
