#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/MesMonitor/Controls/MesAlarmPanel.xaml.cs
// 层级: UI 层 — MES 监控页子控件
// 作用: 告警列表面板；关闭/查看详情通过事件交给 DataMonitorPage。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor.Controls {
    /// <summary>
    /// 告警列表面板（MesAlarmPanel.xaml 的交互逻辑）。
    /// 作为告警弹窗的默认子面板，通过 CloseRequested/DetailRequested 事件将关闭/查看详情的意图
    /// 交给宿主（DataMonitorPage）处理页面切换。
    /// </summary>
    public partial class MesAlarmPanel : UserControl {
        public MesAlarmPanel () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        /// <summary>请求关闭整个告警弹窗。</summary>
        public event Action CloseRequested;

        /// <summary>关闭按钮点击：转发 CloseRequested 事件。</summary>
        private void BtnClose_Click (object sender, RoutedEventArgs e) {
            CloseRequested?.Invoke();
        }

        /// <summary>请求切换到告警详情子面板。</summary>
        public event Action DetailRequested;

        /// <summary>查看详情按钮点击：转发 DetailRequested 事件。</summary>
        private void BtnDetail_Click (object sender, RoutedEventArgs e) {
            DetailRequested?.Invoke();
        }
    }

}
